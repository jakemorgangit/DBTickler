using DBTickler.Core.Data;

namespace DBTickler.Core.Workloads;

/// <summary>
/// Inspects the target before a run so the workload can be built from what is actually
/// there. v1 hard-coded AdventureWorks object names, so any other database produced a
/// 100% error rate with no explanation; probing first turns that into a degraded but
/// working generic workload.
/// </summary>
public sealed class SchemaProbe
{
    /// <summary>
    /// Tables the AdventureWorks workload needs. All must be present to use it — including
    /// the ones only the chaos operations reference, since a partial match would otherwise
    /// pass the probe and then fail on every chaos operation.
    /// </summary>
    public static readonly string[] AdventureWorksTables =
    [
        "Sales.SalesOrderHeader",
        "Sales.SalesOrderDetail",
        "Person.Person",
        "Production.Product",
        "Sales.Customer",
        "Person.Address",
        "HumanResources.Employee",
    ];

    /// <summary>Column types the generic workload can sort and filter on without surprises.</summary>
    private static readonly string[] UsableSortTypes =
        ["int", "bigint", "smallint", "tinyint", "decimal", "numeric", "money",
         "date", "datetime", "datetime2", "smalldatetime", "uniqueidentifier"];

    private static readonly string[] UsableTextTypes = ["varchar", "nvarchar", "char", "nchar"];

    public const string ServerInfoSql = """
        SELECT
            CAST(SERVERPROPERTY('ServerName')     AS NVARCHAR(256)),
            DB_NAME(),
            CAST(SERVERPROPERTY('ProductVersion') AS NVARCHAR(64)),
            CAST(SERVERPROPERTY('Edition')        AS NVARCHAR(128)),
            CAST(SERVERPROPERTY('EngineEdition')  AS INT)
        """;

    public const string LoadGenSql = """
        SELECT
            CASE WHEN OBJECT_ID('dbo.LoadGen', 'U') IS NULL THEN 0 ELSE 1 END,
            ISNULL((SELECT COUNT(*) FROM sys.columns
                    WHERE object_id = OBJECT_ID('dbo.LoadGen', 'U')
                      AND name IN ('Id', 'Tag', 'TextData', 'BinaryData')), 0),
            ISNULL((SELECT SUM(p.rows) FROM sys.partitions p
                    WHERE p.object_id = OBJECT_ID('dbo.LoadGen', 'U') AND p.index_id IN (0, 1)), 0)
        """;

    /// <summary>
    /// Largest user tables with their row counts, taken from partition metadata rather than
    /// COUNT(*) so probing a large database does not itself become a scan.
    /// </summary>
    public const string TableDiscoverySql = """
        SELECT TOP (@topN)
            s.name  AS SchemaName,
            t.name  AS TableName,
            SUM(p.rows) AS [RowCount]
        FROM sys.tables t
        JOIN sys.schemas s ON s.schema_id = t.schema_id
        JOIN sys.partitions p ON p.object_id = t.object_id AND p.index_id IN (0, 1)
        WHERE t.is_ms_shipped = 0
          AND t.temporal_type <> 1
          AND t.name <> 'LoadGen'
        GROUP BY s.name, t.name
        HAVING SUM(p.rows) >= @minRows
        ORDER BY SUM(p.rows) DESC
        """;

    public const string ColumnDiscoverySql = """
        SELECT
            s.name AS SchemaName,
            t.name AS TableName,
            c.name AS ColumnName,
            ty.name AS TypeName,
            c.max_length
        FROM sys.columns c
        JOIN sys.tables t  ON t.object_id = c.object_id
        JOIN sys.schemas s ON s.schema_id = t.schema_id
        JOIN sys.types ty  ON ty.user_type_id = c.user_type_id
        WHERE t.is_ms_shipped = 0
          AND c.is_computed = 0
          AND ty.name NOT IN ('xml', 'text', 'ntext', 'image', 'geography', 'geometry',
                              'hierarchyid', 'sql_variant', 'timestamp')
        ORDER BY s.name, t.name, c.column_id
        """;

    private readonly string _connectionString;

    public SchemaProbe(string connectionString) => _connectionString = connectionString;

