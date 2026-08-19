using System.Diagnostics;
using DBTickler.Core.Configuration;
using DBTickler.Core.Data;
using DBTickler.Core.Engine;
using DBTickler.Core.Tests.Testing;
using DBTickler.Core.Workloads;

namespace DBTickler.Core.Tests.Engine;

/// <summary>
/// Exercises <see cref="LoadEngine"/> end-to-end against a fake <see cref="ISqlSessionFactory"/>
/// so the concurrency, ramp, duration, cancellation and error-budget behaviour can be verified
/// without a database. Every fake session carries a small non-zero delay: with a truly
/// synchronous fake, a single virtual user's loop never yields back to the scheduler (there is
/// no real I/O to await), so it can run to completion before any other user even starts —
/// realistic enough for a database, but not what these tests are trying to measure.
/// </summary>
public class LoadEngineTests
{
    private static WorkloadProfile ReadOnlyProfile(int virtualUsers, int durationSeconds, int rampUpSeconds = 0)
    {
        var profile = WorkloadProfile.ReadOnly();
        profile.VirtualUsers = virtualUsers;
        profile.DurationSeconds = durationSeconds;
        profile.RampUpSeconds = rampUpSeconds;
        profile.ThinkTimeMs = 0;
        return profile;
    }

    [Fact]
    public async Task Concurrency_never_exceeds_the_configured_virtual_user_count()
    {
        var factory = new FakeSqlSessionFactory { Delay = TimeSpan.FromMilliseconds(150) };
        var profile = ReadOnlyProfile(virtualUsers: 10, durationSeconds: 3);
        var plan = WorkloadPlan.Build(profile, TestSchemas.LoadGenOnly());
        var engine = new LoadEngine(factory);

        await engine.RunAsync(plan).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(factory.TotalExecutions > 0, "Expected at least one operation to execute.");
        Assert.True(
            factory.MaxConcurrentObserved <= profile.VirtualUsers,
            $"Observed {factory.MaxConcurrentObserved} concurrent executions, " +
            $"but only {profile.VirtualUsers} virtual users were configured. " +
            "This is the headline concurrency guarantee of the engine.");
        // Soft sanity check that the test actually exercised real concurrency rather than
        // happening to run everything sequentially.
        Assert.True(
            factory.MaxConcurrentObserved >= profile.VirtualUsers - 1,
            $"Expected concurrency to approach {profile.VirtualUsers}, only observed {factory.MaxConcurrentObserved}.");
    }

