using DBTickler.Core.Data;

namespace DBTickler.Core.Observability;

/// <summary>
/// Polls the server for what it is doing right now. This is the half of DBTickler that
/// turns generated load into something you can learn from: the README always promised you
/// could "observe sessions, waits, locking and blocking in real time", but v1 shipped no
/// server-side view at all.
/// </summary>
public sealed class ServerMonitor
{
    /// <summary>
    /// Running requests plus any idle session that is holding someone else up. The second
    /// half matters: the classic head blocker is a session sleeping with an open
    /// transaction, and it has no row in sys.dm_exec_requests at all.
    /// </summary>
    public const string ActiveRequestsSql = """
        SET NOCOUNT ON;
        SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

        WITH blockers AS
        (
            SELECT DISTINCT blocking_session_id AS session_id
            FROM sys.dm_exec_requests
            WHERE blocking_session_id <> 0
        )
        SELECT
            CAST(s.session_id AS INT)                           AS session_id,
            CAST(ISNULL(r.blocking_session_id, 0) AS INT)       AS blocking_session_id,
            ISNULL(r.status, s.status)                          AS status,
            ISNULL(r.command, N'')                              AS command,
            r.wait_type,
            CAST(ISNULL(r.wait_time, 0) AS BIGINT)              AS wait_time,
            r.wait_resource,
            CAST(ISNULL(r.cpu_time, s.cpu_time) AS BIGINT)      AS cpu_time,
            CAST(ISNULL(r.logical_reads, s.logical_reads) AS BIGINT)           AS logical_reads,
            CAST(ISNULL(r.total_elapsed_time, s.total_elapsed_time) AS BIGINT) AS total_elapsed_time,
            s.login_name,
            s.host_name,
            s.program_name,
            DB_NAME(ISNULL(r.database_id, s.database_id))       AS database_name,
            SUBSTRING(
                t.text,
                (ISNULL(r.statement_start_offset, 0) / 2) + 1,
                ((CASE ISNULL(r.statement_end_offset, -1)
                       WHEN -1 THEN DATALENGTH(t.text)
                       ELSE r.statement_end_offset
                  END - ISNULL(r.statement_start_offset, 0)) / 2) + 1)  AS statement_text
        FROM sys.dm_exec_sessions s
        LEFT JOIN sys.dm_exec_requests r ON r.session_id = s.session_id
        -- An idle head blocker has no request, so its statement text has to come from the
        -- connection's most recent handle instead. Taken with TOP (1) rather than a join,
        -- because a session using MARS has several connections and would otherwise appear
        -- once per connection.
        OUTER APPLY
        (
            SELECT TOP (1) c.most_recent_sql_handle
            FROM sys.dm_exec_connections c
            WHERE c.session_id = s.session_id
        ) c
        OUTER APPLY sys.dm_exec_sql_text(COALESCE(r.sql_handle, c.most_recent_sql_handle)) t
        WHERE s.is_user_process = 1
          AND s.session_id <> @@SPID
          AND (r.session_id IS NOT NULL OR s.session_id IN (SELECT session_id FROM blockers))
        ORDER BY CAST(ISNULL(r.total_elapsed_time, 0) AS BIGINT) DESC;
        """;

    private readonly string _connectionString;

    public ServerMonitor(string connectionString) => _connectionString = connectionString;

    public async Task<IReadOnlyList<ActiveRequest>> GetActiveRequestsAsync(CancellationToken cancellationToken = default)
    {
        return await SqlQuery.ListAsync(
            _connectionString,
            ActiveRequestsSql,
            static reader => new ActiveRequest
            {
                SessionId = reader.GetInt32(0),
                BlockingSessionId = reader.IsDBNull(1) ? null : reader.GetInt32(1),
                Status = reader.GetNullableString(2) ?? "",
                Command = reader.GetNullableString(3) ?? "",
                WaitType = reader.GetNullableString(4),
                WaitTimeMs = reader.GetInt64Loose(5),
                WaitResource = reader.GetNullableString(6),
                CpuTimeMs = reader.GetInt64Loose(7),
                LogicalReads = reader.GetInt64Loose(8),
                ElapsedMs = reader.GetInt64Loose(9),
                LoginName = reader.GetNullableString(10),
                HostName = reader.GetNullableString(11),
                ProgramName = reader.GetNullableString(12),
                DatabaseName = reader.GetNullableString(13),
                StatementText = reader.GetNullableString(14),
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Terminates the sessions this tool opened, and only those. Matching is on the
    /// application name that <see cref="Configuration.ConnectionProfile"/> stamps on every
    /// connection.
    ///
    /// v1 ran the same idea but never set an application name on its connections, so the
    /// predicate matched nothing and the advertised "immediate stop via KILL" silently did
    /// nothing at all.
    /// </summary>
    public const string KillOwnSessionsSql = """
        SET NOCOUNT ON;

        DECLARE @sql NVARCHAR(MAX) = N'';
        DECLARE @killed INT = 0;

        SELECT @sql = @sql + N'BEGIN TRY KILL ' + CAST(session_id AS NVARCHAR(10)) + N'; END TRY BEGIN CATCH END CATCH; '
        FROM sys.dm_exec_sessions
        WHERE program_name = @appName
          AND session_id <> @@SPID
          AND is_user_process = 1;

        SELECT @killed = COUNT(*)
        FROM sys.dm_exec_sessions
        WHERE program_name = @appName
          AND session_id <> @@SPID
          AND is_user_process = 1;

        -- EXEC rather than sp_executesql: KILL is documented as runnable from an EXEC batch,
        -- and there is nothing to parameterise here since the ids are already validated.
        IF LEN(@sql) > 0 EXEC (@sql);

        SELECT @killed;
        """;

    /// <summary>Kills this tool's own sessions and returns how many were targeted.</summary>
    public async Task<int> KillOwnSessionsAsync(CancellationToken cancellationToken = default)
    {
        return await SqlQuery.ScalarAsync<int>(
            _connectionString,
            KillOwnSessionsSql,
            timeoutSeconds: 30,
            parameters: [new SqlParameterValue("@appName", Configuration.ConnectionProfile.ApplicationName)],
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Counts this tool's currently connected sessions, for the status display.</summary>
    public const string CountOwnSessionsSql = """
        SELECT COUNT(*)
        FROM sys.dm_exec_sessions
        WHERE program_name = @appName AND is_user_process = 1
        """;

    public async Task<int> CountOwnSessionsAsync(CancellationToken cancellationToken = default)
    {
        return await SqlQuery.ScalarAsync<int>(
            _connectionString,
            CountOwnSessionsSql,
            parameters: [new SqlParameterValue("@appName", Configuration.ConnectionProfile.ApplicationName)],
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
