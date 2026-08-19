using DBTickler.Core.Configuration;
using DBTickler.Core.Data;
using DBTickler.Core.Metrics;

namespace DBTickler.Core.Workloads;

/// <summary>
/// Deliberately hostile operations, grouped by what they attack. Each is built only when the
/// target actually supports it, so enabling chaos against a database without the sample
/// schema produces fewer operations rather than a wall of "invalid object name" errors.
/// </summary>
public static class ChaosOperations
{
    /// <summary>Formats a delay for WAITFOR, which needs a time literal rather than a number.</summary>
    internal static string DelayLiteral(TimeSpan delay) =>
        delay.ToString(@"hh\:mm\:ss\.fff");

    public static IReadOnlyList<WorkloadOperation> Build(SchemaCapabilities schema, WorkloadProfile profile)
    {
        var operations = new List<WorkloadOperation>();

        if (profile.ChaosBadQueries)
            operations.AddRange(BadQueries(schema));

        if (profile.ChaosResourceBurners)
            operations.AddRange(ResourceBurners(schema));

        // Concurrency attacks modify data, so they are gated on both safe mode and the
        // presence of the tool's own table — they must never touch a user's tables.
        if (profile.ChaosConcurrency && !profile.SafeMode && schema.CanGenerateWrites)
            operations.AddRange(ConcurrencyAttacks());

        // The read-only half of the concurrency attacks: they take shared locks and hold
        // them, which is enough to demonstrate blocking without writing anything.
        if (profile.ChaosConcurrency && profile.SafeMode && schema.CanGenerateWrites)
            operations.AddRange(ReadOnlyConcurrencyAttacks());

        return operations;
    }

    private static IEnumerable<WorkloadOperation> BadQueries(SchemaCapabilities schema)
    {
        yield return new WorkloadOperation
        {
            Name = "Cartesian explosion",
            Kind = OperationKind.ChaosRead,
            Explanation = "Cross join with no predicate — row count multiplies, the plan has nowhere to hide.",
            Build = context => new SqlRequest
            {
                Sql = schema.HasAdventureWorks
                    ? """
                      SELECT TOP (@top) p.Name, e.JobTitle
                      FROM Person.Person p
                      CROSS JOIN HumanResources.Employee e
                      ORDER BY p.BusinessEntityID, e.BusinessEntityID
                      """
                    : """
                      SELECT TOP (@top) a.name, b.name
                      FROM sys.all_columns a
                      CROSS JOIN sys.all_columns b
                      ORDER BY a.name, b.name
                      """,
                Parameters = [new SqlParameterValue("@top", 50_000)],
                TimeoutSeconds = context.Timeout,
                MaxRows = context.MaxRows,
            },
        };

        yield return new WorkloadOperation
        {
            Name = "Non-SARGable predicate",
            Kind = OperationKind.ChaosRead,
            Explanation = "Wraps the indexed column in a function, so every index becomes unusable.",
            Build = context => new SqlRequest
            {
                Sql = schema.HasAdventureWorks
                    ? """
                      SELECT COUNT_BIG(*)
                      FROM Sales.SalesOrderDetail
                      WHERE SQRT(ABS(LineTotal) + 1) > @threshold
                        AND CAST(ModifiedDate AS VARCHAR(30)) LIKE '%201%'
                      """
                    : """
                      SELECT COUNT_BIG(*)
                      FROM dbo.LoadGen
                      WHERE SQRT(ABS(Id) + 1) > @threshold
                        AND CAST(CreatedAt AS VARCHAR(30)) LIKE '%20%'
                      """,
                Parameters = [new SqlParameterValue("@threshold", context.Random.Next(1, 50))],
                TimeoutSeconds = context.Timeout,
                MaxRows = context.MaxRows,
            },
        };

        if (schema.CanGenerateWrites)
        {
            yield return new WorkloadOperation
            {
                Name = "Implicit conversion scan",
                Kind = OperationKind.ChaosRead,
                Explanation = "Compares CHAR to NVARCHAR, forcing a conversion that defeats the index seek.",
                Build = context => new SqlRequest
                {
                    Sql = "SELECT COUNT_BIG(*) FROM dbo.LoadGen WHERE Tag = @tag",
                    Parameters = [new SqlParameterValue("@tag", PayloadGenerator.NextTag(context.Random))],
                    TimeoutSeconds = context.Timeout,
                    MaxRows = context.MaxRows,
                },
            };
        }
    }

