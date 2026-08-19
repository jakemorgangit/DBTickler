using DBTickler.Core.Data;
using DBTickler.Core.Logging;

namespace DBTickler.Core.Workloads;

/// <summary>Creates, seeds and removes the objects DBTickler needs on the target.</summary>
public sealed class DatabaseSetup
{
    private readonly string _connectionString;
    private readonly IRunLog _log;

    public DatabaseSetup(string connectionString, IRunLog? log = null)
    {
        _connectionString = connectionString;
        _log = log ?? NullRunLog.Instance;
    }

    /// <summary>
    /// Creates or upgrades dbo.LoadGen, seeds the anchor rows, and optionally prefills it so
    /// reads have something to hit from the first second of a run.
    /// </summary>
    public async Task<long> SetupAsync(int prefillRows = 20_000, CancellationToken cancellationToken = default)
    {
        _log.Info("Creating dbo.LoadGen if it does not exist…");
        await SqlQuery.ExecuteAsync(_connectionString, SetupScript.CreateOrUpgrade,
            timeoutSeconds: 120, cancellationToken: cancellationToken).ConfigureAwait(false);

        _log.Debug("Seeding anchor rows…");
        await SqlQuery.ExecuteAsync(_connectionString, SetupScript.SeedAnchors,
            timeoutSeconds: 120, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (prefillRows > SetupScript.ReservedIdCount)
        {
            _log.Info($"Filling dbo.LoadGen to {prefillRows:N0} rows…");
            await SqlQuery.ExecuteAsync(_connectionString, SetupScript.Prefill,
                timeoutSeconds: 600,
                parameters: [new SqlParameterValue("@rows", prefillRows)],
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        var rows = await CountRowsAsync(cancellationToken).ConfigureAwait(false);
        _log.Success($"Setup complete — dbo.LoadGen holds {rows:N0} rows.");
        return rows;
    }

    public async Task<long> CountRowsAsync(CancellationToken cancellationToken = default) =>
        await SqlQuery.ScalarAsync<long>(_connectionString, SetupScript.CountRows,
            timeoutSeconds: 120, cancellationToken: cancellationToken).ConfigureAwait(false);

    /// <summary>Drops everything the tool created, so a demo can leave the database as it found it.</summary>
    public async Task TeardownAsync(CancellationToken cancellationToken = default)
    {
        _log.Warning("Dropping dbo.LoadGen…");
        await SqlQuery.ExecuteAsync(_connectionString, SetupScript.Teardown,
            timeoutSeconds: 300, cancellationToken: cancellationToken).ConfigureAwait(false);
        _log.Success("dbo.LoadGen removed.");
    }

    /// <summary>Opens a connection and reports the server version, for a connection test.</summary>
    public async Task<ServerInfo> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        var probe = new SchemaProbe(_connectionString);
        var capabilities = await probe.ProbeAsync(maxTables: 0, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return capabilities.Server;
    }
}
