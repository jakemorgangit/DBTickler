using DBTickler.Core.Configuration;
using DBTickler.Core.Observability;
using DBTickler.Core.Safety;
using DBTickler.Core.Workloads;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace DBTickler.Core.Tests.Workloads;

/// <summary>
/// Parses every statement DBTickler can issue with Microsoft's own T-SQL parser.
///
/// The rest of the suite can run the engine without a database, but nothing else checks
/// that the SQL it sends is actually valid SQL — a syntax error in a chaos operation or a
/// monitoring query would only surface at runtime, against a real server, in front of a
/// user. Parsing against the SQL Server 2022 grammar catches that here instead.
///
/// This verifies syntax, not semantics: it cannot tell you a column does not exist.
/// </summary>
public class GeneratedSqlTests
{
    private static readonly ServerInfo TestServer =
        new("test", "testdb", "16.0.1000.6", "Developer Edition", 3, 16);

    public static TheoryData<string, string> StaticStatements()
    {
        var data = new TheoryData<string, string>
        {
            { nameof(SetupScript.CreateOrUpgrade), SetupScript.CreateOrUpgrade },
            { nameof(SetupScript.SeedAnchors), SetupScript.SeedAnchors },
            { nameof(SetupScript.Prefill), Declare(SetupScript.Prefill, "@rows INT") },
            { nameof(SetupScript.Teardown), SetupScript.Teardown },
            { nameof(SetupScript.CountRows), SetupScript.CountRows },
            { nameof(SchemaProbe.ServerInfoSql), SchemaProbe.ServerInfoSql },
            { nameof(SchemaProbe.LoadGenSql), SchemaProbe.LoadGenSql },
            { nameof(SchemaProbe.TableDiscoverySql), Declare(SchemaProbe.TableDiscoverySql, "@topN INT", "@minRows BIGINT") },
            { nameof(SchemaProbe.ColumnDiscoverySql), SchemaProbe.ColumnDiscoverySql },
            { nameof(ServerMonitor.ActiveRequestsSql), ServerMonitor.ActiveRequestsSql },
            { nameof(ServerMonitor.KillOwnSessionsSql), Declare(ServerMonitor.KillOwnSessionsSql, "@appName NVARCHAR(128)") },
            { nameof(ServerMonitor.CountOwnSessionsSql), Declare(ServerMonitor.CountOwnSessionsSql, "@appName NVARCHAR(128)") },
            { nameof(WaitStatsMonitor.WaitStatsSql), WaitStatsMonitor.WaitStatsSql },
            { nameof(DeadlockMonitor.RingBufferSql), DeadlockMonitor.RingBufferSql },
            { nameof(ProductionGuard.SignalsSql), Declare(ProductionGuard.SignalsSql, "@appName NVARCHAR(128)") },
        };

        return data;
    }

    [Theory]
    [MemberData(nameof(StaticStatements))]
    public void Static_scripts_are_valid_TSql(string name, string sql)
    {
        var errors = Parse(sql);
        Assert.True(errors.Count == 0, $"{name} failed to parse: {Describe(errors)}\n{sql}");
    }

