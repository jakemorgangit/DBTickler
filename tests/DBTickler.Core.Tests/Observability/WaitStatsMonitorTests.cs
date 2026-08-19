using DBTickler.Core.Observability;

namespace DBTickler.Core.Tests.Observability;

public class WaitStatsMonitorTests
{
    [Fact]
    public void Null_baseline_or_current_throws()
    {
        Assert.Throws<ArgumentNullException>(() => WaitStatsMonitor.Diff(null!, []));
        Assert.Throws<ArgumentNullException>(() => WaitStatsMonitor.Diff([], null!));
    }

    [Fact]
    public void Empty_inputs_produce_an_empty_result_without_dividing_by_zero() =>
        Assert.Empty(WaitStatsMonitor.Diff([], []));

    [Fact]
    public void Delta_is_the_difference_between_current_and_baseline()
    {
        var baseline = new[] { new WaitStat("LCK_M_X", WaitTimeMs: 1000, SignalWaitTimeMs: 200, WaitingTasks: 5) };
        var current = new[] { new WaitStat("LCK_M_X", WaitTimeMs: 5000, SignalWaitTimeMs: 800, WaitingTasks: 25) };

        var delta = Assert.Single(WaitStatsMonitor.Diff(baseline, current));

        Assert.Equal("LCK_M_X", delta.WaitType);
        Assert.Equal(4000, delta.WaitTimeMs);
        Assert.Equal(600, delta.SignalWaitTimeMs);
        Assert.Equal(20, delta.WaitingTasks);
        Assert.Equal(3400, delta.ResourceWaitTimeMs); // wait time minus signal wait time
        Assert.Equal(200, delta.AverageWaitMs);        // 4000 / 20
    }

    [Fact]
    public void A_wait_type_absent_from_the_baseline_is_treated_as_starting_from_zero()
    {
        var baseline = Array.Empty<WaitStat>();
        var current = new[] { new WaitStat("PAGEIOLATCH_SH", WaitTimeMs: 300, SignalWaitTimeMs: 50, WaitingTasks: 10) };

        var delta = Assert.Single(WaitStatsMonitor.Diff(baseline, current));

        Assert.Equal(300, delta.WaitTimeMs);
        Assert.Equal(10, delta.WaitingTasks);
    }

    [Fact]
    public void Baseline_lookup_is_case_insensitive()
    {
        var baseline = new[] { new WaitStat("lck_m_x", WaitTimeMs: 1000, SignalWaitTimeMs: 0, WaitingTasks: 5) };
        var current = new[] { new WaitStat("LCK_M_X", WaitTimeMs: 1500, SignalWaitTimeMs: 0, WaitingTasks: 8) };

        var delta = Assert.Single(WaitStatsMonitor.Diff(baseline, current));

        Assert.Equal(500, delta.WaitTimeMs);
        Assert.Equal(3, delta.WaitingTasks);
    }

    [Theory]
    [InlineData("SLEEP_TASK")]
    [InlineData("BROKER_TO_FLUSH")]
    [InlineData("LAZYWRITER_SLEEP")]
    [InlineData("XE_TIMER_EVENT")]
    public void Benign_wait_types_are_filtered_out_regardless_of_magnitude(string benignWaitType)
    {
        var baseline = Array.Empty<WaitStat>();
        var current = new[] { new WaitStat(benignWaitType, WaitTimeMs: 999_999, SignalWaitTimeMs: 0, WaitingTasks: 999) };

        Assert.Empty(WaitStatsMonitor.Diff(baseline, current));
    }

    [Fact]
    public void Benign_filter_is_case_insensitive()
    {
        var current = new[] { new WaitStat("sleep_task", WaitTimeMs: 5000, SignalWaitTimeMs: 0, WaitingTasks: 10) };
        Assert.Empty(WaitStatsMonitor.Diff([], current));
    }

    [Fact]
    public void Counters_going_backwards_fall_back_to_the_current_reading()
    {
        // Server restarted or DBCC SQLPERF('sys.dm_os_wait_stats', CLEAR) ran mid-run: the
        // "delta" would be negative, which is nonsensical, so the current value is used as-is.
        var baseline = new[] { new WaitStat("WRITELOG", WaitTimeMs: 10_000, SignalWaitTimeMs: 500, WaitingTasks: 100) };
        var current = new[] { new WaitStat("WRITELOG", WaitTimeMs: 200, SignalWaitTimeMs: 20, WaitingTasks: 5) };

        var delta = Assert.Single(WaitStatsMonitor.Diff(baseline, current));

        Assert.Equal(200, delta.WaitTimeMs);
        Assert.Equal(20, delta.SignalWaitTimeMs);
        Assert.Equal(5, delta.WaitingTasks);
    }

