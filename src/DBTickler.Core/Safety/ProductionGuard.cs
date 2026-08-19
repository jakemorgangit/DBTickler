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
public static class ProductionGuard
{
    private static readonly string[] ProductionKeywords = ["production", "prod", "prd", "live"];

    /// <summary>
    /// True when a server or database name contains a production keyword as a recognisable
    /// part of the name.
    ///
    /// This deliberately does not use a <c>\b</c> word boundary. In .NET, digits and
    /// underscore are word characters, so <c>\bprod\b</c> fails to fire on <c>Sales_Prod</c>,
    /// <c>DB_PROD_01</c> and <c>PROD01</c> — some of the most common production naming
    /// conventions there are, and precisely the names this guard exists to catch. Instead the
    /// boundary is worked out from the characters either side, treating a change of case as a
    /// boundary too so that <c>LiveDB</c> and <c>SQLPROD2</c> are caught while
    /// <c>productdb</c>, <c>reproduction</c> and <c>delivery</c> are not.
    ///
    /// Where the two goals conflict, this errs towards matching: a false positive costs one
    /// confirmation click, and a false negative means running a destructive workload against
    /// production.
    /// </summary>
    internal static bool LooksLikeProduction(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;

        foreach (var keyword in ProductionKeywords)
        {
            var index = 0;
            while ((index = name.IndexOf(keyword, index, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                if (HasBoundariesAround(name, index, keyword))
                    return true;

                index++;
            }
        }

        return false;
    }

    private static bool HasBoundariesAround(string name, int index, string keyword)
    {
        var matched = name.AsSpan(index, keyword.Length);
        var matchIsUpper = !ContainsLower(matched);

        var before = index == 0 ? '\0' : name[index - 1];
        var afterIndex = index + keyword.Length;
        var after = afterIndex >= name.Length ? '\0' : name[afterIndex];

        // Left: a separator, a digit or the start of the name is a clean boundary. A letter is
        // only accepted when both it and the match are upper case, which is what makes
        // SQLPROD2 count while reproduction does not.
        var leftIsBoundary = !char.IsLetter(before) || (char.IsUpper(before) && matchIsUpper);

        // Right: a separator, a digit or the end of the name. An upper-case letter counts too,
        // since that is a word boundary in PascalCase — LiveDB, ProdServer. The full word
        // "production" needs no right boundary at all.
        var rightIsBoundary = !char.IsLetter(after)
                              || char.IsUpper(after)
                              || keyword.Equals("production", StringComparison.OrdinalIgnoreCase);

        return leftIsBoundary && rightIsBoundary;
    }

    private static bool ContainsLower(ReadOnlySpan<char> text)
    {
        foreach (var character in text)
        {
            if (char.IsLower(character)) return true;
        }
        return false;
    }

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

        if (!string.IsNullOrWhiteSpace(database) && LooksLikeProduction(database))
            signals.Add($"The database name '{database}' looks like a production database.");

        if (!string.IsNullOrWhiteSpace(server) && LooksLikeProduction(server))
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