    [Fact]
    public void Every_workload_operation_produces_valid_TSql()
    {
        var failures = new List<string>();
        var checkedCount = 0;

        foreach (var (schemaName, schema) in Schemas())
        {
            foreach (var (profileName, profile) in Profiles())
            {
                var plan = WorkloadPlan.Build(profile, schema);

                foreach (var operation in plan.AllOperations)
                {
                    // Both parities, so the deadlock trap's alternating lock order is covered
                    // in both directions.
                    foreach (var userIndex in new[] { 0, 1 })
                    {
                        var request = operation.Build(new OperationContext
                        {
                            Random = new Random(1234 + userIndex),
                            Payload = PayloadGenerator.ForRowBudget(profile.PayloadBytes, 99),
                            Profile = profile.Normalized(),
                            Schema = schema,
                            UserIndex = userIndex,
                        });

                        var declarations = request.Parameters
                            .Select(parameter => $"{parameter.Name} {InferType(parameter.Value)}")
                            .ToArray();

                        var errors = Parse(Declare(request.Sql, declarations));
                        checkedCount++;

                        if (errors.Count > 0)
                        {
                            failures.Add(
                                $"{schemaName}/{profileName}/{operation.Name} (user {userIndex}): " +
                                $"{Describe(errors)}\n{request.Sql}");
                        }
                    }
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n\n", failures));

        // Guards against the loop silently covering nothing if plan building changes.
        Assert.True(checkedCount > 100, $"Only {checkedCount} operations were checked; expected far more.");
    }

    [Fact]
    public void Discovered_identifiers_needing_quoting_still_produce_valid_TSql()
    {
        // Table and column names come from the target's own catalogue and are interpolated
        // into generated SQL, so a name containing a closing bracket must not break out.
        var schema = SchemaCapabilities.Empty with
        {
            Server = TestServer,
            Tables =
            [
                new ProbedTable("Sales Data", "Line Items", 5000, "Line Id", "Product Name"),
                new ProbedTable("dbo", "Weird]Name", 5000, "Odd]Column", "Text]Column"),
            ],
        };

        var plan = WorkloadPlan.Build(WorkloadProfile.ReadOnly(), schema);
        Assert.NotEmpty(plan.AllOperations);

        foreach (var operation in plan.AllOperations)
        {
            var request = operation.Build(new OperationContext
            {
                Random = new Random(7),
                Payload = PayloadGenerator.ForRowBudget(1024, 7),
                Profile = WorkloadProfile.ReadOnly(),
                Schema = schema,
                UserIndex = 0,
            });

            var declarations = request.Parameters
                .Select(parameter => $"{parameter.Name} {InferType(parameter.Value)}")
                .ToArray();

            var errors = Parse(Declare(request.Sql, declarations));
            Assert.True(errors.Count == 0,
                $"{operation.Name} failed to parse: {Describe(errors)}\n{request.Sql}");
        }
    }

    private static IList<ParseError> Parse(string sql)
    {
        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(sql);
        parser.Parse(reader, out var errors);
        return errors;
    }

    private static string Describe(IList<ParseError> errors) =>
        string.Join("; ", errors.Select(error => $"line {error.Line} col {error.Column}: {error.Message}"));

    /// <summary>Prefixes DECLAREs so the parser sees bound parameters as declared variables.</summary>
    private static string Declare(string sql, params string[] declarations) =>
        declarations.Length == 0
            ? sql
            : string.Join("\n", declarations.Select(d => $"DECLARE {d};")) + "\n" + sql;

    private static string InferType(object? value) => value switch
    {
        int => "INT",
        long => "BIGINT",
        byte[] => "VARBINARY(MAX)",
        DateTime => "DATETIME2",
        string text => $"NVARCHAR({Math.Clamp(text.Length, 1, 4000)})",
        _ => "NVARCHAR(MAX)",
    };

    private static IEnumerable<(string Name, SchemaCapabilities Schema)> Schemas()
    {
        yield return ("bare", SchemaCapabilities.Empty with { Server = TestServer });

        yield return ("loadgen-only", SchemaCapabilities.Empty with
        {
            Server = TestServer,
            HasLoadGenTable = true,
            LoadGenRowCount = 20_000,
        });

        yield return ("adventureworks", SchemaCapabilities.Empty with
        {
            Server = TestServer,
            HasLoadGenTable = true,
            LoadGenRowCount = 20_000,
            HasAdventureWorks = true,
            AdventureWorksTablesFound = SchemaProbe.AdventureWorksTables,
        });

        yield return ("discovered", SchemaCapabilities.Empty with
        {
            Server = TestServer,
            HasLoadGenTable = true,
            LoadGenRowCount = 20_000,
            Tables =
            [
                new ProbedTable("dbo", "Orders", 100_000, "OrderId", "CustomerName"),
                new ProbedTable("Sales Data", "Line Items", 50_000, "Line Id", "Product Name"),
            ],
        });
    }

    private static IEnumerable<(string Name, WorkloadProfile Profile)> Profiles()
    {
        foreach (var (name, factory) in WorkloadProfile.Presets)
            yield return (name, factory());

        yield return ("chaos-safe", new WorkloadProfile
        {
            SafeMode = true,
            ChaosMode = true,
            ReadPercent = 100, InsertPercent = 0, UpdatePercent = 0, DeletePercent = 0,
        });

        yield return ("chaos-all-writes", new WorkloadProfile
        {
            SafeMode = false,
            ChaosMode = true,
            ChaosIntensityPercent = 100,
            ReadPercent = 0, InsertPercent = 40, UpdatePercent = 40, DeletePercent = 20,
            BatchRows = 250,
            PayloadBytes = 4096,
        });
    }
}