    public async Task<SchemaCapabilities> ProbeAsync(
        int maxTables = 8,
        long minRows = 1000,
        CancellationToken cancellationToken = default)
    {
        var server = await ProbeServerAsync(cancellationToken).ConfigureAwait(false);
        var (hasLoadGen, loadGenRows) = await ProbeLoadGenAsync(cancellationToken).ConfigureAwait(false);
        var adventureWorksFound = await ProbeAdventureWorksAsync(cancellationToken).ConfigureAwait(false);
        var tables = await ProbeTablesAsync(maxTables, minRows, cancellationToken).ConfigureAwait(false);

        return new SchemaCapabilities
        {
            Server = server,
            HasLoadGenTable = hasLoadGen,
            LoadGenRowCount = loadGenRows,
            HasAdventureWorks = adventureWorksFound.Count == AdventureWorksTables.Length,
            AdventureWorksTablesFound = adventureWorksFound,
            Tables = tables,
        };
    }

    private async Task<ServerInfo> ProbeServerAsync(CancellationToken cancellationToken)
    {
        var rows = await SqlQuery.ListAsync(
            _connectionString,
            ServerInfoSql,
            reader => new ServerInfo(
                reader.GetNullableString(0) ?? "(unknown)",
                reader.GetNullableString(1) ?? "(unknown)",
                reader.GetNullableString(2) ?? "0.0.0.0",
                reader.GetNullableString(3) ?? "(unknown)",
                reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                0),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (rows.Count == 0) return ServerInfo.Unknown;

        var info = rows[0];
        return info with { MajorVersion = ParseMajorVersion(info.ProductVersion) };
    }

    internal static int ParseMajorVersion(string productVersion)
    {
        if (string.IsNullOrWhiteSpace(productVersion)) return 0;
        var firstPart = productVersion.Split('.', 2)[0];
        return int.TryParse(firstPart, out var major) ? major : 0;
    }

    private async Task<(bool Exists, long Rows)> ProbeLoadGenAsync(CancellationToken cancellationToken)
    {
        var rows = await SqlQuery.ListAsync(
            _connectionString,
            LoadGenSql,
            reader => (
                Exists: reader.GetInt32(0) == 1,
                Columns: reader.GetInt32(1),
                Rows: reader.GetInt64Loose(2)),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (rows.Count == 0) return (false, 0);

        // All four columns must be present: a LoadGen table left over from an older version
        // is missing Tag, and the write workload would fail on every statement.
        var row = rows[0];
        return (row.Exists && row.Columns == 4, row.Rows);
    }

    private async Task<IReadOnlyList<string>> ProbeAdventureWorksAsync(CancellationToken cancellationToken)
    {
        var checks = AdventureWorksTables
            .Select((table, index) =>
                $"SELECT '{table}' AS TableName WHERE OBJECT_ID(@t{index}, 'U') IS NOT NULL")
            .ToArray();

        var parameters = AdventureWorksTables
            .Select((table, index) => new SqlParameterValue($"@t{index}", table))
            .ToArray();

        var found = await SqlQuery.ListAsync(
            _connectionString,
            string.Join(" UNION ALL ", checks),
            reader => reader.GetString(0),
            parameters: parameters,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return found;
    }

    private async Task<IReadOnlyList<ProbedTable>> ProbeTablesAsync(
        int maxTables, long minRows, CancellationToken cancellationToken)
    {
        var candidates = await SqlQuery.ListAsync(
            _connectionString,
            TableDiscoverySql,
            reader => (Schema: reader.GetString(0), Name: reader.GetString(1), Rows: reader.GetInt64Loose(2)),
            parameters:
            [
                new SqlParameterValue("@topN", maxTables),
                new SqlParameterValue("@minRows", minRows),
            ],
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (candidates.Count == 0) return [];

        var columns = await SqlQuery.ListAsync(
            _connectionString,
            ColumnDiscoverySql,
            reader => (
                Schema: reader.GetString(0),
                Table: reader.GetString(1),
                Column: reader.GetString(2),
                Type: reader.GetString(3)),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var columnsByTable = columns
            .GroupBy(c => (c.Schema, c.Table))
            .ToDictionary(g => g.Key, g => g.ToList());

        var result = new List<ProbedTable>(candidates.Count);
        foreach (var candidate in candidates)
        {
            columnsByTable.TryGetValue((candidate.Schema, candidate.Name), out var tableColumns);
            tableColumns ??= [];

            var sortable = tableColumns
                .FirstOrDefault(c => UsableSortTypes.Contains(c.Type, StringComparer.OrdinalIgnoreCase)).Column;
            var text = tableColumns
                .FirstOrDefault(c => UsableTextTypes.Contains(c.Type, StringComparer.OrdinalIgnoreCase)).Column;

            // A table with no usable column can still be scanned, but there is nothing
            // interesting to sort or filter on, so it is a poor load source.
            if (sortable is null && text is null)
                continue;

            result.Add(new ProbedTable(candidate.Schema, candidate.Name, candidate.Rows, sortable, text));
        }

        return result;
    }
}
