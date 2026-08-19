using DBTickler.Core.Data;

namespace DBTickler.Core.Observability;

/// <summary>
/// Reads deadlock graphs out of the always-on <c>system_health</c> extended-event session.
/// Nothing has to be enabled on the server and no trace flag is needed — the graphs are
/// already being recorded, they were simply never surfaced.
/// </summary>
public sealed class DeadlockMonitor
{
    /// <summary>
    /// Pulls the xml_deadlock_report events out of the system_health ring buffer. The
    /// shredding is done server-side so only the deadlock graphs cross the network, not the
    /// whole multi-megabyte buffer.
    /// </summary>
    public const string RingBufferSql = """
        SET NOCOUNT ON;

        WITH ring AS
        (
            SELECT CAST(target_data AS XML) AS target_data
            FROM sys.dm_xe_session_targets st
            JOIN sys.dm_xe_sessions s ON s.address = st.event_session_address
            WHERE s.name = 'system_health'
              AND st.target_name = 'ring_buffer'
        )
        SELECT CAST(event_node.query('.') AS NVARCHAR(MAX)) AS event_xml
        FROM ring
        CROSS APPLY target_data.nodes('RingBufferTarget/event[@name="xml_deadlock_report"]') AS n(event_node)
        """;

    /// <summary>
    /// Cap on retained graphs. A long chaos run can produce thousands of deadlocks, and each
    /// graph carries its full XML, so keeping every one would grow without bound.
    /// </summary>
    private const int MaxRetained = 200;

    private readonly string _connectionString;
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    public DeadlockMonitor(string connectionString) => _connectionString = connectionString;

    /// <summary>The most recent deadlocks the monitor has surfaced, newest first.</summary>
    public List<DeadlockReport> Collected { get; } = [];

    /// <summary>
    /// Fetches graphs and returns only the ones not seen before. The ring buffer keeps a
    /// rolling window, so every poll re-reads the same events until they age out; without
    /// de-duplication the UI would show the same deadlock over and over.
    /// </summary>
    public async Task<IReadOnlyList<DeadlockReport>> PollAsync(CancellationToken cancellationToken = default)
    {
        var documents = await SqlQuery.ListAsync(
            _connectionString,
            RingBufferSql,
            static reader => reader.GetString(0),
            timeoutSeconds: 30,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var fresh = new List<DeadlockReport>();
        foreach (var document in documents)
        {
            foreach (var report in DeadlockReport.ParseAll(document))
            {
                if (!_seen.Add(report.Fingerprint))
                    continue;

                fresh.Add(report);
                Collected.Insert(0, report);
            }
        }

        if (Collected.Count > MaxRetained)
            Collected.RemoveRange(MaxRetained, Collected.Count - MaxRetained);

        // The ring buffer is not ordered newest-first, so sort what we just learned about.
        fresh.Sort((left, right) => Nullable.Compare(right.Timestamp, left.Timestamp));
        return fresh;
    }

    /// <summary>
    /// Marks everything currently in the ring buffer as already seen, without reporting it.
    /// Called before a run starts so historical deadlocks are not attributed to this run.
    /// </summary>
    public async Task PrimeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await PollAsync(cancellationToken).ConfigureAwait(false);
            Collected.Clear();
        }
        catch (Exception)
        {
            // Priming is best-effort: on a server where system_health is unavailable the
            // monitor simply reports nothing, which must not stop a run from starting.
        }
    }

    public void Clear()
    {
        _seen.Clear();
        Collected.Clear();
    }
}
