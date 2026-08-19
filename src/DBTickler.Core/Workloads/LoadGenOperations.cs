using DBTickler.Core.Data;
using DBTickler.Core.Metrics;

namespace DBTickler.Core.Workloads;

/// <summary>
/// Reads and writes against the tool's own table. These work on any database once
/// <c>dbo.LoadGen</c> exists, which is what makes DBTickler usable outside AdventureWorks,
/// and they are the only place the tool ever modifies data.
/// </summary>
public static class LoadGenOperations
{
    public static IEnumerable<WorkloadOperation> Reads() =>
    [
        new WorkloadOperation
        {
            Name = "LoadGen point lookup",
            Kind = OperationKind.Read,
            Explanation = "Clustered index seek on a single row — the cheapest possible read.",
            Build = context => new SqlRequest
            {
                Sql = "SELECT Id, Tag, CreatedAt FROM dbo.LoadGen WHERE Id = @id",
                Parameters = [new SqlParameterValue("@id", RandomExistingId(context))],
                TimeoutSeconds = context.Timeout,
                MaxRows = context.MaxRows,
            },
        },

        new WorkloadOperation
        {
            Name = "LoadGen range scan",
            Kind = OperationKind.Read,
            Explanation = "Clustered index range scan over a contiguous key band.",
            Build = context =>
            {
                var start = RandomExistingId(context);
                return new SqlRequest
                {
                    Sql = "SELECT TOP (@top) Id, Tag, CreatedAt FROM dbo.LoadGen WHERE Id >= @start ORDER BY Id",
                    Parameters =
                    [
                        new SqlParameterValue("@top", Math.Min(context.MaxRows, context.Profile.BatchRows * 10)),
                        new SqlParameterValue("@start", start),
                    ],
                    TimeoutSeconds = context.Timeout,
                    MaxRows = context.MaxRows,
                };
            },
        },

        new WorkloadOperation
        {
            Name = "LoadGen tag seek",
            Kind = OperationKind.Read,
            Explanation = "Nonclustered index seek with a key lookup back to the base table.",
            Build = context => new SqlRequest
            {
                Sql = "SELECT TOP (@top) Id, Tag, CreatedAt FROM dbo.LoadGen WHERE Tag = @tag",
                Parameters =
                [
                    new SqlParameterValue("@top", Math.Min(context.MaxRows, 100)),
                    new SqlParameterValue("@tag", PayloadGenerator.NextTag(context.Random)),
                ],
                TimeoutSeconds = context.Timeout,
                MaxRows = context.MaxRows,
            },
        },

        new WorkloadOperation
        {
            Name = "LoadGen blob read",
            Kind = OperationKind.Read,
            Explanation = "Pulls LOB data back to the client — exercises read-ahead and network throughput.",
            Build = context => new SqlRequest
            {
                Sql = """
                    SELECT TOP (@top) Id, DATALENGTH(TextData) AS TextBytes, DATALENGTH(BinaryData) AS BinaryBytes
                    FROM dbo.LoadGen
                    WHERE Id >= @start
                    ORDER BY Id
                    """,
                Parameters =
                [
                    new SqlParameterValue("@top", Math.Min(context.MaxRows, context.Profile.BatchRows)),
                    new SqlParameterValue("@start", RandomExistingId(context)),
                ],
                TimeoutSeconds = context.Timeout,
                MaxRows = context.MaxRows,
            },
        },

        new WorkloadOperation
        {
            Name = "LoadGen aggregate",
            Kind = OperationKind.Read,
            Explanation = "Full scan with aggregation — drives CPU and buffer-pool reads.",
            Build = context => new SqlRequest
            {
                Sql = """
                    SELECT COUNT_BIG(*) AS Rows, MIN(Id) AS MinId, MAX(Id) AS MaxId,
                           AVG(DATALENGTH(BinaryData) * 1.0) AS AvgBytes
                    FROM dbo.LoadGen
                    """,
                TimeoutSeconds = context.Timeout,
                MaxRows = context.MaxRows,
            },
        },
    ];

