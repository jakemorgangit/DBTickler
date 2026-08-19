using DBTickler.Core.Data;

namespace DBTickler.Core.Observability;

/// <summary>A wait type's counters at one instant.</summary>
public readonly record struct WaitStat(string WaitType, long WaitTimeMs, long SignalWaitTimeMs, long WaitingTasks);

/// <summary>How much a wait type accumulated between two snapshots.</summary>
public sealed record WaitDelta(
    string WaitType,
    long WaitTimeMs,
    long SignalWaitTimeMs,
    long WaitingTasks)
{
    /// <summary>Time spent waiting for a resource, as opposed to waiting for a CPU quantum.</summary>
    public long ResourceWaitTimeMs => Math.Max(0, WaitTimeMs - SignalWaitTimeMs);

    public double AverageWaitMs => WaitingTasks > 0 ? (double)WaitTimeMs / WaitingTasks : 0;

    /// <summary>Share of the whole run's wait time, set once the full set is known.</summary>
    public double PercentOfTotal { get; init; }
}

/// <summary>
/// Samples <c>sys.dm_os_wait_stats</c> and reports what a run actually waited on.
///
/// Wait statistics are cumulative since the instance started, so the raw view is dominated
/// by whatever happened last week. Taking a baseline before the run and diffing against it
/// is what makes the numbers describe the run rather than the server's history.
/// </summary>
public sealed class WaitStatsMonitor
{
    /// <summary>
    /// Waits that are always present and say nothing about user workload — idle worker
    /// threads, checkpoint timers, and similar background housekeeping. Reporting these
    /// buries the handful of waits that actually matter.
    /// </summary>
    public static readonly HashSet<string> BenignWaitTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "BROKER_EVENTHANDLER", "BROKER_RECEIVE_WAITFOR", "BROKER_TASK_STOP",
        "BROKER_TO_FLUSH", "BROKER_TRANSMITTER", "CHECKPOINT_QUEUE",
        "CHKPT", "CLR_AUTO_EVENT", "CLR_MANUAL_EVENT", "CLR_SEMAPHORE",
        "DBMIRROR_DBM_EVENT", "DBMIRROR_EVENTS_QUEUE", "DBMIRROR_WORKER_QUEUE",
        "DBMIRRORING_CMD", "DIRTY_PAGE_POLL", "DISPATCHER_QUEUE_SEMAPHORE",
        "EXECSYNC", "FSAGENT", "FT_IFTS_SCHEDULER_IDLE_WAIT", "FT_IFTSHC_MUTEX",
        "HADR_CLUSAPI_CALL", "HADR_FILESTREAM_IOMGR_IOCOMPLETION", "HADR_LOGCAPTURE_WAIT",
        "HADR_NOTIFICATION_DEQUEUE", "HADR_TIMER_TASK", "HADR_WORK_QUEUE",
        "KSOURCE_WAKEUP", "LAZYWRITER_SLEEP", "LOGMGR_QUEUE", "MEMORY_ALLOCATION_EXT",
        "ONDEMAND_TASK_QUEUE", "PARALLEL_REDO_DRAIN_WORKER", "PARALLEL_REDO_LOG_CACHE",
        "PARALLEL_REDO_TRAN_LIST", "PARALLEL_REDO_WORKER_SYNC", "PARALLEL_REDO_WORKER_WAIT_WORK",
        "PREEMPTIVE_XE_GETTARGETSTATE", "PWAIT_ALL_COMPONENTS_INITIALIZED",
        "PWAIT_DIRECTLOGCONSUMER_GETNEXT", "QDS_PERSIST_TASK_MAIN_LOOP_SLEEP",
        "QDS_ASYNC_QUEUE", "QDS_CLEANUP_STALE_QUERIES_TASK_MAIN_LOOP_SLEEP",
        "QDS_SHUTDOWN_QUEUE", "REDO_THREAD_PENDING_WORK", "REQUEST_FOR_DEADLOCK_SEARCH",
        "RESOURCE_QUEUE", "SERVER_IDLE_CHECK", "SLEEP_BPOOL_FLUSH", "SLEEP_DBSTARTUP",
        "SLEEP_DCOMSTARTUP", "SLEEP_MASTERDBREADY", "SLEEP_MASTERMDREADY",
        "SLEEP_MASTERUPGRADED", "SLEEP_MSDBSTARTUP", "SLEEP_SYSTEMTASK", "SLEEP_TASK",
        "SLEEP_TEMPDBSTARTUP", "SNI_HTTP_ACCEPT", "SOS_WORK_DISPATCHER",
        "SP_SERVER_DIAGNOSTICS_SLEEP", "SQLTRACE_BUFFER_FLUSH",
        "SQLTRACE_INCREMENTAL_FLUSH_SLEEP", "SQLTRACE_WAIT_ENTRIES",
        "STARTUP_DEPENDENCY_MANAGER", "WAIT_FOR_RESULTS", "WAITFOR",
        "WAITFOR_TASKSHUTDOWN", "WAIT_XTP_RECOVERY", "WAIT_XTP_HOST_WAIT",
        "WAIT_XTP_OFFLINE_CKPT_NEW_LOG", "WAIT_XTP_CKPT_CLOSE", "XE_DISPATCHER_JOIN",
        "XE_DISPATCHER_WAIT", "XE_LIVE_TARGET_TVF", "XE_TIMER_EVENT",
    };

    public const string WaitStatsSql = """
        SELECT wait_type,
               CAST(wait_time_ms AS BIGINT),
               CAST(signal_wait_time_ms AS BIGINT),
               CAST(waiting_tasks_count AS BIGINT)
        FROM sys.dm_os_wait_stats
        WHERE waiting_tasks_count > 0
        """;

    private readonly string _connectionString;

    public WaitStatsMonitor(string connectionString) => _connectionString = connectionString;

    public async Task<IReadOnlyList<WaitStat>> SampleAsync(CancellationToken cancellationToken = default)
    {
        return await SqlQuery.ListAsync(
            _connectionString,
            WaitStatsSql,
            static reader => new WaitStat(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt64(3)),
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Diffs two samples and returns the meaningful waits, largest first. Pure function, so
    /// the interesting part is testable without a server.
    /// </summary>
    public static IReadOnlyList<WaitDelta> Diff(
        IReadOnlyList<WaitStat> baseline,
        IReadOnlyList<WaitStat> current,
        int topN = 15)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(current);

        var baselineByType = baseline.ToDictionary(stat => stat.WaitType, StringComparer.OrdinalIgnoreCase);

        var deltas = new List<WaitDelta>();
        foreach (var stat in current)
        {
            if (BenignWaitTypes.Contains(stat.WaitType))
                continue;

            baselineByType.TryGetValue(stat.WaitType, out var before);

            // A negative delta means the counters were cleared mid-run (DBCC SQLPERF), in
            // which case the current reading is the best estimate of what the run caused.
            var waitTime = stat.WaitTimeMs - before.WaitTimeMs;
            var signalTime = stat.SignalWaitTimeMs - before.SignalWaitTimeMs;
            var tasks = stat.WaitingTasks - before.WaitingTasks;

            if (waitTime < 0 || tasks < 0)
            {
                waitTime = stat.WaitTimeMs;
                signalTime = stat.SignalWaitTimeMs;
                tasks = stat.WaitingTasks;
            }

            if (waitTime <= 0 && tasks <= 0)
                continue;

            deltas.Add(new WaitDelta(stat.WaitType, waitTime, signalTime, tasks));
        }

        var totalWaitTime = deltas.Sum(delta => delta.WaitTimeMs);
        if (totalWaitTime <= 0) totalWaitTime = 1;

        return deltas
            .OrderByDescending(delta => delta.WaitTimeMs)
            .Take(topN)
            .Select(delta => delta with { PercentOfTotal = (double)delta.WaitTimeMs / totalWaitTime })
            .ToList();
    }
}
