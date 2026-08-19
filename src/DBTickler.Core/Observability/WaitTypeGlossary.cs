namespace DBTickler.Core.Observability;

/// <summary>
/// Plain-English explanations of the wait types a workload is likely to produce.
///
/// A list of wait types only helps someone who already knows what they mean, which is
/// exactly the audience this tool is meant to teach. Each entry says what the server was
/// waiting for and what usually causes it.
/// </summary>
public static class WaitTypeGlossary
{
    private const string Locking =
        "Waiting for a lock another session holds — this is blocking. Follow the blocking chain to the head blocker.";

    private const string PageIo =
        "Waiting for a data page to be read from disk into the buffer pool. Points at storage latency, or a buffer pool too small for the working set.";

    private const string PageLatch =
        "Waiting for a page already in memory — contention on a hot page, such as the end of a clustered index during heavy inserts, or tempdb allocation pages.";

    private const string Latch =
        "Waiting for an internal structure that is not a data page. Often accompanies heavy parallelism or in-memory contention.";

    private const string IoCompletion =
        "Waiting for a read or write that is not a data page, such as a sort spilling to disk.";

    private const string WriteLog =
        "Waiting for the transaction log to be flushed to disk. Every commit waits on this, so it dominates write-heavy workloads with slow log storage.";

    private const string LogBuffer =
        "Waiting for space in the in-memory log buffer — the log cannot be written out fast enough to keep up.";

    private const string Network =
        "The server has rows ready and is waiting for the client to consume them. Usually the client is the bottleneck, not SQL Server.";

    private const string Parallelism =
        "Parallel query threads waiting for each other. Some is normal on a parallel plan; a lot suggests skew, or a cost threshold for parallelism set too low.";

    private const string ParallelConsumer =
        "The consuming side of a parallel exchange waiting for producers. Benign on its own.";

    private const string Scheduler =
        "A task yielded the CPU and is waiting for its next turn — CPU pressure, or a query burning through many rows already in memory.";

    private const string ThreadPool =
        "No worker thread was available to run a request. Serious: usually a symptom of long blocking chains tying up workers.";

    private const string MemoryGrant =
        "Waiting for a memory grant before a query can start. Large sorts and hashes queue behind each other here.";

    private const string CompileMemory =
        "Waiting for memory to compile a plan, typically caused by a flood of unparameterised ad-hoc queries.";

    private const string Preemptive =
        "The server called out to the operating system and left the scheduler while it waited — file system, network or authentication calls.";

    private const string LinkedServer =
        "Waiting on a linked server, DBCC or full-text — anything routed through the OLE DB provider.";

    private const string LogGovernor =
        "Log generation is being throttled by the service tier's limit. Common on Azure SQL when the tier's log rate is reached.";

    /// <summary>
    /// Prefix families, longest prefix first so that a more specific entry wins. Families
    /// such as LCK_M_* and PAGEIOLATCH_* have dozens of members that all mean the same thing
    /// to someone reading a report, so they are described once.
    /// </summary>
    private static readonly (string Prefix, string Description)[] Families =
    [
        ("RESOURCE_SEMAPHORE_QUERY_COMPILE", CompileMemory),
        ("INSTANCE_LOG_RATE_GOVERNOR", LogGovernor),
        ("WAIT_ON_SYNC_STATISTICS_REFRESH",
            "Waiting for statistics to be updated synchronously before a plan could be produced."),
        ("HADR_SYNC_COMMIT",
            "Waiting for a synchronous availability group replica to harden the log. Commit latency here is network and replica latency."),
        ("TRANSACTION_MUTEX",
            "Several sessions are trying to use the same transaction, usually through MARS or a distributed transaction."),
        ("RESOURCE_SEMAPHORE", MemoryGrant),
        ("ASYNC_NETWORK_IO", Network),
        ("SOS_SCHEDULER_YIELD", Scheduler),
        ("LOG_RATE_GOVERNOR", LogGovernor),
        ("MEMORY_ALLOCATION_EXT", "Waiting on a memory allocation, frequently alongside memory pressure."),
        ("PAGEIOLATCH_", PageIo),
        ("PREEMPTIVE_OS_", Preemptive),
        ("PREEMPTIVE_", Preemptive),
        ("IO_COMPLETION", IoCompletion),
        ("BACKUPIO", "Waiting on a backup device."),
        ("CXCONSUMER", ParallelConsumer),
        ("THREADPOOL", ThreadPool),
        ("PAGELATCH_", PageLatch),
        ("NETWORK_IO", Network),
        ("LOGBUFFER", LogBuffer),
        ("WRITELOG", WriteLog),
        ("CXPACKET", Parallelism),
        ("TRACEWRITE", "Waiting for a trace or extended-event target to be written."),
        ("OPTIMIZER_", "Waiting inside query optimisation."),
        ("LCK_M_", Locking),
        ("LATCH_", Latch),
        ("OLEDB", LinkedServer),
        ("MSQL_XP", "Waiting for an extended stored procedure to return."),
        ("DTC", "Waiting on the distributed transaction coordinator."),
    ];

    /// <summary>
    /// Returns an explanation, or null when the wait type is not one the glossary covers.
    /// Unknown types are left unannotated rather than guessed at.
    /// </summary>
    public static string? Describe(string? waitType)
    {
        if (string.IsNullOrWhiteSpace(waitType)) return null;

        foreach (var (prefix, description) in Families)
        {
            if (waitType.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return description;
        }

        return null;
    }

    /// <summary>An explanation, or a neutral fallback suitable for display in a table.</summary>
    public static string DescribeOrDefault(string? waitType) =>
        Describe(waitType) ?? "Not in the glossary — look this wait type up before drawing conclusions from it.";

    /// <summary>
    /// True when the wait type means one session is stuck behind another, which is what
    /// makes it worth pointing the reader at the blocking view.
    /// </summary>
    public static bool IndicatesBlocking(string? waitType) =>
        waitType is not null && waitType.StartsWith("LCK_M_", StringComparison.OrdinalIgnoreCase);
}
