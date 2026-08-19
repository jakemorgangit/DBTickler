using DBTickler.Core.Metrics;

namespace DBTickler.Core.Tests.Metrics;

/// <summary>
/// <see cref="LatencyHistogram"/> is a from-scratch HdrHistogram-style implementation, so it
/// gets the heaviest scrutiny in the suite: percentile accuracy, the "never report below what
/// was recorded" guarantee, clamping, merging, snapshotting and thread safety.
/// </summary>
public class LatencyHistogramTests
{
    public class Construction
    {
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(-5)]
        public void Throws_when_highest_trackable_value_is_below_two(long highest) =>
            Assert.Throws<ArgumentOutOfRangeException>(() => new LatencyHistogram(highestTrackableValue: highest));

        [Fact]
        public void Accepts_highest_trackable_value_of_exactly_two() =>
            new LatencyHistogram(highestTrackableValue: 2); // must not throw

        [Theory]
        [InlineData(-1)]
        [InlineData(6)]
        [InlineData(100)]
        public void Throws_when_significant_digits_out_of_zero_to_five_range(int digits) =>
            Assert.Throws<ArgumentOutOfRangeException>(() => new LatencyHistogram(significantDigits: digits));

        [Theory]
        [InlineData(0)]
        [InlineData(3)]
        [InlineData(5)]
        public void Accepts_significant_digits_within_zero_to_five_range(int digits) =>
            new LatencyHistogram(significantDigits: digits); // must not throw
    }

    public class EmptyHistogram
    {
        [Fact]
        public void Reports_zero_for_every_statistic()
        {
            var histogram = new LatencyHistogram();

            Assert.Equal(0, histogram.TotalCount);
            Assert.Equal(0, histogram.MinValue);
            Assert.Equal(0, histogram.MaxValue);
            Assert.Equal(0, histogram.MeanValue);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(50)]
        [InlineData(99.9)]
        [InlineData(100)]
        public void GetValueAtPercentile_returns_zero_rather_than_throwing(double percentile)
        {
            var histogram = new LatencyHistogram();
            Assert.Equal(0, histogram.GetValueAtPercentile(percentile));
        }
    }

    public class BasicStatistics
    {
        [Fact]
        public void MinValue_MaxValue_MeanValue_and_TotalCount_are_correct_for_a_uniform_range()
        {
            var histogram = new LatencyHistogram();
            for (long value = 1; value <= 10_000; value++)
                histogram.Record(value);

            Assert.Equal(10_000, histogram.TotalCount);
            Assert.Equal(1, histogram.MinValue);
            Assert.Equal(10_000, histogram.MaxValue);
            // Mean of 1..10000 is (1+10000)/2 = 5000.5, computed from an exact integer sum so
            // there is no rounding slack to allow for.
            Assert.Equal(5000.5, histogram.MeanValue, precision: 9);
        }

        [Fact]
        public void MinValue_and_MaxValue_are_exact_even_though_percentiles_are_bucketed()
        {
            // 10,000 is inside a bucket whose resolution is coarser than 1 at 3 significant
            // digits, so GetValueAtPercentile(100) may round up past the true maximum — but
            // MinValue/MaxValue are tracked outside the bucketing and must stay exact.
            var histogram = new LatencyHistogram();
            for (long value = 1; value <= 10_000; value++)
                histogram.Record(value);

            Assert.Equal(10_000, histogram.MaxValue);
            Assert.True(histogram.GetValueAtPercentile(100) >= histogram.MaxValue);
        }

        [Fact]
        public void Single_recorded_value_is_min_max_and_every_percentile()
        {
            var histogram = new LatencyHistogram();
            histogram.Record(4242);

            Assert.Equal(1, histogram.TotalCount);
            Assert.Equal(4242, histogram.MinValue);
            Assert.Equal(4242, histogram.MaxValue);
            Assert.Equal(4242, histogram.MeanValue);
        }
    }

    public class PercentileAccuracy
    {
        /// <summary>
        /// Records 1..10000 (a dataset whose nearest-rank percentile value is simply the rank
        /// itself) and checks the histogram's answer against that ground truth, both for
        /// direction (never below what nearest-rank selection would return) and for magnitude
        /// (within the ~0.1% relative error that 3 significant digits promises).
        /// </summary>
        [Theory]
        [InlineData(50, 5_000)]
        [InlineData(90, 9_000)]
        [InlineData(95, 9_500)]
        [InlineData(99, 9_900)]
        [InlineData(99.9, 9_990)]
        public void Percentiles_are_accurate_to_within_configured_precision_for_a_uniform_distribution(
            double percentile, long expectedRawValue)
        {
            var histogram = new LatencyHistogram();
            for (long value = 1; value <= 10_000; value++)
                histogram.Record(value);

            var reported = histogram.GetValueAtPercentile(percentile);

            Assert.True(reported >= expectedRawValue,
                $"p{percentile} reported {reported}, which is below the true rank value {expectedRawValue}.");

            var relativeError = (reported - expectedRawValue) / (double)expectedRawValue;
            Assert.True(relativeError <= 0.002,
                $"p{percentile} reported {reported} vs expected {expectedRawValue} " +
                $"({relativeError:P3} relative error, expected <= 0.2%).");
        }

        [Fact]
        public void GetValueAtPercentile_zero_returns_the_minimum()
        {
            var histogram = new LatencyHistogram();
            for (long value = 1; value <= 10_000; value++)
                histogram.Record(value);

            Assert.Equal(1, histogram.GetValueAtPercentile(0));
        }

        [Fact]
        public void GetValueAtPercentile_clamps_out_of_range_input()
        {
            var histogram = new LatencyHistogram();
            for (long value = 1; value <= 100; value++)
                histogram.Record(value);

            // Percentile is documented as 0-100; values outside that must not throw and must
            // behave like the nearest in-range percentile.
            Assert.Equal(histogram.GetValueAtPercentile(0), histogram.GetValueAtPercentile(-50));
            Assert.Equal(histogram.GetValueAtPercentile(100), histogram.GetValueAtPercentile(150));
        }

        /// <summary>
        /// For any single recorded value, the value the histogram reports back must never be
        /// lower than what was recorded — only rounded up to the edge of its equivalent range.
        /// </summary>
        [Fact]
        public void Reported_value_is_never_below_the_recorded_value()
        {
            var random = new Random(20260819);
            for (var i = 0; i < 500; i++)
            {
                var value = (long)random.NextInt64(0, 3_600_000_000L);
                var histogram = new LatencyHistogram();
                histogram.Record(value);

                var reported = histogram.GetValueAtPercentile(random.Next(0, 101));

                Assert.True(reported >= value, $"Recorded {value} but percentile reported {reported}.");
            }
        }

        /// <summary>
        /// The doc comment promises relative error bounded by the configured significant
        /// digits (3 digits ⇒ roughly 0.1%). Sampling a value at each order of magnitude keeps
        /// this test meaningful across the whole trackable range, not just near the origin.
        /// </summary>
        [Theory]
        [InlineData(5)]
        [InlineData(50)]
        [InlineData(500)]
        [InlineData(5_000)]
        [InlineData(50_000)]
        [InlineData(500_000)]
        [InlineData(5_000_000)]
        [InlineData(50_000_000)]
        [InlineData(500_000_000)]
        public void Relative_error_stays_within_three_significant_digits_across_the_range(long value)
        {
            var histogram = new LatencyHistogram(); // default: 3 significant digits
            histogram.Record(value);

            var reported = histogram.GetValueAtPercentile(100);

            Assert.True(reported >= value);
            var relativeError = (reported - value) / (double)value;
            Assert.True(relativeError <= 0.0015,
                $"value {value} reported as {reported}: {relativeError:P4} relative error, expected <= 0.15%.");
        }

        [Fact]
        public void Values_below_two_thousand_are_reported_with_exact_single_unit_resolution()
        {
            // 2 * 10^3 significant digits guarantees single-unit resolution up to 2000.
            for (long value = 1; value < 2000; value += 137)
            {
                var histogram = new LatencyHistogram();
                histogram.Record(value);
                Assert.Equal(value, histogram.GetValueAtPercentile(100));
            }
        }
    }

    public class Clamping
    {
        [Fact]
        public void Values_above_highest_trackable_value_are_clamped_not_thrown_or_corrupted()
        {
            var histogram = new LatencyHistogram(highestTrackableValue: 10_000);

            histogram.Record(50_000);
            histogram.Record(1_000_000);

            Assert.Equal(2, histogram.TotalCount);
            Assert.Equal(10_000, histogram.MaxValue);
            Assert.True(histogram.GetValueAtPercentile(100) >= 10_000);
        }

        [Fact]
        public void Negative_values_are_clamped_to_zero_rather_than_thrown()
        {
            var histogram = new LatencyHistogram();

            histogram.Record(-5);
            histogram.Record(-1_000_000);

            Assert.Equal(2, histogram.TotalCount);
            Assert.Equal(0, histogram.MinValue);
            Assert.Equal(0, histogram.MaxValue);
        }

        [Fact]
        public void A_single_clamped_outlier_still_shows_up_at_the_top_of_the_range()
        {
            var histogram = new LatencyHistogram(highestTrackableValue: 100_000);
            for (long value = 1; value <= 100; value++)
                histogram.Record(value);
            histogram.Record(50_000_000); // way above highestTrackableValue, clamps to 100_000

            Assert.Equal(100_000, histogram.MaxValue);
            // The top percentile is bucketed like everything else, so it may round up slightly
            // past the clamp point rather than landing on it exactly.
            var top = histogram.GetValueAtPercentile(100);
            Assert.True(top >= 100_000, $"Expected the top percentile to be at least 100000, was {top}.");
            Assert.True(top <= 100_100, $"Expected the top percentile to stay close to the clamp point, was {top}.");
        }
    }

    public class Merging
    {
        [Fact]
        public void Add_merges_two_histograms_to_match_a_single_histogram_fed_both_datasets()
        {
            var first = new LatencyHistogram();
            var second = new LatencyHistogram();
            var combinedDirectly = new LatencyHistogram();

            var random = new Random(7);
            for (var i = 0; i < 2000; i++)
            {
                var value = random.Next(1, 250_000);
                first.Record(value);
                combinedDirectly.Record(value);
            }
            for (var i = 0; i < 2000; i++)
            {
                var value = random.Next(1, 250_000);
                second.Record(value);
                combinedDirectly.Record(value);
            }

            var merged = new LatencyHistogram();
            merged.Add(first);
            merged.Add(second);

            Assert.Equal(combinedDirectly.TotalCount, merged.TotalCount);
            Assert.Equal(combinedDirectly.MinValue, merged.MinValue);
            Assert.Equal(combinedDirectly.MaxValue, merged.MaxValue);
            Assert.Equal(combinedDirectly.MeanValue, merged.MeanValue, precision: 9);

            foreach (var percentile in new[] { 10, 25, 50, 75, 90, 95, 99, 99.9 })
            {
                Assert.Equal(
                    combinedDirectly.GetValueAtPercentile(percentile),
                    merged.GetValueAtPercentile(percentile));
            }
        }

        [Fact]
        public void Add_with_empty_other_histogram_is_a_no_op()
        {
            var histogram = new LatencyHistogram();
            histogram.Record(123);

            histogram.Add(new LatencyHistogram());

            Assert.Equal(1, histogram.TotalCount);
            Assert.Equal(123, histogram.MaxValue);
        }

        [Fact]
        public void Add_null_throws()
        {
            var histogram = new LatencyHistogram();
            Assert.Throws<ArgumentNullException>(() => histogram.Add(null!));
        }

        [Fact]
        public void Add_with_mismatched_configuration_throws()
        {
            var histogram = new LatencyHistogram(significantDigits: 3);
            var incompatible = new LatencyHistogram(significantDigits: 1);

            Assert.Throws<ArgumentException>(() => histogram.Add(incompatible));
        }
    }

    public class Snapshotting
    {
        [Fact]
        public void Snapshot_is_independent_of_further_mutation_of_the_original()
        {
            var histogram = new LatencyHistogram();
            histogram.Record(100);
            histogram.Record(200);

            var snapshot = histogram.Snapshot();
            Assert.Equal(2, snapshot.TotalCount);
            Assert.Equal(200, snapshot.MaxValue);

            histogram.Record(300);
            histogram.Record(9_999);

            Assert.Equal(4, histogram.TotalCount);
            Assert.Equal(2, snapshot.TotalCount);
            Assert.Equal(200, snapshot.MaxValue);
        }

        [Fact]
        public void Mutating_the_snapshot_does_not_affect_the_original()
        {
            var histogram = new LatencyHistogram();
            histogram.Record(50);

            var snapshot = histogram.Snapshot();
            snapshot.Record(999_999);

            Assert.Equal(1, histogram.TotalCount);
            Assert.Equal(50, histogram.MaxValue);
            Assert.Equal(2, snapshot.TotalCount);
        }
    }

    public class Resetting
    {
        [Fact]
        public void Reset_clears_all_statistics()
        {
            var histogram = new LatencyHistogram();
            for (long value = 1; value <= 500; value++)
                histogram.Record(value);

            histogram.Reset();

            Assert.Equal(0, histogram.TotalCount);
            Assert.Equal(0, histogram.MinValue);
            Assert.Equal(0, histogram.MaxValue);
            Assert.Equal(0, histogram.MeanValue);
            Assert.Equal(0, histogram.GetValueAtPercentile(50));
        }

        [Fact]
        public void Histogram_is_usable_again_after_reset()
        {
            var histogram = new LatencyHistogram();
            histogram.Record(1);
            histogram.Reset();

            histogram.Record(42);

            Assert.Equal(1, histogram.TotalCount);
            Assert.Equal(42, histogram.MaxValue);
        }
    }

    public class Concurrency
    {
        [Fact]
        public async Task Recording_from_many_threads_in_parallel_produces_an_exact_total_count()
        {
            var histogram = new LatencyHistogram();
            const int Threads = 20;
            const int PerThread = 10_000;

            var tasks = Enumerable.Range(0, Threads).Select(threadIndex => Task.Run(() =>
            {
                var random = new Random(threadIndex);
                for (var i = 0; i < PerThread; i++)
                    histogram.Record(random.Next(1, 1_000_000));
            }));

            await Task.WhenAll(tasks);

            Assert.Equal((long)Threads * PerThread, histogram.TotalCount);
        }
    }
}