    [Fact]
    public void A_negative_task_count_alone_also_triggers_the_fallback()
    {
        // wait time delta is non-negative here, but the task count went backwards — the whole
        // row should still fall back to the current reading rather than reporting a negative
        // task count.
        var baseline = new[] { new WaitStat("LCK_M_X", WaitTimeMs: 1000, SignalWaitTimeMs: 100, WaitingTasks: 50) };
        var current = new[] { new WaitStat("LCK_M_X", WaitTimeMs: 1000, SignalWaitTimeMs: 100, WaitingTasks: 10) };

        var delta = Assert.Single(WaitStatsMonitor.Diff(baseline, current));

        Assert.Equal(1000, delta.WaitTimeMs);
        Assert.Equal(10, delta.WaitingTasks);
    }

    [Fact]
    public void A_wait_type_with_zero_delta_is_omitted()
    {
        var baseline = new[] { new WaitStat("LCK_M_X", WaitTimeMs: 1000, SignalWaitTimeMs: 100, WaitingTasks: 10) };
        var current = new[] { new WaitStat("LCK_M_X", WaitTimeMs: 1000, SignalWaitTimeMs: 100, WaitingTasks: 10) };

        Assert.Empty(WaitStatsMonitor.Diff(baseline, current));
    }

    [Fact]
    public void PercentOfTotal_sums_to_approximately_one_across_all_returned_deltas()
    {
        var baseline = Array.Empty<WaitStat>();
        var current = new[]
        {
            new WaitStat("A", WaitTimeMs: 100, SignalWaitTimeMs: 0, WaitingTasks: 1),
            new WaitStat("B", WaitTimeMs: 200, SignalWaitTimeMs: 0, WaitingTasks: 1),
            new WaitStat("C", WaitTimeMs: 300, SignalWaitTimeMs: 0, WaitingTasks: 1),
        };

        var deltas = WaitStatsMonitor.Diff(baseline, current);

        Assert.Equal(3, deltas.Count);
        Assert.Equal(1.0, deltas.Sum(d => d.PercentOfTotal), precision: 9);

        var byType = deltas.ToDictionary(d => d.WaitType);
        Assert.Equal(100.0 / 600.0, byType["A"].PercentOfTotal, precision: 9);
        Assert.Equal(300.0 / 600.0, byType["C"].PercentOfTotal, precision: 9);
    }

    [Fact]
    public void TopN_limits_the_number_of_results()
    {
        var baseline = Array.Empty<WaitStat>();
        var current = Enumerable.Range(0, 20)
            .Select(i => new WaitStat($"WAIT_TYPE_{i}", WaitTimeMs: (i + 1) * 10, SignalWaitTimeMs: 0, WaitingTasks: 1))
            .ToArray();

        var deltas = WaitStatsMonitor.Diff(baseline, current, topN: 5);

        Assert.Equal(5, deltas.Count);
    }

    [Fact]
    public void Results_are_ordered_by_wait_time_descending()
    {
        var baseline = Array.Empty<WaitStat>();
        var current = new[]
        {
            new WaitStat("SMALL", WaitTimeMs: 50, SignalWaitTimeMs: 0, WaitingTasks: 1),
            new WaitStat("LARGE", WaitTimeMs: 9000, SignalWaitTimeMs: 0, WaitingTasks: 1),
            new WaitStat("MEDIUM", WaitTimeMs: 500, SignalWaitTimeMs: 0, WaitingTasks: 1),
        };

        var deltas = WaitStatsMonitor.Diff(baseline, current);

        Assert.Equal(["LARGE", "MEDIUM", "SMALL"], deltas.Select(d => d.WaitType));
        for (var i = 1; i < deltas.Count; i++)
            Assert.True(deltas[i - 1].WaitTimeMs >= deltas[i].WaitTimeMs);
    }

    [Fact]
    public void TopN_keeps_the_largest_waits_not_an_arbitrary_subset()
    {
        var baseline = Array.Empty<WaitStat>();
        var current = new[]
        {
            new WaitStat("BIGGEST", WaitTimeMs: 10_000, SignalWaitTimeMs: 0, WaitingTasks: 1),
            new WaitStat("TINY_1", WaitTimeMs: 1, SignalWaitTimeMs: 0, WaitingTasks: 1),
            new WaitStat("TINY_2", WaitTimeMs: 2, SignalWaitTimeMs: 0, WaitingTasks: 1),
            new WaitStat("SECOND_BIGGEST", WaitTimeMs: 5_000, SignalWaitTimeMs: 0, WaitingTasks: 1),
        };

        var deltas = WaitStatsMonitor.Diff(baseline, current, topN: 2);

        Assert.Equal(["BIGGEST", "SECOND_BIGGEST"], deltas.Select(d => d.WaitType));
    }
}
