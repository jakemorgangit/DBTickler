using DBTickler.Core.Configuration;
using DBTickler.Core.Data;
using DBTickler.Core.Logging;
using DBTickler.Core.Metrics;
using DBTickler.Core.Workloads;

namespace DBTickler.Core.Engine;

/// <summary>
/// Runs a workload: starts the virtual users, staggers them through ramp-up, enforces the
/// duration and error budget, and produces a report.
///
/// The engine depends on <see cref="ISqlSessionFactory"/> rather than on SqlClient directly,
/// which is what makes the concurrency, ramp, cancellation and error-budget behaviour
/// testable without a database.
/// </summary>
public sealed class LoadEngine
{
    private readonly ISqlSessionFactory _sessionFactory;
    private readonly IRunLog _log;

    private RunCoordinator? _coordinator;
    private RunState _state = RunState.Idle;

    public LoadEngine(ISqlSessionFactory sessionFactory, IRunLog? log = null)
    {
        _sessionFactory = sessionFactory;
        _log = log ?? NullRunLog.Instance;
    }

    /// <summary>Metrics for the run in progress, or the most recent one.</summary>
    public MetricsCollector Metrics { get; private set; } = new();

    public RunState State
    {
        get => _state;
        private set
        {
            if (_state == value) return;
            _state = value;
            RaiseStateChanged(value);
        }
    }

    /// <summary>
    /// Notifies subscribers one at a time, containing any that throw.
    ///
    /// A run can be hours long and is often unattended; a buggy listener must not be able to
    /// end it, nor to stop the other listeners hearing about the transition. This is not
    /// hypothetical — a UI subscriber touching thread-affine state from the engine's thread
    /// throws every time, and because some transitions are raised from a fire-and-forget task
    /// the exception went unobserved and was reported as a crash.
    /// </summary>
    private void RaiseStateChanged(RunState state)
    {
        if (StateChanged is not { } handler) return;

        foreach (var subscriber in handler.GetInvocationList())
        {
            try
            {
                ((Action<RunState>)subscriber)(state);
            }
            catch (Exception exception)
            {
                _log.Error($"A run-state subscriber threw handling '{state}': {exception.Message}");
            }
        }
    }

    /// <summary>
    /// Raised on whichever thread advanced the run — a thread-pool thread once the engine's
    /// awaits stop capturing the caller's context. A UI consumer must marshal to its own
    /// thread before touching anything thread-affine.
    /// </summary>
    public event Action<RunState>? StateChanged;

    public bool IsRunning => State is RunState.Preparing or RunState.RampingUp or RunState.Running or RunState.Stopping;

    /// <summary>
    /// Signals the run to stop. Users abandon their current statement rather than finishing
    /// it, so this returns control quickly even mid-query.
    /// </summary>
    public void RequestStop()
    {
        if (_coordinator is null) return;
        State = RunState.Stopping;
        _coordinator.RequestStop(StopReason.UserRequested);
    }

