namespace DBTickler.Core.Data;

/// <summary>A parameter bound to a statement. Values are always parameterised, never interpolated.</summary>
public readonly record struct SqlParameterValue(string Name, object? Value);

/// <summary>
/// One statement to run, along with how the caller wants its results handled.
/// </summary>
public sealed class SqlRequest
{
    public required string Sql { get; init; }

    public IReadOnlyList<SqlParameterValue> Parameters { get; init; } = [];

    public int TimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// When true the statement is executed as a reader and rows are drained; when false it is
    /// executed as a non-query. Draining matters for load generation: without it the server can
    /// stop producing rows early and the measured cost is not the cost of the query.
    /// </summary>
    public bool ReadsResults { get; init; } = true;

    /// <summary>Stop draining after this many rows. Guards against a runaway result set.</summary>
    public int MaxRows { get; init; } = int.MaxValue;

    public override string ToString() => Sql;
}

/// <summary>What a single executed statement cost and returned.</summary>
public readonly record struct SqlExecutionResult(long RowsAffected, long RowsRead);
