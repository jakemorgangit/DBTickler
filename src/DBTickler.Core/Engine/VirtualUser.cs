using System.Diagnostics;
using DBTickler.Core.Data;
using DBTickler.Core.Logging;
using DBTickler.Core.Metrics;
using DBTickler.Core.Workloads;

namespace DBTickler.Core.Engine;

/// <summary>
/// One simulated session. A user holds a single connection and runs exactly one operation
/// at a time, so the virtual-user count really is the concurrency the server sees.
///
/// v1's worker instead fanned out <c>BatchSize / 20</c> concurrent statements from each of
/// its "threads", so a nominal 32 threads at batch size 1000 opened 1,600 concurrent
/// connections — well past the default pool limit, which then surfaced as timeouts that
/// looked like server problems.
/// </summary>
internal sealed class VirtualUser
{
    private const int MaxReconnectAttempts = 5;

    private readonly int _index;
    private readonly ISqlSessionFactory _sessionFactory;
    private readonly WorkloadPlan _plan;
    private readonly MetricsCollector _metrics;
    private readonly IRunLog _log;
    private readonly RunCoordinator _coordinator;
    private readonly OperationContext _context;
    private readonly Random _random;

    private ISqlSession? _session;

    public VirtualUser(
        int index,
        ISqlSessionFactory sessionFactory,
        WorkloadPlan plan,
        PayloadGenerator payload,
        MetricsCollector metrics,
        IRunLog log,
        RunCoordinator coordinator,
        int seed)
    {
        _index = index;
        _sessionFactory = sessionFactory;
        _plan = plan;
        _metrics = metrics;
        _log = log;
        _coordinator = coordinator;
        _random = new Random(seed);

        _context = new OperationContext
        {
            Random = _random,
            Payload = payload,
            Profile = plan.Profile,
            Schema = plan.Schema,
            UserIndex = index,
        };
    }

    public async Task RunAsync(TimeSpan startDelay, CancellationToken cancellationToken)
    {
        try
        {
            if (startDelay > TimeSpan.Zero)
                await Task.Delay(startDelay, cancellationToken).ConfigureAwait(false);

            if (!await ConnectAsync(cancellationToken).ConfigureAwait(false))
                return;

            _metrics.UserStarted();
            try
            {
                await LoopAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _metrics.UserFinished();
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on stop; the coordinator already knows why.
        }
        catch (Exception exception)
        {
            _log.Error($"User {_index} stopped unexpectedly: {exception.Message}");
        }
        finally
        {
            await DisposeSessionAsync().ConfigureAwait(false);
        }
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var operation = _plan.Next(_random, _index);
            SqlRequest request;

            try
            {
                request = operation.Build(_context);
            }
            catch (Exception exception)
            {
                // A malformed operation is a defect in the catalogue, not a server problem.
                _log.Error($"Could not build operation '{operation.Name}': {exception.Message}");
                _coordinator.ReportFatal($"operation '{operation.Name}' could not be built");
                return;
            }

            var start = Stopwatch.GetTimestamp();
            try
            {
                var result = await _session!.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
                var elapsed = ElapsedMicroseconds(start);

                _metrics.RecordSuccess(operation.Kind, elapsed, result.RowsRead + Math.Max(0, result.RowsAffected));

                if (_log.MinimumLevel <= LogLevel.Trace)
                    _log.Trace($"user {_index}: {operation.Name} in {elapsed / 1000.0:F1} ms");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                var elapsed = ElapsedMicroseconds(start);
                if (!await HandleFailureAsync(operation, exception, elapsed, cancellationToken).ConfigureAwait(false))
                    return;
            }

            if (_coordinator.ShouldStop)
                return;

            await ThinkAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Returns false when the user should stop looping.</summary>
    private async Task<bool> HandleFailureAsync(
        WorkloadOperation operation, Exception exception, long elapsedMicroseconds, CancellationToken cancellationToken)
    {
        var failure = SqlErrorClassifier.Classify(exception);
        var errorNumber = SqlErrorClassifier.ErrorNumber(exception);

        _metrics.RecordFailure(operation.Kind, elapsedMicroseconds, failure, errorNumber);

        switch (failure)
        {
            case SqlFailureKind.DeadlockVictim:
                // The headline event for a chaos run: worth surfacing, but only from one user
                // at a time or the log fills with duplicates of the same cycle.
                _log.Debug($"user {_index}: chosen as deadlock victim running '{operation.Name}'");
                break;

            case SqlFailureKind.Killed:
                _log.Debug($"user {_index}: session was killed");
                return false;

            case SqlFailureKind.Connection:
                _log.Warning($"user {_index}: connection lost ({exception.Message.Trim()}); reconnecting");
                await DisposeSessionAsync().ConfigureAwait(false);
                if (!await ConnectAsync(cancellationToken).ConfigureAwait(false))
                    return false;
                break;

            default:
                _log.Debug($"user {_index}: {operation.Name} failed — {Summarize(exception)}");
                break;
        }

        if (SqlErrorClassifier.IsFatalForRun(failure))
            _coordinator.ReportFatalCandidate(operation.Name, Summarize(exception));

        _coordinator.ReportError();
        return !_coordinator.ShouldStop;
    }

    private async Task<bool> ConnectAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaxReconnectAttempts; attempt++)
        {
            if (cancellationToken.IsCancellationRequested) return false;

            try
            {
                _session = await _sessionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception exception) when (attempt < MaxReconnectAttempts)
            {
                var backoff = TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempt - 1));
                _log.Debug($"user {_index}: connect attempt {attempt} failed ({exception.Message.Trim()}); " +
                           $"retrying in {backoff.TotalMilliseconds:F0} ms");
                try
                {
                    await Task.Delay(backoff, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
            }
            catch (Exception exception)
            {
                _log.Error($"user {_index}: could not connect after {MaxReconnectAttempts} attempts — {exception.Message.Trim()}");
                _coordinator.ReportFatal($"user {_index} could not connect");
                return false;
            }
        }

        return false;
    }

    private async Task ThinkAsync(CancellationToken cancellationToken)
    {
        var meanMs = _plan.Profile.ThinkTimeMs;
        if (meanMs <= 0) return;

        var delayMs = _plan.Profile.ThinkTimeJitter
            ? ExponentialDelay(meanMs)
            : meanMs;

        if (delayMs <= 0) return;

        try
        {
            await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stopping; the caller's loop condition handles it.
        }
    }

    /// <summary>
    /// Draws a gap from an exponential distribution with the configured mean, which produces
    /// Poisson arrivals. A fixed pause makes every user tick in lockstep and hides exactly the
    /// queueing behaviour a stress test is meant to expose.
    /// </summary>
    private int ExponentialDelay(int meanMs)
    {
        var uniform = 1.0 - _random.NextDouble(); // in (0, 1], so Log is finite.
        var delay = -Math.Log(uniform) * meanMs;
        return (int)Math.Clamp(delay, 0, meanMs * 10);
    }

    private async Task DisposeSessionAsync()
    {
        if (_session is null) return;

        try
        {
            await _session.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // A connection that fails to close cleanly is already gone; nothing to report.
        }
        finally
        {
            _session = null;
        }
    }

    private static long ElapsedMicroseconds(long startTimestamp) =>
        (long)(Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds * 1000);

    private static string Summarize(Exception exception)
    {
        var message = exception.Message.Trim().ReplaceLineEndings(" ");
        return message.Length <= 200 ? message : message[..200] + "…";
    }
}