    [Fact]
    public async Task DurationSeconds_stops_the_run_within_a_generous_time_band()
    {
        var factory = new FakeSqlSessionFactory { Delay = TimeSpan.FromMilliseconds(20) };
        var profile = ReadOnlyProfile(virtualUsers: 4, durationSeconds: 2);
        var plan = WorkloadPlan.Build(profile, TestSchemas.LoadGenOnly());
        var engine = new LoadEngine(factory);

        var stopwatch = Stopwatch.StartNew();
        var report = await engine.RunAsync(plan).WaitAsync(TimeSpan.FromSeconds(20));
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed >= TimeSpan.FromSeconds(1.5), $"Stopped too early: {stopwatch.Elapsed}.");
        Assert.True(stopwatch.Elapsed <= TimeSpan.FromSeconds(10), $"Stopped too late: {stopwatch.Elapsed}.");
        Assert.Equal(StopReason.DurationReached, report.StopReason);
        Assert.InRange(report.Duration.TotalSeconds, 1.5, 10);
    }

    [Fact]
    public async Task RequestStop_stops_the_run_promptly_with_user_requested_reason()
    {
        var factory = new FakeSqlSessionFactory { Delay = TimeSpan.FromMilliseconds(20) };
        // Long enough that only RequestStop (not the duration timer) ends the run.
        var profile = ReadOnlyProfile(virtualUsers: 4, durationSeconds: 30);
        var plan = WorkloadPlan.Build(profile, TestSchemas.LoadGenOnly());
        var engine = new LoadEngine(factory);

        var runTask = engine.RunAsync(plan);
        await Task.Delay(300);
        engine.RequestStop();

        var stopwatch = Stopwatch.StartNew();
        var report = await runTask.WaitAsync(TimeSpan.FromSeconds(10));
        stopwatch.Stop();

        Assert.Equal(StopReason.UserRequested, report.StopReason);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), "RequestStop should return control quickly.");
    }

    [Fact]
    public async Task External_cancellation_produces_cancelled_stop_reason()
    {
        var factory = new FakeSqlSessionFactory { Delay = TimeSpan.FromMilliseconds(20) };
        var profile = ReadOnlyProfile(virtualUsers: 4, durationSeconds: 30);
        var plan = WorkloadPlan.Build(profile, TestSchemas.LoadGenOnly());
        var engine = new LoadEngine(factory);

        using var cts = new CancellationTokenSource();
        var runTask = engine.RunAsync(plan, cts.Token);
        await Task.Delay(300);
        cts.Cancel();

        var report = await runTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(StopReason.Cancelled, report.StopReason);
    }

    [Fact]
    public async Task Error_budget_stops_the_run_with_a_bounded_overshoot()
    {
        var factory = new FakeSqlSessionFactory
        {
            Delay = TimeSpan.FromMilliseconds(10),
            ExceptionProvider = _ => new TimeoutException("simulated timeout"),
        };
        var profile = ReadOnlyProfile(virtualUsers: 8, durationSeconds: 30); // budget should end it long before this
        profile.MaxErrors = 30;
        var plan = WorkloadPlan.Build(profile, TestSchemas.LoadGenOnly());
        var engine = new LoadEngine(factory);

        var report = await engine.RunAsync(plan).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(StopReason.ErrorBudgetExceeded, report.StopReason);
        Assert.Equal(0, report.TotalOperations); // every attempt failed
        // Every virtual user runs one operation at a time, so at most VirtualUsers extra
        // errors can land after the budget is crossed but before each user notices and stops.
        Assert.InRange(report.TotalErrors, profile.MaxErrors, profile.MaxErrors + profile.VirtualUsers + 20);
    }

    [Fact]
    public async Task RunAsync_throws_InvalidOperationException_when_the_plan_has_validation_errors()
    {
        // Oltp() requests writes; an empty schema has no dbo.LoadGen, so the plan cannot
        // honour the write share and Diagnostics carries an error (while still being
        // "runnable" via the read-only metadata-scan fallback).
        var profile = WorkloadProfile.Oltp();
        var plan = WorkloadPlan.Build(profile, TestSchemas.Empty());
        Assert.False(plan.Diagnostics.IsValid); // sanity check on the fixture itself

        var engine = new LoadEngine(new FakeSqlSessionFactory());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.RunAsync(plan));
        Assert.Contains("LoadGen", exception.Message);
    }

    [Fact]
    public async Task RunAsync_throws_InvalidOperationException_when_a_run_is_already_in_progress()
    {
        var factory = new FakeSqlSessionFactory { Delay = TimeSpan.FromMilliseconds(50) };
        var profile = ReadOnlyProfile(virtualUsers: 2, durationSeconds: 5);
        var plan = WorkloadPlan.Build(profile, TestSchemas.LoadGenOnly());
        var engine = new LoadEngine(factory);

        var firstRun = engine.RunAsync(plan);
        await Task.Delay(100);

        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.RunAsync(plan));

        engine.RequestStop();
        await firstRun.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Successes_are_counted_when_every_operation_succeeds()
    {
        var factory = new FakeSqlSessionFactory { Delay = TimeSpan.FromMilliseconds(5) };
        var profile = ReadOnlyProfile(virtualUsers: 3, durationSeconds: 1);
        var plan = WorkloadPlan.Build(profile, TestSchemas.LoadGenOnly());
        var engine = new LoadEngine(factory);

        var report = await engine.RunAsync(plan).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.True(report.TotalOperations > 0);
        Assert.Equal(0, report.TotalErrors);
    }

    [Fact]
    public async Task Failures_are_recorded_with_the_correct_SqlFailureKind()
    {
        var factory = new FakeSqlSessionFactory
        {
            Delay = TimeSpan.FromMilliseconds(5),
            ExceptionProvider = _ => new TimeoutException("simulated timeout"),
        };
        var profile = ReadOnlyProfile(virtualUsers: 2, durationSeconds: 1);
        profile.MaxErrors = 0; // disable the budget so only the duration ends the run
        var plan = WorkloadPlan.Build(profile, TestSchemas.LoadGenOnly());
        var engine = new LoadEngine(factory);

        var report = await engine.RunAsync(plan).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(0, report.TotalOperations);
        Assert.True(report.TotalErrors > 0);
        var expectedLabel = SqlErrorClassifier.DescribeKind(SqlFailureKind.CommandTimeout);
        Assert.True(report.ErrorsByKind.ContainsKey(expectedLabel));
        Assert.Equal(report.TotalErrors, report.ErrorsByKind[expectedLabel]);
    }

    [Fact]
    public async Task Same_random_seed_produces_the_same_operation_sequence_for_a_single_user()
    {
        var first = await RunAndCaptureSqlAsync();
        var second = await RunAndCaptureSqlAsync();

        const int PrefixLength = 40;
        Assert.True(first.Length >= PrefixLength, $"First run only captured {first.Length} operations.");
        Assert.True(second.Length >= PrefixLength, $"Second run only captured {second.Length} operations.");

        Assert.Equal(first.Take(PrefixLength), second.Take(PrefixLength));

        static async Task<string[]> RunAndCaptureSqlAsync()
        {
            var factory = new FakeSqlSessionFactory { Delay = TimeSpan.FromMilliseconds(2) };
            var profile = ReadOnlyProfile(virtualUsers: 1, durationSeconds: 2);
            profile.RandomSeed = 424242;

            var plan = WorkloadPlan.Build(profile, TestSchemas.LoadGenOnly());
            var engine = new LoadEngine(factory);
            await engine.RunAsync(plan).WaitAsync(TimeSpan.FromSeconds(15));

            return [.. factory.ExecutedSql];
        }
    }

    public class RampDelayForTests
    {
        private static WorkloadProfile Profile(int virtualUsers, int rampUpSeconds)
        {
            var profile = WorkloadProfile.ReadOnly();
            profile.VirtualUsers = virtualUsers;
            profile.RampUpSeconds = rampUpSeconds;
            return profile;
        }

        [Fact]
        public void Returns_zero_when_ramp_up_is_disabled()
        {
            var profile = Profile(virtualUsers: 8, rampUpSeconds: 0);
            Assert.Equal(TimeSpan.Zero, LoadEngine.RampDelayFor(3, profile));
        }

        [Fact]
        public void Returns_zero_when_there_is_only_one_virtual_user()
        {
            var profile = Profile(virtualUsers: 1, rampUpSeconds: 10);
            Assert.Equal(TimeSpan.Zero, LoadEngine.RampDelayFor(0, profile));
        }

        [Fact]
        public void First_user_starts_immediately()
        {
            var profile = Profile(virtualUsers: 5, rampUpSeconds: 10);
            Assert.Equal(TimeSpan.Zero, LoadEngine.RampDelayFor(0, profile));
        }

        [Fact]
        public void Delays_increase_monotonically_with_user_index()
        {
            var profile = Profile(virtualUsers: 5, rampUpSeconds: 10);

            var delays = Enumerable.Range(0, profile.VirtualUsers)
                .Select(index => LoadEngine.RampDelayFor(index, profile))
                .ToArray();

            for (var i = 1; i < delays.Length; i++)
                Assert.True(delays[i] > delays[i - 1], $"delay[{i}]={delays[i]} should exceed delay[{i - 1}]={delays[i - 1]}.");
        }

        [Fact]
        public void Delays_stay_bounded_by_ramp_up_seconds()
        {
            var profile = Profile(virtualUsers: 5, rampUpSeconds: 10);

            for (var index = 0; index < profile.VirtualUsers; index++)
            {
                var delay = LoadEngine.RampDelayFor(index, profile);
                Assert.True(delay < TimeSpan.FromSeconds(profile.RampUpSeconds),
                    $"delay for user {index} was {delay}, expected less than {profile.RampUpSeconds}s.");
            }
        }
    }
}
