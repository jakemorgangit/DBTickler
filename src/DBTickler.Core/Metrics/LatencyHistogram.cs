using System.Numerics;

namespace DBTickler.Core.Metrics;

/// <summary>
/// A fixed-precision latency histogram using HdrHistogram's bucketing scheme: values are
/// stored in exponentially growing buckets that each hold a fixed number of linear
/// sub-buckets, which keeps relative error bounded across the whole range.
///
/// Recording is an array increment, so a virtual user can record every operation without
/// the measurement itself becoming a bottleneck, and memory stays constant no matter how
/// many operations run. Percentiles are therefore exact to within the configured precision
/// rather than sampled or averaged.
/// </summary>
public sealed class LatencyHistogram
{
    private readonly int _subBucketHalfCountMagnitude;
    private readonly int _subBucketHalfCount;
    private readonly long _subBucketMask;
    private readonly int _leadingZeroCountBase;
    private readonly long[] _counts;

    private long _totalCount;
    private long _minValue = long.MaxValue;
    private long _maxValue;
    private long _sum;

    public LatencyHistogram(long highestTrackableValue = 3_600_000_000L, int significantDigits = 3)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(highestTrackableValue, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(significantDigits, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(significantDigits, 5);

        HighestTrackableValue = highestTrackableValue;
        SignificantDigits = significantDigits;

        var largestValueWithSingleUnitResolution = 2 * (long)Math.Pow(10, significantDigits);
        var subBucketCountMagnitude = (int)Math.Ceiling(Math.Log2(largestValueWithSingleUnitResolution));
        var subBucketCount = 1 << subBucketCountMagnitude;

        _subBucketHalfCountMagnitude = subBucketCountMagnitude - 1;
        _subBucketHalfCount = subBucketCount / 2;
        _subBucketMask = subBucketCount - 1L;
        _leadingZeroCountBase = 64 - subBucketCountMagnitude;

        var bucketCount = BucketsNeededFor(highestTrackableValue, subBucketCount);
        _counts = new long[(bucketCount + 1) * _subBucketHalfCount];
    }

    public long HighestTrackableValue { get; }
    public int SignificantDigits { get; }

    public long TotalCount => Interlocked.Read(ref _totalCount);
    public long MinValue => TotalCount == 0 ? 0 : Interlocked.Read(ref _minValue);
    public long MaxValue => Interlocked.Read(ref _maxValue);
    public double MeanValue => TotalCount == 0 ? 0 : (double)Interlocked.Read(ref _sum) / TotalCount;

    /// <summary>
    /// Records one measurement. Values above <see cref="HighestTrackableValue"/> are clamped
    /// rather than dropped so a pathological outlier still shows up at the top of the range.
    /// </summary>
    public void Record(long value)
    {
        if (value < 0) value = 0;
        if (value > HighestTrackableValue) value = HighestTrackableValue;

        var index = CountsIndexFor(value);
        Interlocked.Increment(ref _counts[index]);
        Interlocked.Increment(ref _totalCount);
        Interlocked.Add(ref _sum, value);

        UpdateMin(value);
        UpdateMax(value);
    }

    private void UpdateMin(long value)
    {
        var current = Interlocked.Read(ref _minValue);
        while (value < current)
        {
            var seen = Interlocked.CompareExchange(ref _minValue, value, current);
            if (seen == current) break;
            current = seen;
        }
    }

    private void UpdateMax(long value)
    {
        var current = Interlocked.Read(ref _maxValue);
        while (value > current)
        {
            var seen = Interlocked.CompareExchange(ref _maxValue, value, current);
            if (seen == current) break;
            current = seen;
        }
    }

    /// <summary>
    /// Value at the given percentile, expressed 0-100. Returns the highest value that is
    /// indistinguishable from the recorded one at this histogram's precision, which is the
    /// conservative choice for a latency report.
    /// </summary>
    public long GetValueAtPercentile(double percentile)
    {
        var total = TotalCount;
        if (total == 0) return 0;

        var clamped = Math.Clamp(percentile, 0.0, 100.0);
        var requestedCount = (long)Math.Ceiling(clamped / 100.0 * total);
        if (requestedCount == 0) requestedCount = 1;

        long running = 0;
        for (var i = 0; i < _counts.Length; i++)
        {
            running += Interlocked.Read(ref _counts[i]);
            if (running >= requestedCount)
                return HighestEquivalentValue(ValueFromIndex(i));
        }

        return MaxValue;
    }

    /// <summary>Folds another histogram into this one. Used to merge per-user histograms.</summary>
    public void Add(LatencyHistogram other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (other._counts.Length != _counts.Length)
            throw new ArgumentException("Histograms must share the same configuration to be merged.", nameof(other));

        for (var i = 0; i < _counts.Length; i++)
        {
            var count = Interlocked.Read(ref other._counts[i]);
            if (count != 0)
                Interlocked.Add(ref _counts[i], count);
        }

        var otherTotal = other.TotalCount;
        if (otherTotal == 0) return;

        Interlocked.Add(ref _totalCount, otherTotal);
        Interlocked.Add(ref _sum, Interlocked.Read(ref other._sum));
        UpdateMin(other.MinValue);
        UpdateMax(other.MaxValue);
    }

    /// <summary>An independent copy, so a snapshot can be reported while the run continues.</summary>
    public LatencyHistogram Snapshot()
    {
        var copy = new LatencyHistogram(HighestTrackableValue, SignificantDigits);
        copy.Add(this);
        return copy;
    }

    public void Reset()
    {
        Array.Clear(_counts);
        Interlocked.Exchange(ref _totalCount, 0);
        Interlocked.Exchange(ref _sum, 0);
        Interlocked.Exchange(ref _minValue, long.MaxValue);
        Interlocked.Exchange(ref _maxValue, 0);
    }

    private static int BucketsNeededFor(long value, int subBucketCount)
    {
        long smallestUntrackable = subBucketCount;
        var bucketsNeeded = 1;
        while (smallestUntrackable <= value)
        {
            if (smallestUntrackable > long.MaxValue / 2)
                return bucketsNeeded + 1;
            smallestUntrackable <<= 1;
            bucketsNeeded++;
        }
        return bucketsNeeded;
    }

    private int CountsIndexFor(long value)
    {
        var bucketIndex = BucketIndexFor(value);
        var subBucketIndex = SubBucketIndexFor(value, bucketIndex);
        var bucketBaseIndex = (bucketIndex + 1) << _subBucketHalfCountMagnitude;
        var offsetInBucket = subBucketIndex - _subBucketHalfCount;
        return bucketBaseIndex + offsetInBucket;
    }

    private int BucketIndexFor(long value) =>
        _leadingZeroCountBase - BitOperations.LeadingZeroCount((ulong)(value | _subBucketMask));

    private static int SubBucketIndexFor(long value, int bucketIndex) => (int)(value >>> bucketIndex);

    private long ValueFromIndex(int index)
    {
        var bucketIndex = (index >> _subBucketHalfCountMagnitude) - 1;
        var subBucketIndex = (index & (_subBucketHalfCount - 1)) + _subBucketHalfCount;
        if (bucketIndex < 0)
        {
            subBucketIndex -= _subBucketHalfCount;
            bucketIndex = 0;
        }
        return (long)subBucketIndex << bucketIndex;
    }

    private long HighestEquivalentValue(long value)
    {
        var bucketIndex = BucketIndexFor(value);
        var subBucketIndex = SubBucketIndexFor(value, bucketIndex);
        var sizeOfEquivalentRange = 1L << bucketIndex;
        var lowest = (long)subBucketIndex << bucketIndex;
        return lowest + sizeOfEquivalentRange - 1;
    }
}
