using System.Text.RegularExpressions;
using DBTickler.Core.Data;

namespace DBTickler.Core.Safety;

public enum RiskLevel
{
    Low,
    Elevated,
    High,
}

/// <summary>What the guard found, and how strongly it objects.</summary>
public sealed record RiskAssessment(RiskLevel Level, IReadOnlyList<string> Signals)
{
    public bool RequiresConfirmation => Level >= RiskLevel.Elevated;

    public string Describe() => Signals.Count == 0
        ? "No production indicators found."
        : string.Join(Environment.NewLine, Signals.Select(signal => "• " + signal));

    public static RiskAssessment None { get; } = new(RiskLevel.Low, []);
}

/// <summary>
/// Looks for signs that the target is a production system before a destructive run starts.
///
/// This tool ships blocking attacks, lock escalation and deadlock generation, and v1 had
/// nothing between "type a server name" and "start hammering it" beyond a checkbox the
/// operator had to remember to tick. The guard cannot know for certain what is production,
/// so it gathers evidence and asks rather than refusing.
/// </summary>
public static partial class ProductionGuard
{
    [GeneratedRegex(@"\b(prod|production|live|prd)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ProductionNamePattern();

    public const string SignalsSql = """
        SET NOCOUNT ON;

        SELECT
            CAST(ISNULL(SERVERPROPERTY('IsHadrEnabled'), 0) AS INT)          AS is_hadr_enabled,
            CAST(ISNULL(SERVERPROPERTY('IsClustered'), 0) AS INT)            AS is_clustered,
            CAST(d.is_published AS INT)                                      AS is_published,
            CAST(d.is_subscribed AS INT)                                     AS is_subscribed,
            d.recovery_model_desc,
            CAST(ISNULL((SELECT COUNT(DISTINCT s.program_name)
                         FROM sys.dm_exec_sessions s
                         WHERE s.is_user_process = 1
                           AND s.program_name <> @appName), 0) AS INT)       AS other_app_count,
            CAST(ISNULL((SELECT COUNT(*)
                         FROM sys.dm_exec_sessions s
                         WHERE s.is_user_process = 1
                           AND s.program_name <> @appName), 0) AS INT)       AS other_session_count
        FROM sys.databases d
        WHERE d.database_id = DB_ID();
        """;

    /// <summary>
    /// Name-based check only. Runs without touching the server, so the UI can react as the
    /// operator types rather than waiting for a connection.
    /// </summary>
    public static RiskAssessment AssessName(string? server, string? database)
    {
        var signals = new List<string>();

        if (!string.IsNullOrWhiteSpace(database) && ProductionNamePattern().IsMatch(database))
            signals.Add($"The database name '{database}' looks like a production database.");

        if (!string.IsNullOrWhiteSpace(server) && ProductionNamePattern().IsMatch(server))
            signals.Add($"The server name '{server}' looks like a production server.");

        return signals.Count == 0
            ? RiskAssessment.None
            : new RiskAssessment(RiskLevel.Elevated, signals);
    }

    /// <summary>
    /// Full check, including what the server says about itself. Failures are swallowed: a
    /// guard that cannot read a DMV must not stop a run, only decline to vouch for it.
    /// </summary>
    public static async Task<RiskAssessment> AssessAsync(
        string connectionString,
        string? server,
        string? database,
        CancellationToken cancellationToken = default)
    {
        var signals = new List<string>(AssessName(server, database).Signals);
        var level = signals.Count > 0 ? RiskLevel.Elevated : RiskLevel.Low;

        try
        {
            var rows = await SqlQuery.ListAsync(
                connectionString,
                SignalsSql,
                static reader => new
                {
                    HadrEnabled = reader.GetInt32(0) == 1,
                    Clustered = reader.GetInt32(1) == 1,
                    Published = reader.GetInt32(2) == 1,
                    Subscribed = reader.GetInt32(3) == 1,
                    RecoveryModel = reader.GetNullableString(4) ?? "",
                    OtherApps = reader.GetInt32(5),
                    OtherSessions = reader.GetInt32(6),
                },
                parameters: [new SqlParameterValue("@appName", Configuration.ConnectionProfile.ApplicationName)],
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (rows.Count == 0)
                return new RiskAssessment(level, signals);

            var row = rows[0];

            if (row.HadrEnabled)
            {
                signals.Add("Always On availability groups are enabled on this instance.");
                level = RiskLevel.High;
            }

            if (row.Clustered)
            {
                signals.Add("This instance is clustered.");
                level = RiskLevel.High;
            }

            if (row.Published || row.Subscribed)
            {
                signals.Add("This database takes part in replication.");
                level = RiskLevel.High;
            }

            if (row.OtherSessions >= 10 || row.OtherApps >= 3)
            {
                signals.Add(
                    $"{row.OtherSessions} session(s) from {row.OtherApps} other application(s) are connected — " +
                    "something else is using this server.");
                if (level < RiskLevel.Elevated) level = RiskLevel.Elevated;
            }

            if (string.Equals(row.RecoveryModel, "FULL", StringComparison.OrdinalIgnoreCase))
            {
                signals.Add("The database is in FULL recovery, which usually means it is backed up as production.");
                if (level < RiskLevel.Elevated) level = RiskLevel.Elevated;
            }
        }
        catch (Exception)
        {
            // Insufficient rights to read the DMVs is itself unremarkable — plenty of lab
            // logins lack VIEW SERVER STATE. Report what the name check found.
        }

        return new RiskAssessment(level, signals);
    }
}
