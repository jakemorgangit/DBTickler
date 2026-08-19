using Microsoft.Data.SqlClient;

namespace DBTickler.Core.Data;

/// <summary>
/// Why an operation failed. The distinction matters: a deadlock victim during a chaos run is
/// the tool working as intended, while a login failure means the run is not testing anything.
/// </summary>
public enum SqlFailureKind
{
    /// <summary>Not a failure the classifier recognises.</summary>
    Other,

    /// <summary>Chosen as a deadlock victim (error 1205).</summary>
    DeadlockVictim,

    /// <summary>Lock request timed out (error 1222).</summary>
    LockTimeout,

    /// <summary>Client-side command timeout — usually blocking, sometimes a slow plan.</summary>
    CommandTimeout,

    /// <summary>Connection could not be established or was dropped.</summary>
    Connection,

    /// <summary>Object or column referenced by the workload does not exist on the target.</summary>
    MissingObject,

    /// <summary>Caller lacks rights for the statement.</summary>
    Permission,

    /// <summary>Session was killed — expected when the operator stops a run.</summary>
    Killed,

    /// <summary>Constraint violation, arithmetic overflow, conversion failure.</summary>
    DataError,

    /// <summary>Server ran out of a resource (tempdb, log, memory).</summary>
    ResourceExhausted,
}

public static class SqlErrorClassifier
{
    /// <summary>Errors worth retrying once: transient by nature and not caused by the workload text.</summary>
    public static bool IsTransient(SqlFailureKind kind) =>
        kind is SqlFailureKind.DeadlockVictim or SqlFailureKind.LockTimeout or SqlFailureKind.Connection;

    /// <summary>
    /// True when the failure means the workload itself cannot run here — retrying or continuing
    /// just produces the same error thousands of times.
    /// </summary>
    public static bool IsFatalForRun(SqlFailureKind kind) =>
        kind is SqlFailureKind.MissingObject or SqlFailureKind.Permission;

    public static SqlFailureKind Classify(Exception exception)
    {
        if (exception is SqlException sqlException)
            return ClassifySqlException(sqlException);

        if (exception is TimeoutException)
            return SqlFailureKind.CommandTimeout;

        if (exception is InvalidOperationException invalid &&
            invalid.Message.Contains("pool", StringComparison.OrdinalIgnoreCase))
            return SqlFailureKind.Connection;

        return SqlFailureKind.Other;
    }

    private static SqlFailureKind ClassifySqlException(SqlException exception)
    {
        // A SqlException carries every error the server raised; the first one is the trigger.
        foreach (SqlError error in exception.Errors)
        {
            var kind = ClassifyErrorNumber(error.Number);
            if (kind != SqlFailureKind.Other)
                return kind;
        }

        return ClassifyErrorNumber(exception.Number);
    }

    private static SqlFailureKind ClassifyErrorNumber(int number) => number switch
    {
        1205 => SqlFailureKind.DeadlockVictim,
        1222 => SqlFailureKind.LockTimeout,
        -2 => SqlFailureKind.CommandTimeout,

        // Connection establishment and transport-level drops.
        2 or 53 or 121 or 233 or 258 or 10053 or 10054 or 10060 or 10061 or 11001
            or 4060 or 4062 or 4063 or 4064 or 18456 or 18452 or 40613 or 40197 or 40501
            => SqlFailureKind.Connection,

        // Object/column resolution.
        207 or 208 or 1088 or 2706 or 2812 or 4104 => SqlFailureKind.MissingObject,

        // Rights.
        229 or 230 or 262 or 297 or 300 or 916 => SqlFailureKind.Permission,

        // Session terminated by KILL or by the server shutting the connection.
        596 or 3617 or 6104 => SqlFailureKind.Killed,

        // Constraint, conversion, arithmetic.
        8152 or 8114 or 8115 or 8134 or 245 or 241 or 2601 or 2627 or 515 or 547
            => SqlFailureKind.DataError,

        // Space and memory.
        701 or 802 or 1101 or 1105 or 1121 or 8645 or 9002 => SqlFailureKind.ResourceExhausted,

        _ => SqlFailureKind.Other,
    };

    /// <summary>Server error number, when the exception carries one.</summary>
    public static int? ErrorNumber(Exception exception) =>
        exception is SqlException sql ? sql.Number : null;

    /// <summary>
    /// A short, stable label for grouping errors in a report. Server messages embed row counts
    /// and object names, so grouping on the raw text produces thousands of distinct "errors".
    /// </summary>
    public static string DescribeKind(SqlFailureKind kind) => kind switch
    {
        SqlFailureKind.DeadlockVictim => "Deadlock victim (1205)",
        SqlFailureKind.LockTimeout => "Lock request timeout (1222)",
        SqlFailureKind.CommandTimeout => "Command timeout",
        SqlFailureKind.Connection => "Connection failure",
        SqlFailureKind.MissingObject => "Missing object or column",
        SqlFailureKind.Permission => "Permission denied",
        SqlFailureKind.Killed => "Session killed",
        SqlFailureKind.DataError => "Data error",
        SqlFailureKind.ResourceExhausted => "Server resource exhausted",
        _ => "Other",
    };
}