    public async Task<RunReport> RunAsync(
        WorkloadPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (IsRunning)
            throw new InvalidOperationException("A run is already in progress.");

        if (!plan.IsRunnable || !plan.Diagnostics.IsValid)
        {
            var detail = plan.Diagnostics.IsValid
                ? "the workload plan contains no runnable operations"
                : plan.Diagnostics.FormatErrors();
            throw new InvalidOperationException($"Cannot start the run: {detail}");
        }

        var profile = plan.Profile;
        State = RunState.Preparing;

        Metrics = new MetricsCollector(profile.DurationSeconds);

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var coordinator = new RunCoordinator(cancellation, _log, profile.MaxErrors);
        _coordinator = coordinator;

        var seed = profile.RandomSeed ?? Random.Shared.Next();
        var payload = PayloadGenerator.ForRowBudget(profile.PayloadBytes, seed);

        _log.Info($"Starting: {profile.VirtualUsers} virtual user(s), {plan.Describe()}.");
        if (profile.RandomSeed is { } fixedSeed)
            _log.Info($"Random seed {fixedSeed} — this run is reproducible.");
        foreach (var warning in plan.Diagnostics.Warnings)
            _log.Warning(warning);

        var startedAt = DateTimeOffset.Now;
        Metrics.Start();

        // A duration of zero means "run until stopped"; anything else ends the run on its own.
        if (profile.DurationSeconds > 0)
            cancellation.CancelAfter(TimeSpan.FromSeconds(profile.DurationSeconds));

        State = profile.RampUpSeconds > 0 ? RunState.RampingUp : RunState.Running;

        var users = new Task[profile.VirtualUsers];
        for (var index = 0; index < profile.VirtualUsers; index++)
        {
            var user = new VirtualUser(
                index,
                _sessionFactory,
                plan,
                payload,
                Metrics,
                _log,
                coordinator,
                // Each user gets a distinct but deterministic seed, so a fixed profile seed
                // reproduces the whole run rather than just one user's choices.
                unchecked(seed + index * 7919));

            users[index] = user.RunAsync(RampDelayFor(index, profile), cancellation.Token);
        }

        if (State == RunState.RampingUp)
            _ = MarkRunningAfterRampAsync(profile.RampUpSeconds, cancellation.Token);

        try
        {
            await Task.WhenAll(users).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Individual users already handle their own cancellation; this only fires if one
            // propagated, which is not an error condition for the run as a whole.
        }
        finally
        {
            Metrics.Stop();
            _coordinator = null;
        }

        var reason = ResolveStopReason(coordinator, profile, cancellationToken);
        State = reason == StopReason.FatalError ? RunState.Failed : RunState.Finished;

        var snapshot = Metrics.Snapshot();
        var report = RunReport.Create(
            startedAt,
            snapshot,
            profile,
            plan,
            _sessionFactory.Target,
            reason,
            coordinator.FatalDetail);

        _log.Info(
            $"Finished after {snapshot.Elapsed.TotalSeconds:F1}s — {snapshot.TotalOperations:N0} operations, " +
            $"{snapshot.TotalErrors:N0} errors, {snapshot.OperationsPerSecond:F1} ops/s, " +
            $"p95 {snapshot.Latency.P95:F1} ms. Stopped because {reason.Describe()}.");

        return report;
    }

    private async Task MarkRunningAfterRampAsync(int rampUpSeconds, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(rampUpSeconds), cancellationToken).ConfigureAwait(false);
            if (State == RunState.RampingUp)
                State = RunState.Running;
        }
        catch (OperationCanceledException)
        {
            // Run ended during ramp-up; the final state is set by RunAsync.
        }
        catch (Exception exception)
        {
            // Nothing awaits this task, so anything escaping here would surface much later as
            // an unobserved exception on the finaliser thread — far from its cause, and fatal
            // to a host that treats those as crashes.
            _log.Error($"Ramp-up transition failed: {exception.Message}");
        }
    }

    /// <summary>
    /// Spreads user starts evenly across the ramp window. Starting every user at once
    /// produces a burst whose latency reflects pool warm-up rather than the server.
    /// </summary>
    internal static TimeSpan RampDelayFor(int userIndex, WorkloadProfile profile)
    {
        if (profile.RampUpSeconds <= 0 || profile.VirtualUsers <= 1)
            return TimeSpan.Zero;

        var fraction = (double)userIndex / profile.VirtualUsers;
        return TimeSpan.FromSeconds(profile.RampUpSeconds * fraction);
    }

    private static StopReason ResolveStopReason(
        RunCoordinator coordinator, WorkloadProfile profile, CancellationToken hostToken)
    {
        if (coordinator.Reason != StopReason.None)
            return coordinator.Reason;

        if (hostToken.IsCancellationRequested)
            return StopReason.Cancelled;

        // Nothing set a reason, so the only thing that could have cancelled the run is the
        // duration timer.
        return profile.DurationSeconds > 0 ? StopReason.DurationReached : StopReason.UserRequested;
    }
}
