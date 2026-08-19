using System.Reflection;
using System.Text.Json;
using DBTickler.Core.Configuration;
using DBTickler.Core.Data;
using DBTickler.Core.Engine;
using DBTickler.Core.Metrics;
using DBTickler.Core.Tests.Testing;
using DBTickler.Core.Workloads;

namespace DBTickler.Core.Tests.Engine;

public class RunReportTests
{
    private static RunReport BuildSampleReport()
    {
        var profile = WorkloadProfile.Oltp();
        var plan = WorkloadPlan.Build(profile, TestSchemas.LoadGenOnly());

        var metrics = new MetricsCollector(expectedDurationSeconds: 5);
        metrics.Start();
        metrics.RecordSuccess(OperationKind.Read, elapsedMicroseconds: 1_000, rows: 10);
        metrics.RecordSuccess(OperationKind.Read, elapsedMicroseconds: 2_000, rows: 5);
        metrics.RecordSuccess(OperationKind.Insert, elapsedMicroseconds: 1_500, rows: 1);
        metrics.RecordFailure(OperationKind.Update, elapsedMicroseconds: 3_000, SqlFailureKind.CommandTimeout, errorNumber: null);
        metrics.RecordFailure(OperationKind.Update, elapsedMicroseconds: 500, SqlFailureKind.DeadlockVictim, errorNumber: 1205);
        // Guarantee at least one complete second so the throughput series is non-empty; the
        // assertions below check shape relative to report.Series.Count rather than a fixed
        // number, so this is not a flaky timing dependency.
        Thread.Sleep(1050);
        metrics.Stop();

        var snapshot = metrics.Snapshot();
        return RunReport.Create(
            startedAt: DateTimeOffset.Now,
            snapshot: snapshot,
            profile: profile,
            plan: plan,
            target: "test-target",
            stopReason: StopReason.DurationReached,
            stopDetail: null);
    }

    [Fact]
    public void ToJson_produces_parseable_json_containing_the_key_figures()
    {
        var report = BuildSampleReport();

        var json = report.ToJson();
        using var document = JsonDocument.Parse(json); // throws on invalid JSON
        var root = document.RootElement;

        Assert.Equal(report.TotalOperations, root.GetProperty("TotalOperations").GetInt64());
        Assert.Equal(report.TotalErrors, root.GetProperty("TotalErrors").GetInt64());
        Assert.Equal("test-target", root.GetProperty("Target").GetString());
        Assert.Equal("DurationReached", root.GetProperty("StopReason").GetString()); // enum serialised as its name
    }

    [Fact]
    public void ToJson_round_trips_operation_counts_correctly()
    {
        var report = BuildSampleReport();
        var json = report.ToJson();
        var root = JsonDocument.Parse(json).RootElement;

        Assert.Equal(3, root.GetProperty("TotalOperations").GetInt64()); // 2 reads + 1 insert
        Assert.Equal(2, root.GetProperty("TotalErrors").GetInt64());
    }

    [Fact]
    public void SummaryToCsv_has_the_documented_header_and_one_row_per_kind_plus_total()
    {
        var report = BuildSampleReport();

        var lines = report.SummaryToCsv()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r'))
            .ToArray();

        Assert.Equal("category,operations,errors,rows,p50_ms,p95_ms,p99_ms,max_ms", lines[0]);
        Assert.Equal(report.ByKind.Count + 2, lines.Length); // header + per-kind rows + TOTAL
        Assert.StartsWith("TOTAL,", lines[^1]);
    }

    [Fact]
    public void SummaryToCsv_total_row_matches_the_report_totals()
    {
        var report = BuildSampleReport();
        var totalLine = report.SummaryToCsv()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r'))
            .Last();

        var fields = totalLine.Split(',');
        Assert.Equal("TOTAL", fields[0]);
        Assert.Equal(report.TotalOperations.ToString(), fields[1]);
        Assert.Equal(report.TotalErrors.ToString(), fields[2]);
    }

    [Fact]
    public void SeriesToCsv_has_the_documented_header_and_one_row_per_series_entry()
    {
        var report = BuildSampleReport();

        var lines = report.SeriesToCsv()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r'))
            .ToArray();

        Assert.Equal("second,operations,errors,mean_latency_ms", lines[0]);
        Assert.Equal(report.Series.Count + 1, lines.Length);
        Assert.True(report.Series.Count > 0, "Expected at least one completed second in the series for this test to be meaningful.");
    }

    [Fact]
    public void ToText_includes_the_key_figures()
    {
        var report = BuildSampleReport();
        var text = report.ToText();

        Assert.Contains("test-target", text);
        Assert.Contains("Operations", text);
        Assert.Contains("Latency (ms)", text);
        Assert.Contains(report.StopReason.Describe(), text);
        Assert.Contains("Errors:", text); // ErrorsByKind is non-empty for this fixture
    }

    [Fact]
    public void ToText_is_not_empty_even_with_no_errors()
    {
        var profile = WorkloadProfile.ReadOnly();
        var plan = WorkloadPlan.Build(profile, TestSchemas.LoadGenOnly());
        var metrics = new MetricsCollector();
        metrics.Start();
        metrics.RecordSuccess(OperationKind.Read, 1000, 1);
        metrics.Stop();

        var report = RunReport.Create(
            DateTimeOffset.Now, metrics.Snapshot(), profile, plan, "t", StopReason.UserRequested, null);

        var text = report.ToText();
        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.DoesNotContain("Errors:", text); // no failures recorded, so the section is omitted
    }

    /// <summary>
    /// The CSV escaping helper is not currently reachable through any public path (no
    /// <see cref="OperationKind.DisplayName"/> or literal category name contains a comma or
    /// quote today), so it is exercised directly via reflection.
    /// </summary>
    public class CsvEscaping
    {
        private static readonly MethodInfo EscapeMethod =
            typeof(RunReport).GetMethod("Escape", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("RunReport.Escape was not found by reflection.");

        private static string Escape(string value) => (string)EscapeMethod.Invoke(null, [value])!;

        [Fact]
        public void Plain_values_are_left_unchanged() =>
            Assert.Equal("plain", Escape("plain"));

        [Fact]
        public void Values_containing_a_comma_are_quoted() =>
            Assert.Equal("\"a,b\"", Escape("a,b"));

        [Fact]
        public void Embedded_quotes_are_doubled_and_the_whole_value_is_quoted() =>
            Assert.Equal("\"has\"\"quote\"", Escape("has\"quote"));

        [Fact]
        public void Empty_string_is_left_unchanged() =>
            Assert.Equal("", Escape(""));
    }
}
