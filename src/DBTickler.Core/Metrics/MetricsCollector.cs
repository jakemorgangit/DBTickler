using System.Collections.Concurrent;
using System.Diagnostics;
using DBTickler.Core.Data;

namespace DBTickler.Core.Metrics;

/// <summary>
/// Collects every operation's outcome and latency. Shared by all virtual users, so all
/// mutation goes through interlocked operations on preallocated arrays — no locks, no
/// allocation on the recording path, and nothing that grows with the operation count.
///
/// Elapsed time comes from a <see cref="Stopwatch"/> rather than wall-clock time: a run
/// measured with DateTime.Now silently reports the wrong duration and throughput if the
/// clock is adjusted mid-run.
/// </summary>
public sealed class MetricsCollector
{
    private const int MaxSeriesSeconds = 86_400;

    private readonly Stopwatch _clock = new();
    private readonly LatencyHistogram[] _latencyByKind;
    private readonly long[] _opsByKind;
    private readonly long[] _errorsByKind;
    private readonly long[] _rowsByKind;
    private readonly long[] _failureCounts;
    private readonly ConcurrentDictionary<int, long> _errorsByNumber = new();

    private readonly long[] _seriesOps;
    private readonly long[] _seriesErrors;
    private readonly long[] _seriesLatencySumUs;
    private readonly int _seriesCapacity;

    private long _totalOperations;
    private long _totalErrors;
    private long _totalRows;
    private int _activeUsers;

    public MetricsCollector(int expectedDurationSeconds = 0)
    {
        var kindCount = OperationKindExtensions.All.Length;
        _latencyByKind = new LatencyHistogram[kindCount];
        for (var i = 0; i < kindCount; i++)
            _latencyByKind[i] = new LatencyHistogram();

        _opsByKind = new long[kindCount];
        _errorsByKind = new long[kindCount];
        _rowsByKind = new long[kindCount];
        _failureCounts = new long[Enum.GetValues<SqlFailureKind>().Length];

        _seriesCapacity = expectedDurationSeconds > 0
            ? Math.Min(MaxSeriesSeconds, expectedDurationSeconds + 300)
            : MaxSeriesSeconds;
        _seriesOps = new long[_seriesCapacity];
        _seriesErrors = new long[_seriesCapacity];
        _seriesLatencySumUs = new long[_seriesCapacity];
    }

    public TimeSpan Elapsed => _clock.Elapsed;
    public long TotalOperations => Interlocked.Read(ref _totalOperations);
    public long TotalErrors => Interlocked.Read(ref _totalErrors);

    public void Start() => _clock.Restart();
    public void Stop() => _clock.Stop();

    public void UserStarted() => Interlocked.Increment(ref _activeUsers);
    public void UserFinished() => Interlocked.Decrement(ref _activeUsers);

    /// <summary>Records one successful operation.</summary>
    public void RecordSuccess(OperationKind kind, long elapsedMicroseconds, long rows)
    {
        var index = (int)kind;
        _latencyByKind[index].Record(elapsedMicroseconds);
        Interlocked.Increment(ref _opsByKind[index]);
        Interlocked.Add(ref _rowsByKind[index], rows);
        Interlocked.Increment(ref _totalOperations);
        Interlocked.Add(ref _totalRows, rows);

        var second = CurrentSecond();
        Interlocked.Increment(ref _seriesOps[second]);
        Interlocked.Add(ref _seriesLatencySumUs[second], elapsedMicroseconds);
    }

    /// <summary>
    /// Records one failed operation. Failures carry latency too — a command that times out
    /// after 30 seconds is a 30-second data point, and dropping it flatters the percentiles.
    /// </summary>
    public void RecordFailure(OperationKind kind, long elapsedMicroseconds, SqlFailureKind failure, int? errorNumber)
    {
        var index = (int)kind;
        _latencyByKind[index].Record(elapsedMicroseconds);
        Interlocked.Increment(ref _errorsByKind[index]);
        Interlocked.Increment(ref _totalErrors);
        Interlocked.Increment(ref _failureCounts[(int)failure]);

        if (errorNumber is { } number)
            _errorsByNumber.AddOrUpdate(number, 1, static (_, existing) => existing + 1);

        var second = CurrentSecond();
        Interlocked.Increment(ref _seriesErrors[second]);
    }

    private int CurrentSecond()
    {
        var second = (int)_clock.Elapsed.TotalSeconds;
        if (second < 0) return 0;
        return second >= _seriesCapacity ? _seriesCapacity - 1 : second;
    }

    public MetricsSnapshot Snapshot()
    {
        var elapsed = _clock.Elapsed;

        var byKind = new List<OperationStats>(_latencyByKind.Length);
        var merged = new LatencyHistogram();

        foreach (var kind in OperationKindExtensions.All)
        {
            var index = (int)kind;
            var histogram = _latencyByKind[index].Snapshot();
            merged.Add(histogram);

            var operations = Interlocked.Read(ref _opsByKind[index]);
            var errors = Interlocked.Read(ref _errorsByKind[index]);
            if (operations == 0 && errors == 0)
                continue;

            byKind.Add(new OperationStats(
                kind,
                operations,
                errors,
                Interlocked.Read(ref _rowsByKind[index]),
                LatencySummary.FromHistogram(histogram)));
        }

        var errorsByKind = new Dictionary<SqlFailureKind, long>();
        foreach (var failure in Enum.GetValues<SqlFailureKind>())
        {
            var count = Interlocked.Read(ref _failureCounts[(int)failure]);
            if (count > 0) errorsByKind[failure] = count;
        }

        return new MetricsSnapshot
        {
            Elapsed = elapsed,
            TotalOperations = Interlocked.Read(ref _totalOperations),
            TotalErrors = Interlocked.Read(ref _totalErrors),
            TotalRows = Interlocked.Read(ref _totalRows),
            ActiveUsers = Volatile.Read(ref _activeUsers),
            Latency = LatencySummary.FromHistogram(merged),
            ByKind = byKind,
            ErrorsByKind = errorsByKind,
            ErrorsByNumber = new Dictionary<int, long>(_errorsByNumber),
            Series = BuildSeries(elapsed),
        };
    }

    private List<ThroughputSample> BuildSeries(TimeSpan elapsed)
    {
        // Only whole elapsed seconds are complete; the in-progress second would render as a
        // dip on the chart and read as a throughput collapse that never happened.
        var completeSeconds = Math.Min((int)elapsed.TotalSeconds, _seriesCapacity);
        var series = new List<ThroughputSample>(Math.Max(0, completeSeconds));

        for (var second = 0; second < completeSeconds; second++)
        {
            var ops = Interlocked.Read(ref _seriesOps[second]);
            var errors = Interlocked.Read(ref _seriesErrors[second]);
            var latencySum = Interlocked.Read(ref _seriesLatencySumUs[second]);
            series.Add(new ThroughputSample(
                second,
                ops,
                errors,
                ops > 0 ? latencySum / (double)ops / 1000.0 : 0));
        }

        return series;
    }
}
