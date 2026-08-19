using DBTickler.Core.Data;
using DBTickler.Core.Metrics;

namespace DBTickler.Core.Workloads;

/// <summary>
/// Read operations built at runtime from whatever tables probing discovered. This is what
/// lets DBTickler generate meaningful read load against a database it has never seen —
/// a customer's own schema, a restored backup, anything — instead of requiring the
/// AdventureWorks sample.
///
/// Every one of these is read-only. The tool never modifies a table it did not create.
/// </summary>
public static class GenericTableOperations
{
    public static IEnumerable<WorkloadOperation> Reads(IReadOnlyList<ProbedTable> tables)
    {
        foreach (var table in tables)
        {
            if (table.SortableColumn is { } sortColumn)
            {
                yield return OrderedScan(table, sortColumn);
                yield return RangeAggregate(table, sortColumn);
            }

            if (table.TextColumn is { } textColumn)
                yield return TextSearch(table, textColumn);
        }
    }

    private static WorkloadOperation OrderedScan(ProbedTable table, string sortColumn) => new()
    {
        Name = $"Scan {table.Schema}.{table.Name}",
        Kind = OperationKind.Read,
        Explanation = $"Ordered read of {table.Name} — index scan or sort depending on the plan.",
        Build = context => new SqlRequest
        {
            Sql = $"""
                SELECT TOP (@top) {SqlIdentifier.Quote(sortColumn)}
                FROM {table.QuotedName}
                ORDER BY {SqlIdentifier.Quote(sortColumn)} DESC
                """,
            Parameters = [new SqlParameterValue("@top", Math.Min(context.MaxRows, context.Profile.BatchRows * 10))],
            TimeoutSeconds = context.Timeout,
            MaxRows = context.MaxRows,
        },
    };

    private static WorkloadOperation RangeAggregate(ProbedTable table, string sortColumn) => new()
    {
        Name = $"Aggregate {table.Schema}.{table.Name}",
        Kind = OperationKind.Read,
        Explanation = $"Aggregation over {table.Name} — CPU, memory grant and buffer-pool pressure.",
        Build = context => new SqlRequest
        {
            Sql = $"""
                SELECT COUNT_BIG(*) AS Rows,
                       MIN({SqlIdentifier.Quote(sortColumn)}) AS MinValue,
                       MAX({SqlIdentifier.Quote(sortColumn)}) AS MaxValue
                FROM {table.QuotedName}
                """,
            TimeoutSeconds = context.Timeout,
            MaxRows = context.MaxRows,
        },
    };

    private static WorkloadOperation TextSearch(ProbedTable table, string textColumn) => new()
    {
        Name = $"Search {table.Schema}.{table.Name}",
        Kind = OperationKind.Read,
        Explanation = $"Leading-wildcard search on {table.Name}.{textColumn} — unavoidable scan.",
        Build = context =>
        {
            const string Letters = "abcdefghijklmnopqrstuvwxyz";
            var fragment = Letters[context.Random.Next(Letters.Length)].ToString();
            return new SqlRequest
            {
                Sql = $"""
                    SELECT TOP (@top) {SqlIdentifier.Quote(textColumn)}
                    FROM {table.QuotedName}
                    WHERE {SqlIdentifier.Quote(textColumn)} LIKE @pattern
                    """,
                Parameters =
                [
                    new SqlParameterValue("@top", Math.Min(context.MaxRows, 1000)),
                    new SqlParameterValue("@pattern", "%" + fragment + "%"),
                ],
                TimeoutSeconds = context.Timeout,
                MaxRows = context.MaxRows,
            };
        },
    };

    /// <summary>
    /// A last-resort read that works on any SQL Server with no user tables at all: it scans
    /// system metadata. Keeps a run from producing zero operations on an empty database.
    /// </summary>
    public static WorkloadOperation MetadataScan() => new()
    {
        Name = "Metadata scan",
        Kind = OperationKind.Read,
        Explanation = "Reads system catalogue views — always available, modest cost.",
        Build = context => new SqlRequest
        {
            Sql = """
                SELECT TOP (@top) o.name, c.name, t.name
                FROM sys.all_objects o
                JOIN sys.all_columns c ON c.object_id = o.object_id
                JOIN sys.types t ON t.user_type_id = c.user_type_id
                ORDER BY o.name, c.column_id
                """,
            Parameters = [new SqlParameterValue("@top", Math.Min(context.MaxRows, 5000))],
            TimeoutSeconds = context.Timeout,
            MaxRows = context.MaxRows,
        },
    };
}
