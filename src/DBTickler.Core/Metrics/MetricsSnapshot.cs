using DBTickler.Core.Data;

namespace DBTickler.Core.Metrics;

/// <summary>Latency percentiles in milliseconds.</summary>
public readonly record struct LatencySummary(
    double P50, double P90, double P95, double P99, double P999, double Max, double Mean, long Count)
{
    public static LatencySummary Empty => new(0, 0, 0, 0, 0, 0, 0, 0);

    public static LatencySummary FromHistogram(LatencyHistogram histogram)
    {
        const double UsPerMs = 1000.0;
        return new LatencySummary(
            P50: histogram.GetValueAtPercentile(50) / UsPerMs,
            P90: histogram.GetValueAtPercentile(90) / UsPerMs,
            P95: histogram.GetValueAtPercentile(95) / UsPerMs,
            P99: histogram.GetValueAtPercentile(99) / UsPerMs,
            P999: histogram.GetValueAtPercentile(99.9) / UsPerMs,
            Max: histogram.MaxValue / UsPerMs,
            Mean: histogram.MeanValue / UsPerMs,
            Count: histogram.TotalCount);
    }
}

/// <summary>Totals for one operation category.</summary>
public sealed record OperationStats(
    OperationKind Kind,
    long Operations,
    long Errors,
    long Rows,
    LatencySummary Latency)
{
    public static OperationStats Empty(OperationKind kind) =>
        new(kind, 0, 0, 0, LatencySummary.Empty);
}

/// <summary>One second of the run, for the throughput chart and the exported series.</summary>
public readonly record struct ThroughputSample(int Second, long Operations, long Errors, double MeanLatencyMs);

/// <summary>
/// A consistent-enough view of the run for display or export. Counters are read without a
/// global lock, so totals can be off by the handful of operations in flight at the instant
/// of the read; that is deliberate, since taking a lock on every operation to make a
/// progress display exact would change what is being measured.
/// </summary>
public sealed record MetricsSnapshot
{
    public required TimeSpan Elapsed { get; init; }
    public required long TotalOperations { get; init; }
    public required long TotalErrors { get; init; }
    public required long TotalRows { get; init; }
    public required int ActiveUsers { get; init; }
    public required LatencySummary Latency { get; init; }
    public required IReadOnlyList<OperationStats> ByKind { get; init; }
    public required IReadOnlyDictionary<SqlFailureKind, long> ErrorsByKind { get; init; }
    public required IReadOnlyDictionary<int, long> ErrorsByNumber { get; init; }
    public required IReadOnlyList<ThroughputSample> Series { get; init; }

    /// <summary>Operations per second averaged across the whole run so far.</summary>
    public double OperationsPerSecond =>
        Elapsed.TotalSeconds > 0 ? TotalOperations / Elapsed.TotalSeconds : 0;

    /// <summary>Operations per second over the last <paramref name="seconds"/> whole seconds.</summary>
    public double RecentOperationsPerSecond(int seconds = 5)
    {
        if (Series.Count == 0) return 0;
        var take = Math.Min(seconds, Series.Count);
        long sum = 0;
        for (var i = Series.Count - take; i < Series.Count; i++)
            sum += Series[i].Operations;
        return (double)sum / take;
    }

    public double ErrorRate => TotalOperations + TotalErrors > 0
        ? (double)TotalErrors / (TotalOperations + TotalErrors)
        : 0;

    public long DeadlockVictims => ErrorsByKind.GetValueOrDefault(SqlFailureKind.DeadlockVictim);
    public long Timeouts => ErrorsByKind.GetValueOrDefault(SqlFailureKind.CommandTimeout)
                          + ErrorsByKind.GetValueOrDefault(SqlFailureKind.LockTimeout);

    public static MetricsSnapshot Empty { get; } = new()
    {
        Elapsed = TimeSpan.Zero,
        TotalOperations = 0,
        TotalErrors = 0,
        TotalRows = 0,
        ActiveUsers = 0,
        Latency = LatencySummary.Empty,
        ByKind = [],
        ErrorsByKind = new Dictionary<SqlFailureKind, long>(),
        ErrorsByNumber = new Dictionary<int, long>(),
        Series = [],
    };
}
