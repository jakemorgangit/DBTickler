using DBTickler.Core.Data;

namespace DBTickler.Core.Workloads;

/// <summary>A user table discovered on the target, with columns the generic workload can use.</summary>
public sealed record ProbedTable(
    string Schema,
    string Name,
    long RowCount,
    string? SortableColumn,
    string? TextColumn)
{
    public string QuotedName => SqlIdentifier.QuoteTwoPart(Schema, Name);
    public override string ToString() => $"{Schema}.{Name} ({RowCount:N0} rows)";
}

/// <summary>Version and edition facts that decide which features the workload may use.</summary>
public sealed record ServerInfo(
    string ServerName,
    string DatabaseName,
    string ProductVersion,
    string Edition,
    int EngineEdition,
    int MajorVersion)
{
    /// <summary>Azure SQL Database (5) and Azure SQL Managed Instance (8) restrict several DMVs and DDL.</summary>
    public bool IsAzureSqlDatabase => EngineEdition == 5;
    public bool IsAzureManagedInstance => EngineEdition == 8;
    public bool IsAzure => IsAzureSqlDatabase || IsAzureManagedInstance;

    /// <summary>system_health's deadlock report is available from SQL Server 2012 onwards.</summary>
    public bool SupportsSystemHealthDeadlocks => MajorVersion >= 11 && !IsAzureSqlDatabase;

    public string Describe() => $"{ServerName} — {Edition} ({ProductVersion})";

    public static ServerInfo Unknown { get; } =
        new("(unknown)", "(unknown)", "0.0.0.0", "(unknown)", 0, 0);
}

/// <summary>
/// What the target database can actually support. The workload catalogue is assembled from
/// this, so pointing DBTickler at a database that is not AdventureWorks degrades to a
/// generic workload instead of failing every single operation.
/// </summary>
public sealed record SchemaCapabilities
{
    public required ServerInfo Server { get; init; }

    /// <summary>dbo.LoadGen exists with the columns the write path needs.</summary>
    public required bool HasLoadGenTable { get; init; }

    public required long LoadGenRowCount { get; init; }

    /// <summary>Every AdventureWorks table the sample workload references is present.</summary>
    public required bool HasAdventureWorks { get; init; }

    /// <summary>Which AdventureWorks tables were found, for diagnostics when only some are.</summary>
    public required IReadOnlyList<string> AdventureWorksTablesFound { get; init; }

    /// <summary>Largest user tables, used by the generic read workload.</summary>
    public required IReadOnlyList<ProbedTable> Tables { get; init; }

    /// <summary>True when at least one read operation can be built.</summary>
    public bool CanGenerateReads => HasAdventureWorks || HasLoadGenTable || Tables.Count > 0;

    /// <summary>Writes need the tool's own table; the workload never modifies a user's tables.</summary>
    public bool CanGenerateWrites => HasLoadGenTable;

    public string DescribeWorkloadSource() => HasAdventureWorks
        ? "AdventureWorks sample schema"
        : Tables.Count > 0
            ? $"generic schema ({Tables.Count} table(s) discovered)"
            : HasLoadGenTable
                ? "dbo.LoadGen only"
                : "none";

    public static SchemaCapabilities Empty { get; } = new()
    {
        Server = ServerInfo.Unknown,
        HasLoadGenTable = false,
        LoadGenRowCount = 0,
        HasAdventureWorks = false,
        AdventureWorksTablesFound = [],
        Tables = [],
    };
}
