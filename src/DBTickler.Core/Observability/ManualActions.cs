using DBTickler.Core.Data;
using DBTickler.Core.Logging;
using DBTickler.Core.Workloads;
using Microsoft.Data.SqlClient;

namespace DBTickler.Core.Observability;

/// <summary>
/// On-demand demonstrations: hold a lock, or produce a deadlock right now.
///
/// Both act on the tool's own <c>dbo.LoadGen</c> anchor rows, never on user tables.
/// </summary>
public sealed class ManualActions
{
    private readonly string _connectionString;
    private readonly IRunLog _log;

    public ManualActions(string connectionString, IRunLog? log = null)
    {
        _connectionString = connectionString;
        _log = log ?? NullRunLog.Instance;
    }

    /// <summary>
    /// Holds an exclusive lock on an anchor row until the delay elapses or the caller
    /// cancels. Anything else touching that row queues behind it, which is exactly what a
    /// blocking demonstration needs.
    /// </summary>
    public async Task ForceBlockingAsync(TimeSpan duration, CancellationToken cancellationToken = default)
    {
        var seconds = Math.Clamp((int)duration.TotalSeconds, 1, 3600);
        _log.Info($"Holding an exclusive lock on dbo.LoadGen row 3 for {seconds}s — other writers will block behind it.");

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await using (var update = new SqlCommand(
                "UPDATE dbo.LoadGen SET UpdatedAt = SYSUTCDATETIME() WHERE Id = 3", connection, transaction))
            {
                update.CommandTimeout = 30;
                await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            _log.Success($"Lock acquired on session {connection.ServerProcessId}. Holding it now.");

            // The wait happens client-side rather than in a WAITFOR, so cancelling actually
            // releases the lock immediately instead of waiting out the server-side delay.
            await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken).ConfigureAwait(false);

            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            _log.Info("Blocking finished — lock released.");
        }
        catch (OperationCanceledException)
        {
            await SafeRollbackAsync(transaction).ConfigureAwait(false);
            _log.Info("Blocking cancelled — lock released.");
        }
        catch (Exception exception)
        {
            await SafeRollbackAsync(transaction).ConfigureAwait(false);
            _log.Error($"Blocking failed: {exception.Message}");
            throw;
        }
    }

    /// <summary>
    /// Produces a real deadlock between two connections this process owns, then reports which
    /// one was chosen as the victim.
    ///
    /// v1 attempted this across two separate application instances coordinated through lock
    /// files in the temp directory, which meant the operator had to launch a second copy and
    /// click at the right moment, and left stale files behind when it failed. Owning both
    /// sides makes the cycle deterministic.
    /// </summary>
    public async Task<int?> ForceDeadlockAsync(CancellationToken cancellationToken = default)
    {
        _log.Info("Creating a deadlock: two sessions will take the same two row locks in opposite orders.");

        await using var first = new SqlConnection(_connectionString);
        await using var second = new SqlConnection(_connectionString);
        await first.OpenAsync(cancellationToken).ConfigureAwait(false);
        await second.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Both sides take their first lock before either reaches for the second, so the cycle
        // is guaranteed rather than a matter of timing.
        using var firstHasLock = new SemaphoreSlim(0, 1);
        using var secondHasLock = new SemaphoreSlim(0, 1);

        var firstTask = RunSideAsync(first, 1, 2, firstHasLock, secondHasLock, cancellationToken);
        var secondTask = RunSideAsync(second, 2, 1, secondHasLock, firstHasLock, cancellationToken);

        var results = await Task.WhenAll(firstTask, secondTask).ConfigureAwait(false);
        var victim = results.FirstOrDefault(result => result.WasVictim);

        if (victim.WasVictim)
        {
            _log.Success(
                $"Deadlock produced. Session {victim.SessionId} was chosen as the victim and rolled back; " +
                "the other session committed. Check the Deadlocks tab for the graph.");
            return victim.SessionId;
        }

        _log.Warning(
            "Both sessions completed without deadlocking. This usually means the two anchor rows " +
            "landed on the same page and one lock covered both; try again, or run a workload " +
            "alongside it to add contention.");
        return null;
    }

    private readonly record struct DeadlockSideResult(int SessionId, bool WasVictim);

    private async Task<DeadlockSideResult> RunSideAsync(
        SqlConnection connection,
        int firstRowId,
        int secondRowId,
        SemaphoreSlim signalOwnLock,
        SemaphoreSlim awaitOtherLock,
        CancellationToken cancellationToken)
    {
        var sessionId = connection.ServerProcessId;
        await using var transaction = (SqlTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await UpdateAnchorAsync(connection, transaction, firstRowId, cancellationToken).ConfigureAwait(false);

            signalOwnLock.Release();
            await awaitOtherLock.WaitAsync(cancellationToken).ConfigureAwait(false);

            // This is the statement that closes the cycle; one of the two sides will be
            // chosen as the victim and receive error 1205 here.
            await UpdateAnchorAsync(connection, transaction, secondRowId, cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new DeadlockSideResult(sessionId, WasVictim: false);
        }
        catch (SqlException exception) when (SqlErrorClassifier.Classify(exception) == SqlFailureKind.DeadlockVictim)
        {
            // The victim's transaction has already been rolled back by the server.
            return new DeadlockSideResult(sessionId, WasVictim: true);
        }
        catch (Exception)
        {
            await SafeRollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task UpdateAnchorAsync(
        SqlConnection connection, SqlTransaction transaction, int rowId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            "UPDATE dbo.LoadGen SET UpdatedAt = SYSUTCDATETIME() WHERE Id = @id", connection, transaction)
        {
            CommandTimeout = 60,
        };
        command.Parameters.AddWithValue("@id", rowId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task SafeRollbackAsync(SqlTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // The transaction is already gone — rolled back by the server, or the connection
            // dropped. Either way there is nothing left to undo.
        }
    }

    /// <summary>Confirms the anchor rows the manual actions depend on are present.</summary>
    public async Task<bool> AnchorRowsExistAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var count = await SqlQuery.ScalarAsync<int>(
                _connectionString,
                $"SELECT COUNT(*) FROM dbo.LoadGen WHERE Id <= {SetupScript.ReservedIdCount}",
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return count >= 3;
        }
        catch (SqlException)
        {
            return false;
        }
    }
}