    private static IEnumerable<WorkloadOperation> ResourceBurners(SchemaCapabilities schema)
    {
        yield return new WorkloadOperation
        {
            Name = "CPU burner (hashing)",
            Kind = OperationKind.ChaosRead,
            Explanation = "Cryptographic hashing per row — pure CPU with almost no I/O.",
            Build = context => new SqlRequest
            {
                Sql = """
                    SELECT TOP (@top) HASHBYTES('SHA2_512',
                           CAST(a.object_id AS VARCHAR(50)) + a.name + CAST(NEWID() AS VARCHAR(50)))
                    FROM sys.all_columns a
                    CROSS JOIN sys.all_objects b
                    """,
                Parameters = [new SqlParameterValue("@top", 25_000)],
                TimeoutSeconds = context.Timeout,
                MaxRows = context.MaxRows,
            },
        };

        yield return new WorkloadOperation
        {
            Name = "Memory grant hog",
            Kind = OperationKind.ChaosRead,
            Explanation = "Large sort on a wide key — requests a big memory grant and may spill to tempdb.",
            Build = context => new SqlRequest
            {
                Sql = schema.HasAdventureWorks
                    ? """
                      SELECT TOP (@top) CarrierTrackingNumber, UnitPrice, LineTotal, ModifiedDate
                      FROM Sales.SalesOrderDetail
                      ORDER BY CarrierTrackingNumber, UnitPrice, LineTotal, ModifiedDate DESC
                      OPTION (MAXDOP 1)
                      """
                    : """
                      SELECT TOP (@top) a.name, b.name, a.object_id
                      FROM sys.all_columns a
                      CROSS JOIN sys.all_objects b
                      ORDER BY a.name, b.name, a.object_id
                      OPTION (MAXDOP 1)
                      """,
                Parameters = [new SqlParameterValue("@top", 200_000)],
                TimeoutSeconds = context.Timeout,
                MaxRows = context.MaxRows,
            },
        };

        yield return new WorkloadOperation
        {
            Name = "TempDB pressure",
            Kind = OperationKind.ChaosRead,
            Explanation = "Materialises a large intermediate result into tempdb via a table variable.",
            Build = context => new SqlRequest
            {
                Sql = """
                    SET NOCOUNT ON;
                    DECLARE @spill TABLE (Filler CHAR(200) NOT NULL, SortKey UNIQUEIDENTIFIER NOT NULL);
                    INSERT INTO @spill (Filler, SortKey)
                    SELECT TOP (@rows) REPLICATE('x', 200), NEWID()
                    FROM sys.all_columns a CROSS JOIN sys.all_objects b;
                    SELECT COUNT_BIG(*) FROM @spill;
                    """,
                Parameters = [new SqlParameterValue("@rows", 100_000)],
                TimeoutSeconds = context.Timeout,
                ReadsResults = false,
            },
        };
    }

