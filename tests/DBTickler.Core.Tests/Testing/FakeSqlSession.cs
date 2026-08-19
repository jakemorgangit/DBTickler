using System.Collections.Concurrent;
using DBTickler.Core.Data;

namespace DBTickler.Core.Tests.Testing;

/// <summary>
/// A configurable stand-in for a real SQL Server connection, so <see cref="Engine.LoadEngine"/>
/// can be exercised end-to-end without a database.
///
/// One <see cref="FakeSqlSessionFactory"/> is shared by every virtual user in a run (just like
/// the real <see cref="ISqlSessionFactory"/>), which is what lets it observe global properties
/// such as the true peak concurrency across all sessions.
/// </summary>
internal sealed class FakeSqlSessionFactory : ISqlSessionFactory
{
    private int _concurrent;
    private int _maxConcurrentObserved;
    private int _totalExecutions;
    private int _openCalls;

    public string Target => "fake-target";

    /// <summary>How many <see cref="ISqlSession.ExecuteAsync"/> calls were in flight at once, at the busiest instant.</summary>
    public int MaxConcurrentObserved => Volatile.Read(ref _maxConcurrentObserved);

    public int TotalExecutions => Volatile.Read(ref _totalExecutions);

    public int OpenCalls => Volatile.Read(ref _openCalls);

    /// <summary>Every SQL statement text handed to a session, in the order sessions received them.</summary>
    public ConcurrentQueue<string> ExecutedSql { get; } = new();

    /// <summary>Fixed per-call delay used when <see cref="DelayProvider"/> is not set.</summary>
    public TimeSpan Delay { get; set; } = TimeSpan.Zero;

    /// <summary>Overrides <see cref="Delay"/> when set; called once per execution.</summary>
    public Func<TimeSpan>? DelayProvider { get; set; }

    /// <summary>When it returns a non-null exception, that exception is thrown instead of succeeding.</summary>
    public Func<SqlRequest, Exception?>? ExceptionProvider { get; set; }

    /// <summary>Customises the result of a successful call; defaults to one row affected/read.</summary>
    public Func<SqlRequest, SqlExecutionResult>? ResultProvider { get; set; }

    /// <summary>When it returns a non-null exception, <see cref="OpenAsync"/> fails with it instead of connecting.</summary>
    public Func<Exception?>? OpenExceptionProvider { get; set; }

    public Task<ISqlSession> OpenAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _openCalls);

        var failure = OpenExceptionProvider?.Invoke();
        if (failure is not null)
            return Task.FromException<ISqlSession>(failure);

        return Task.FromResult<ISqlSession>(new FakeSqlSession(this));
    }

    internal async Task<SqlExecutionResult> ExecuteAsync(SqlRequest request, CancellationToken cancellationToken)
    {
        var current = Interlocked.Increment(ref _concurrent);
        TrackMax(current);
        try
        {
            Interlocked.Increment(ref _totalExecutions);
            ExecutedSql.Enqueue(request.Sql);

            var delay = DelayProvider?.Invoke() ?? Delay;
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

            var failure = ExceptionProvider?.Invoke(request);
            if (failure is not null)
                throw failure;

            return ResultProvider?.Invoke(request) ?? new SqlExecutionResult(1, 1);
        }
        finally
        {
            Interlocked.Decrement(ref _concurrent);
        }
    }

    private void TrackMax(int current)
    {
        int observed;
        do
        {
            observed = Volatile.Read(ref _maxConcurrentObserved);
            if (current <= observed) return;
        }
        while (Interlocked.CompareExchange(ref _maxConcurrentObserved, current, observed) != observed);
    }
}

internal sealed class FakeSqlSession : ISqlSession
{
    private readonly FakeSqlSessionFactory _factory;

    public FakeSqlSession(FakeSqlSessionFactory factory) => _factory = factory;

    public int? SessionId => null;

    public Task<SqlExecutionResult> ExecuteAsync(SqlRequest request, CancellationToken cancellationToken) =>
        _factory.ExecuteAsync(request, cancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