    public static WorkloadOperation Insert() => new()
    {
        Name = "LoadGen insert",
        Kind = OperationKind.Insert,
        Explanation = "Multi-row insert at the end of the clustered index — log flush and PAGELATCH territory.",
        Build = context =>
        {
            var rows = Math.Clamp(context.Profile.BatchRows, 1, 1000);
            var values = new List<string>(rows);
            var parameters = new List<SqlParameterValue>(rows * 3);

            for (var i = 0; i < rows; i++)
            {
                values.Add($"(@tag{i}, @text{i}, @bin{i})");
                parameters.Add(new SqlParameterValue($"@tag{i}", PayloadGenerator.NextTag(context.Random)));
                parameters.Add(new SqlParameterValue($"@text{i}", context.Payload.NextText(context.Random)));
                parameters.Add(new SqlParameterValue($"@bin{i}", context.Payload.NextBytes(context.Random)));
            }

            return new SqlRequest
            {
                Sql = "INSERT INTO dbo.LoadGen (Tag, TextData, BinaryData) VALUES " + string.Join(", ", values),
                Parameters = parameters,
                TimeoutSeconds = context.Timeout,
                ReadsResults = false,
            };
        },
    };

    public static WorkloadOperation Update() => new()
    {
        Name = "LoadGen update",
        Kind = OperationKind.Update,
        Explanation = "Updates a key range in place — row/page locks, LOB rewrites, page splits.",
        Build = context => new SqlRequest
        {
            // Bounded by an explicit key range so two users updating concurrently contend on
            // real overlapping rows instead of an arbitrary UPDATE TOP with no predicate,
            // which gives the optimiser licence to touch any rows it likes.
            Sql = """
                UPDATE TOP (@rows) dbo.LoadGen
                   SET Tag = @tag, TextData = @text, BinaryData = @bin, UpdatedAt = SYSUTCDATETIME()
                 WHERE Id >= @start
                """,
            Parameters =
            [
                new SqlParameterValue("@rows", Math.Clamp(context.Profile.BatchRows, 1, 10000)),
                new SqlParameterValue("@start", RandomExistingId(context)),
                new SqlParameterValue("@tag", PayloadGenerator.NextTag(context.Random)),
                new SqlParameterValue("@text", context.Payload.NextText(context.Random)),
                new SqlParameterValue("@bin", context.Payload.NextBytes(context.Random)),
            ],
            TimeoutSeconds = context.Timeout,
            ReadsResults = false,
        },
    };

    public static WorkloadOperation Delete() => new()
    {
        Name = "LoadGen delete",
        Kind = OperationKind.Delete,
        Explanation = "Deletes recently inserted rows, keeping the table from growing without bound.",
        Build = context => new SqlRequest
        {
            // Deletes are aimed above the seed rows so the anchors the deadlock and hotspot
            // operations depend on are never removed mid-run.
            Sql = """
                DELETE TOP (@rows) FROM dbo.LoadGen
                 WHERE Id > @floor
                """,
            Parameters =
            [
                new SqlParameterValue("@rows", Math.Clamp(context.Profile.BatchRows, 1, 10000)),
                new SqlParameterValue("@floor", SetupScript.ReservedIdCount),
            ],
            TimeoutSeconds = context.Timeout,
            ReadsResults = false,
        },
    };

    /// <summary>
    /// Picks an Id likely to exist. Row counts come from partition metadata at probe time, so
    /// this is an estimate; operations that miss simply return no rows, which is a realistic
    /// outcome rather than an error.
    /// </summary>
    private static int RandomExistingId(OperationContext context)
    {
        var upper = (int)Math.Clamp(context.Schema.LoadGenRowCount, 1, int.MaxValue - 1);
        return context.Random.Next(1, upper + 1);
    }
}
