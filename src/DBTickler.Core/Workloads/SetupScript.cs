namespace DBTickler.Core.Workloads;

/// <summary>
/// DDL for the tool's own table. Everything here is idempotent and re-runnable, and it
/// upgrades the v1 layout in place rather than requiring the table to be dropped.
/// </summary>
public static class SetupScript
{
    /// <summary>
    /// Ids reserved for the seeded anchor rows. Delete operations stay above this floor so
    /// the rows the deadlock and hotspot operations depend on always exist.
    /// </summary>
    public const int ReservedIdCount = 8;

    /// <summary>Creates the table, or brings a v1 table up to the current shape.</summary>
    public const string CreateOrUpgrade = """
        SET NOCOUNT ON;

        IF OBJECT_ID('dbo.LoadGen', 'U') IS NULL
        BEGIN
            CREATE TABLE dbo.LoadGen
            (
                Id          INT IDENTITY(1,1) NOT NULL,
                Tag         CHAR(16)          NOT NULL CONSTRAINT DF_LoadGen_Tag DEFAULT (''),
                TextData    NVARCHAR(MAX)     NULL,
                BinaryData  VARBINARY(MAX)    NULL,
                CreatedAt   DATETIME2(3)      NOT NULL CONSTRAINT DF_LoadGen_CreatedAt DEFAULT (SYSUTCDATETIME()),
                UpdatedAt   DATETIME2(3)      NULL,
                CONSTRAINT PK_LoadGen PRIMARY KEY CLUSTERED (Id)
            );
        END;

        -- Bring forward a table created by DBTickler v1, which had a Payload column and no
        -- timestamps. Each step is guarded so the script can be run repeatedly.
        IF COL_LENGTH('dbo.LoadGen', 'Tag') IS NULL
            ALTER TABLE dbo.LoadGen ADD Tag CHAR(16) NOT NULL CONSTRAINT DF_LoadGen_Tag DEFAULT ('');

        IF COL_LENGTH('dbo.LoadGen', 'TextData') IS NULL
            ALTER TABLE dbo.LoadGen ADD TextData NVARCHAR(MAX) NULL;

        IF COL_LENGTH('dbo.LoadGen', 'BinaryData') IS NULL
            ALTER TABLE dbo.LoadGen ADD BinaryData VARBINARY(MAX) NULL;

        IF COL_LENGTH('dbo.LoadGen', 'CreatedAt') IS NULL
            ALTER TABLE dbo.LoadGen ADD CreatedAt DATETIME2(3) NOT NULL CONSTRAINT DF_LoadGen_CreatedAt DEFAULT (SYSUTCDATETIME());

        IF COL_LENGTH('dbo.LoadGen', 'UpdatedAt') IS NULL
            ALTER TABLE dbo.LoadGen ADD UpdatedAt DATETIME2(3) NULL;

        IF NOT EXISTS (SELECT 1 FROM sys.indexes
                       WHERE object_id = OBJECT_ID('dbo.LoadGen', 'U') AND name = 'IX_LoadGen_Tag')
            CREATE NONCLUSTERED INDEX IX_LoadGen_Tag ON dbo.LoadGen (Tag) INCLUDE (CreatedAt);
        """;

    /// <summary>
    /// Seeds the anchor rows. Each row is guarded individually rather than the block being
    /// skipped on a total row count: a table that already holds plenty of rows can still be
    /// missing the low ids, and the deadlock and hotspot operations would then quietly
    /// update nothing.
    /// </summary>
    public static string SeedAnchors { get; } = $"""
        SET NOCOUNT ON;

        IF EXISTS
        (
            SELECT 1
            FROM (SELECT TOP ({ReservedIdCount}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i
                  FROM sys.all_objects) AS required
            WHERE NOT EXISTS (SELECT 1 FROM dbo.LoadGen g WHERE g.Id = required.i)
        )
        BEGIN
            SET IDENTITY_INSERT dbo.LoadGen ON;

            ;WITH n AS (
                SELECT TOP ({ReservedIdCount}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i
                FROM sys.all_objects
            )
            INSERT INTO dbo.LoadGen (Id, Tag, TextData, BinaryData)
            SELECT n.i,
                   RIGHT('0000000000000000' + CAST(n.i AS VARCHAR(16)), 16),
                   REPLICATE(N'anchor ', 100),
                   0x00
            FROM n
            WHERE NOT EXISTS (SELECT 1 FROM dbo.LoadGen g WHERE g.Id = n.i);

            SET IDENTITY_INSERT dbo.LoadGen OFF;
        END;
        """;

    /// <summary>
    /// Bulk-fills the table so reads have something to hit before the first insert lands.
    /// Row size is deliberately modest here; the run's own inserts supply the large payloads.
    /// </summary>
    public const string Prefill = """
        SET NOCOUNT ON;

        DECLARE @target INT = @rows;
        DECLARE @current INT = (SELECT COUNT(*) FROM dbo.LoadGen);

        WHILE @current < @target
        BEGIN
            INSERT INTO dbo.LoadGen (Tag, TextData, BinaryData)
            SELECT TOP (CASE WHEN @target - @current > 5000 THEN 5000 ELSE @target - @current END)
                   RIGHT('0000000000000000' + CAST(ABS(CHECKSUM(NEWID())) AS VARCHAR(16)), 16),
                   REPLICATE(N'x', 200),
                   CAST(NEWID() AS VARBINARY(16))
            FROM sys.all_columns a CROSS JOIN sys.all_columns b;

            SET @current = (SELECT COUNT(*) FROM dbo.LoadGen);
        END;
        """;

    /// <summary>Removes everything the tool created. Offered so a demo can leave no trace.</summary>
    public const string Teardown = """
        IF OBJECT_ID('dbo.LoadGen', 'U') IS NOT NULL
            DROP TABLE dbo.LoadGen;
        """;

    /// <summary>Row count after setup, reported back so the workload can size its key ranges.</summary>
    public const string CountRows = "SELECT COUNT_BIG(*) FROM dbo.LoadGen";
}