    private static IEnumerable<WorkloadOperation> ConcurrencyAttacks()
    {
        yield return new WorkloadOperation
        {
            Name = "Deadlock trap",
            Kind = OperationKind.ChaosWrite,
            Explanation = "Takes two row locks in opposite orders on alternating users — a textbook cycle.",
            Build = context =>
            {
                // The ordering alternates with the user index. v1 picked the two orderings at
                // random per operation, so a genuine cycle needed two workers to draw
                // opposite traps at the same moment; splitting by user guarantees that
                // opposing orders really are running concurrently.
                var forward = context.UserIndex % 2 == 0;
                var (first, second) = forward ? (1, 2) : (2, 1);

                return new SqlRequest
                {
                    Sql = $"""
                        SET NOCOUNT ON;
                        BEGIN TRAN;
                            UPDATE dbo.LoadGen SET UpdatedAt = SYSUTCDATETIME() WHERE Id = {first};
                            WAITFOR DELAY '{DelayLiteral(TimeSpan.FromMilliseconds(250))}';
                            UPDATE dbo.LoadGen SET UpdatedAt = SYSUTCDATETIME() WHERE Id = {second};
                        COMMIT TRAN;
                        """,
                    TimeoutSeconds = context.Timeout,
                    ReadsResults = false,
                };
            },
        };

        yield return new WorkloadOperation
        {
            Name = "Blocking chain",
            Kind = OperationKind.ChaosWrite,
            Explanation = "Holds an exclusive row lock through a delay, so every other writer queues behind it.",
            Build = context =>
            {
                var seconds = context.Random.Next(2, 8);
                return new SqlRequest
                {
                    Sql = $"""
                        SET NOCOUNT ON;
                        BEGIN TRAN;
                            UPDATE dbo.LoadGen SET UpdatedAt = SYSUTCDATETIME() WHERE Id = 3;
                            WAITFOR DELAY '{DelayLiteral(TimeSpan.FromSeconds(seconds))}';
                        ROLLBACK TRAN;
                        """,
                    TimeoutSeconds = Math.Max(context.Timeout, seconds + 15),
                    ReadsResults = false,
                };
            },
        };

        yield return new WorkloadOperation
        {
            Name = "Lock escalation",
            Kind = OperationKind.ChaosWrite,
            Explanation = "Updates enough rows in one statement to cross the 5,000-lock escalation threshold.",
            Build = context => new SqlRequest
            {
                Sql = """
                    SET NOCOUNT ON;
                    BEGIN TRAN;
                        UPDATE TOP (6000) dbo.LoadGen SET UpdatedAt = SYSUTCDATETIME() WHERE Id > @floor;
                    ROLLBACK TRAN;
                    """,
                Parameters = [new SqlParameterValue("@floor", SetupScript.ReservedIdCount)],
                TimeoutSeconds = context.Timeout,
                ReadsResults = false,
            },
        };

        yield return new WorkloadOperation
        {
            Name = "Cursor loop",
            Kind = OperationKind.ChaosWrite,
            Explanation = "Row-by-row updates through a cursor — the slow path, held open in a transaction.",
            Build = context => new SqlRequest
            {
                Sql = """
                    SET NOCOUNT ON;
                    DECLARE @id INT;
                    DECLARE loadgen_cursor CURSOR LOCAL FAST_FORWARD FOR
                        SELECT TOP (200) Id FROM dbo.LoadGen WHERE Id > @floor ORDER BY Id;
                    OPEN loadgen_cursor;
                    FETCH NEXT FROM loadgen_cursor INTO @id;
                    WHILE @@FETCH_STATUS = 0
                    BEGIN
                        UPDATE dbo.LoadGen SET UpdatedAt = SYSUTCDATETIME() WHERE Id = @id;
                        FETCH NEXT FROM loadgen_cursor INTO @id;
                    END;
                    CLOSE loadgen_cursor;
                    DEALLOCATE loadgen_cursor;
                    """,
                Parameters = [new SqlParameterValue("@floor", SetupScript.ReservedIdCount)],
                TimeoutSeconds = context.Timeout,
                ReadsResults = false,
            },
        };
    }

    private static IEnumerable<WorkloadOperation> ReadOnlyConcurrencyAttacks()
    {
        yield return new WorkloadOperation
        {
            Name = "Pessimistic read lock",
            Kind = OperationKind.ChaosRead,
            Explanation = "HOLDLOCK on a read, kept through a delay — blocks writers without writing anything.",
            Build = context =>
            {
                var seconds = context.Random.Next(2, 6);
                return new SqlRequest
                {
                    Sql = $"""
                        SET NOCOUNT ON;
                        BEGIN TRAN;
                            SELECT TOP (100) Id FROM dbo.LoadGen WITH (HOLDLOCK) WHERE Id > @floor;
                            WAITFOR DELAY '{DelayLiteral(TimeSpan.FromSeconds(seconds))}';
                        ROLLBACK TRAN;
                        """,
                    Parameters = [new SqlParameterValue("@floor", SetupScript.ReservedIdCount)],
                    TimeoutSeconds = Math.Max(context.Timeout, seconds + 15),
                    ReadsResults = false,
                };
            },
        };
    }
}
