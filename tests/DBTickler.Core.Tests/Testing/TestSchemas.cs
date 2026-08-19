using DBTickler.Core.Workloads;

namespace DBTickler.Core.Tests.Testing;

/// <summary>Small factory helpers for <see cref="SchemaCapabilities"/> used across the suite.</summary>
internal static class TestSchemas
{
    public static SchemaCapabilities LoadGenOnly(long rows = 1000) => new()
    {
        Server = ServerInfo.Unknown,
        HasLoadGenTable = true,
        LoadGenRowCount = rows,
        HasAdventureWorks = false,
        AdventureWorksTablesFound = [],
        Tables = [],
    };

    public static SchemaCapabilities Empty() => SchemaCapabilities.Empty;
}
