using DBTickler.Core.Data;
using DBTickler.Core.Metrics;

namespace DBTickler.Core.Tests.Metrics;

/// <summary>
/// <see cref="MetricsCollector"/> is the glue between per-operation recording and the
/// snapshot the engine reports; it is exercised indirectly through the engine tests too, but
/// these focus on its own aggregation logic in isolation.
/// </summary>
public class MetricsCollectorTests
{
    [Fact]
    public void New_collector_has_zeroed_totals_and_an_empty_snapshot()
    {
        var collector = new MetricsCollector();
        var snapshot = collector.Snapshot();

        Assert.Equal(0, snapshot.TotalOperations);
        Assert.Equal(0, snapshot.TotalErrors);
        Assert.Equal(0, snapshot.TotalRows);
        Assert.Empty(snapshot.ByKind);
        Assert.Empty(snapshot.ErrorsByKind);
        Assert.Empty(snapshot.ErrorsByNumber);
    }

    [Fact]
    public void RecordSuccess_increments_totals_and_the_matching_kind()
    {
        var collector = new MetricsCollector();
        collector.RecordSuccess(OperationKind.Read, elapsedMicroseconds: 1500, rows: 10);
        collector.RecordSuccess(OperationKind.Read, elapsedMicroseconds: 2500, rows: 20);
        collector.RecordSuccess(OperationKind.Insert, elapsedMicroseconds: 1000, rows: 1);

        var snapshot = collector.Snapshot();

        Assert.Equal(3, snapshot.TotalOperations);
        Assert.Equal(31, snapshot.TotalRows);
        Assert.Equal(0, snapshot.TotalErrors);

        var reads = snapshot.ByKind.Single(s => s.Kind == OperationKind.Read);
        Assert.Equal(2, reads.Operations);
        Assert.Equal(30, reads.Rows);

        var inserts = snapshot.ByKind.Single(s => s.Kind == OperationKind.Insert);
        Assert.Equal(1, inserts.Operations);
    }

    [Fact]
    public void RecordFailure_increments_error_totals_and_is_grouped_by_kind_and_number()
    {
        var collector = new MetricsCollector();
        collector.RecordFailure(OperationKind.Update, elapsedMicroseconds: 3000, SqlFailureKind.DeadlockVictim, errorNumber: 1205);
        collector.RecordFailure(OperationKind.Update, elapsedMicroseconds: 3000, SqlFailureKind.DeadlockVictim, errorNumber: 1205);
        collector.RecordFailure(OperationKind.Delete, elapsedMicroseconds: 500, SqlFailureKind.LockTimeout, errorNumber: 1222);

        var snapshot = collector.Snapshot();

        Assert.Equal(0, snapshot.TotalOperations); // failures are not successes
        Assert.Equal(3, snapshot.TotalErrors);
        Assert.Equal(2, snapshot.ErrorsByKind[SqlFailureKind.DeadlockVictim]);
        Assert.Equal(1, snapshot.ErrorsByKind[SqlFailureKind.LockTimeout]);
        Assert.Equal(2, snapshot.ErrorsByNumber[1205]);
        Assert.Equal(1, snapshot.ErrorsByNumber[1222]);
    }

    [Fact]
    public void RecordFailure_with_no_error_number_does_not_add_an_entry_to_ErrorsByNumber()
    {
        var collector = new MetricsCollector();
        collector.RecordFailure(OperationKind.Read, 1000, SqlFailureKind.Other, errorNumber: null);

        Assert.Empty(collector.Snapshot().ErrorsByNumber);
    }

    [Fact]
    public void Failed_operations_still_contribute_latency_to_the_merged_histogram()
    {
        // A failure that took 30 seconds to time out is a 30-second data point; dropping it
        // would flatter the percentiles.
        var collector = new MetricsCollector();
        collector.RecordFailure(OperationKind.Read, elapsedMicroseconds: 30_000_000, SqlFailureKind.CommandTimeout, errorNumber: null);

        var snapshot = collector.Snapshot();
        Assert.Equal(1, snapshot.Latency.Count);
        Assert.True(snapshot.Latency.Max >= 29_999);
    }

    [Fact]
    public void UserStarted_and_UserFinished_track_ActiveUsers()
    {
        var collector = new MetricsCollector();
        collector.UserStarted();
        collector.UserStarted();
        collector.UserStarted();
        collector.UserFinished();

        Assert.Equal(2, collector.Snapshot().ActiveUsers);
    }

    [Fact]
    public void OperationsPerSecond_and_ErrorRate_are_computed_from_the_snapshot()
    {
        var collector = new MetricsCollector();
        collector.Start();
        collector.RecordSuccess(OperationKind.Read, 1000, 1);
        collector.RecordSuccess(OperationKind.Read, 1000, 1);
        collector.RecordSuccess(OperationKind.Read, 1000, 1);
        collector.RecordFailure(OperationKind.Read, 1000, SqlFailureKind.Other, null);
        Thread.Sleep(50);
        collector.Stop();

        var snapshot = collector.Snapshot();

        Assert.True(snapshot.OperationsPerSecond > 0);
        Assert.Equal(0.25, snapshot.ErrorRate, precision: 6); // 1 error out of 4 total attempts
    }

    [Fact]
    public void ErrorRate_is_zero_when_nothing_has_run_yet() =>
        Assert.Equal(0, MetricsSnapshot.Empty.ErrorRate);

    [Fact]
    public void DeadlockVictims_and_Timeouts_are_convenience_sums_over_ErrorsByKind()
    {
        var collector = new MetricsCollector();
        collector.RecordFailure(OperationKind.Read, 1000, SqlFailureKind.DeadlockVictim, 1205);
        collector.RecordFailure(OperationKind.Read, 1000, SqlFailureKind.CommandTimeout, null);
        collector.RecordFailure(OperationKind.Read, 1000, SqlFailureKind.LockTimeout, 1222);

        var snapshot = collector.Snapshot();

        Assert.Equal(1, snapshot.DeadlockVictims);
        Assert.Equal(2, snapshot.Timeouts); // CommandTimeout + LockTimeout
    }

    [Fact]
    public void Snapshot_reflects_state_at_the_time_it_was_taken()
    {
        var collector = new MetricsCollector();
        collector.RecordSuccess(OperationKind.Read, 1000, 1);
        var first = collector.Snapshot();

        collector.RecordSuccess(OperationKind.Read, 1000, 1);
        var second = collector.Snapshot();

        Assert.Equal(1, first.TotalOperations);
        Assert.Equal(2, second.TotalOperations);
    }

    [Fact]
    public void RecentOperationsPerSecond_returns_zero_for_an_empty_series() =>
        Assert.Equal(0, MetricsSnapshot.Empty.RecentOperationsPerSecond());
}
