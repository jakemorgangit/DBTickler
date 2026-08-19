using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DBTickler.Core.Configuration;
using DBTickler.Core.Data;
using DBTickler.Core.Metrics;
using DBTickler.Core.Workloads;

namespace DBTickler.Core.Engine;

/// <summary>
/// The record of a completed run. Exportable so results can be diffed between runs or kept
/// as evidence alongside a change — the point of a repeatable load generator is comparing
/// today's numbers with last week's, which v1 had no way to support.
/// </summary>
public sealed record RunReport
{
    public required DateTimeOffset StartedAt { get; init; }
    public required TimeSpan Duration { get; init; }
    public required string Target { get; init; }
    public required string ServerDescription { get; init; }
    public required string WorkloadSource { get; init; }
    public required StopReason StopReason { get; init; }
    public string? StopDetail { get; init; }

    public required int VirtualUsers { get; init; }
    public required bool SafeMode { get; init; }
    public required bool ChaosMode { get; init; }
    public required int? RandomSeed { get; init; }

    public required long TotalOperations { get; init; }
    public required long TotalErrors { get; init; }
    public required long TotalRows { get; init; }
    public required double OperationsPerSecond { get; init; }
    public required double ErrorRate { get; init; }
    public required LatencySummary Latency { get; init; }
    public required IReadOnlyList<OperationStats> ByKind { get; init; }
    public required IReadOnlyDictionary<string, long> ErrorsByKind { get; init; }
    public required IReadOnlyDictionary<int, long> ErrorsByNumber { get; init; }
    public required IReadOnlyList<ThroughputSample> Series { get; init; }

    public static RunReport Create(
        DateTimeOffset startedAt,
        MetricsSnapshot snapshot,
        WorkloadProfile profile,
        WorkloadPlan plan,
        string target,
        StopReason stopReason,
        string? stopDetail)
    {
        return new RunReport
        {
            StartedAt = startedAt,
            Duration = snapshot.Elapsed,
            Target = target,
            ServerDescription = plan.Schema.Server.Describe(),
            WorkloadSource = plan.Schema.DescribeWorkloadSource(),
            StopReason = stopReason,
            StopDetail = stopDetail,
            VirtualUsers = profile.VirtualUsers,
            SafeMode = profile.SafeMode,
            ChaosMode = profile.ChaosMode,
            RandomSeed = profile.RandomSeed,
            TotalOperations = snapshot.TotalOperations,
            TotalErrors = snapshot.TotalErrors,
            TotalRows = snapshot.TotalRows,
            OperationsPerSecond = snapshot.OperationsPerSecond,
            ErrorRate = snapshot.ErrorRate,
            Latency = snapshot.Latency,
            ByKind = snapshot.ByKind,
            ErrorsByKind = snapshot.ErrorsByKind.ToDictionary(
                pair => SqlErrorClassifier.DescribeKind(pair.Key),
                pair => pair.Value),
            ErrorsByNumber = snapshot.ErrorsByNumber,
            Series = snapshot.Series,
        };
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>The per-second throughput series, for charting elsewhere.</summary>
    public string SeriesToCsv()
    {
        var builder = new StringBuilder();
        builder.AppendLine("second,operations,errors,mean_latency_ms");
        foreach (var sample in Series)
        {
            builder.Append(sample.Second).Append(',')
                   .Append(sample.Operations).Append(',')
                   .Append(sample.Errors).Append(',')
                   .Append(sample.MeanLatencyMs.ToString("F3", CultureInfo.InvariantCulture))
                   .AppendLine();
        }
        return builder.ToString();
    }

    /// <summary>One row per operation category, plus a total — the shape you would paste into a ticket.</summary>
    public string SummaryToCsv()
    {
        var builder = new StringBuilder();
        builder.AppendLine("category,operations,errors,rows,p50_ms,p95_ms,p99_ms,max_ms");

        foreach (var stats in ByKind)
            AppendRow(builder, stats.Kind.DisplayName(), stats.Operations, stats.Errors, stats.Rows, stats.Latency);

        AppendRow(builder, "TOTAL", TotalOperations, TotalErrors, TotalRows, Latency);
        return builder.ToString();

        static void AppendRow(StringBuilder builder, string name, long operations, long errors, long rows, LatencySummary latency)
        {
            builder.Append(Escape(name)).Append(',')
                   .Append(operations).Append(',')
                   .Append(errors).Append(',')
                   .Append(rows).Append(',')
                   .Append(latency.P50.ToString("F2", CultureInfo.InvariantCulture)).Append(',')
                   .Append(latency.P95.ToString("F2", CultureInfo.InvariantCulture)).Append(',')
                   .Append(latency.P99.ToString("F2", CultureInfo.InvariantCulture)).Append(',')
                   .Append(latency.Max.ToString("F2", CultureInfo.InvariantCulture))
                   .AppendLine();
        }
    }

    private static string Escape(string value) =>
        value.Contains(',') || value.Contains('"')
            ? '"' + value.Replace("\"", "\"\"") + '"'
            : value;

    /// <summary>A short human-readable summary, used by the CLI and the finished-run log line.</summary>
    public string ToText()
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Target        : {Target}");
        builder.AppendLine($"Server        : {ServerDescription}");
        builder.AppendLine($"Workload      : {WorkloadSource}");
        builder.AppendLine($"Started       : {StartedAt:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine($"Duration      : {Duration.TotalSeconds:F1} s");
        builder.AppendLine($"Virtual users : {VirtualUsers}{(SafeMode ? " (safe mode)" : "")}{(ChaosMode ? " (chaos)" : "")}");
        if (RandomSeed is { } seed)
            builder.AppendLine($"Seed          : {seed}");
        builder.AppendLine($"Stopped       : {StopReason.Describe()}{(StopDetail is null ? "" : $" — {StopDetail}")}");
        builder.AppendLine();
        builder.AppendLine($"Operations    : {TotalOperations:N0} ({OperationsPerSecond:F1}/s)");
        builder.AppendLine($"Errors        : {TotalErrors:N0} ({ErrorRate:P2})");
        builder.AppendLine($"Rows          : {TotalRows:N0}");
        builder.AppendLine();
        builder.AppendLine("Latency (ms)  : "
            + $"p50 {Latency.P50:F1}   p90 {Latency.P90:F1}   p95 {Latency.P95:F1}   "
            + $"p99 {Latency.P99:F1}   p99.9 {Latency.P999:F1}   max {Latency.Max:F1}");

        if (ByKind.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("By category:");
            foreach (var stats in ByKind)
            {
                builder.AppendLine(
                    $"  {stats.Kind.DisplayName(),-12} {stats.Operations,10:N0} ops  {stats.Errors,8:N0} err  " +
                    $"p95 {stats.Latency.P95,8:F1} ms");
            }
        }

        if (ErrorsByKind.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Errors:");
            foreach (var (kind, count) in ErrorsByKind.OrderByDescending(pair => pair.Value))
                builder.AppendLine($"  {kind,-32} {count,10:N0}");
        }

        return builder.ToString();
    }
}
