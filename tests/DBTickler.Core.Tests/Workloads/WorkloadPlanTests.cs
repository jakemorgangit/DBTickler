using DBTickler.Core.Configuration;
using DBTickler.Core.Metrics;
using DBTickler.Core.Tests.Testing;
using DBTickler.Core.Workloads;

namespace DBTickler.Core.Tests.Workloads;

public class WorkloadPlanTests
{
    public class DiagnosticsFromBuild
    {
        [Fact]
        public void Writes_requested_without_a_LoadGen_table_is_a_diagnostics_error()
        {
            var profile = WorkloadProfile.Oltp(); // SafeMode = false, ~30% writes
            var plan = WorkloadPlan.Build(profile, TestSchemas.Empty());

            Assert.False(plan.Diagnostics.IsValid);
            Assert.Contains(plan.Diagnostics.Errors, e => e.Contains("dbo.LoadGen does not exist"));
        }

        [Fact]
        public void Writes_requested_with_a_LoadGen_table_present_is_valid_and_generates_write_ops()
        {
            var profile = WorkloadProfile.Oltp();
            var plan = WorkloadPlan.Build(profile, TestSchemas.LoadGenOnly());

            Assert.True(plan.Diagnostics.IsValid, plan.Diagnostics.FormatErrors());
            Assert.Contains(plan.AllOperations, op => op.Kind == OperationKind.Insert);
            Assert.Contains(plan.AllOperations, op => op.Kind == OperationKind.Update);
            Assert.Contains(plan.AllOperations, op => op.Kind == OperationKind.Delete);
        }

        [Fact]
        public void Safe_mode_warns_that_the_write_share_was_redirected_to_reads()
        {
            var profile = WorkloadProfile.Oltp();
            profile.SafeMode = true;
            var plan = WorkloadPlan.Build(profile, TestSchemas.LoadGenOnly());

            Assert.Contains(plan.Diagnostics.Warnings, w => w.Contains("Safe mode redirected", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Safe_mode_produces_a_plan_with_no_write_operations_at_all()
        {
            var profile = WorkloadProfile.Oltp();
            profile.SafeMode = true;
            // Even with a LoadGen table present (writes would otherwise be possible), safe
            // mode must block the write path in the engine, not just redirect percentages.
            var plan = WorkloadPlan.Build(profile, TestSchemas.LoadGenOnly());

            Assert.Equal(100, plan.Profile.ReadPercent);
            Assert.Equal(0, plan.Profile.TotalWritePercent);
            Assert.DoesNotContain(plan.AllOperations, op => op.Kind is OperationKind.Insert or OperationKind.Update or OperationKind.Delete);
        }

        [Fact]
        public void Chaos_mode_with_no_applicable_category_warns_that_nothing_chaotic_will_run()
        {
            var profile = WorkloadProfile.Chaos();
            profile.ChaosBadQueries = false;
            profile.ChaosConcurrency = false;
            profile.ChaosResourceBurners = false;

            var plan = WorkloadPlan.Build(profile, TestSchemas.LoadGenOnly());

            Assert.Empty(plan.ChaosCatalogue);
            Assert.Contains(plan.Diagnostics.Warnings, w => w.Contains("no chaos operation applies", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void A_schema_with_nothing_at_all_still_yields_a_runnable_read_plan()
        {
            var profile = WorkloadProfile.ReadOnly();
            var plan = WorkloadPlan.Build(profile, TestSchemas.Empty());

            Assert.True(plan.IsRunnable);
            Assert.Contains(plan.Diagnostics.Warnings, w => w.Contains("falling back to system catalogue scans", StringComparison.OrdinalIgnoreCase));

            var operation = plan.Next(new Random(1), userIndex: 0);
            Assert.Equal("Metadata scan", operation.Name);
        }

        [Fact]
        public void AdventureWorks_schema_uses_the_AdventureWorks_reads()
        {
            var schema = new SchemaCapabilities
            {
                Server = ServerInfo.Unknown,
                HasLoadGenTable = false,
                LoadGenRowCount = 0,
                HasAdventureWorks = true,
                AdventureWorksTablesFound = SchemaProbe.AdventureWorksTables,
                Tables = [],
            };
            var plan = WorkloadPlan.Build(WorkloadProfile.ReadOnly(), schema);

            Assert.Contains(plan.AllOperations, op => op.Name == "Order lookup");
            Assert.Equal("AdventureWorks sample schema", plan.Schema.DescribeWorkloadSource());
        }

        [Fact]
        public void Describe_mentions_read_and_write_operation_counts()
        {
            var plan = WorkloadPlan.Build(WorkloadProfile.Oltp(), TestSchemas.LoadGenOnly());
            var description = plan.Describe();

            Assert.Contains("read op(s)", description);
            Assert.Contains("write op(s)", description);
        }
    }

    public class NextOperationSelection
    {
        [Fact]
        public void Next_never_returns_null()
        {
            var plan = WorkloadPlan.Build(WorkloadProfile.Oltp(), TestSchemas.LoadGenOnly());
            var random = new Random(1);

            for (var i = 0; i < 2000; i++)
                Assert.NotNull(plan.Next(random, userIndex: i % 4));
        }

        [Fact]
        public void Next_respects_the_configured_read_write_mix_within_a_few_percent()
        {
            var profile = WorkloadProfile.Oltp(); // Read 70 / Insert 12 / Update 12 / Delete 6
            var plan = WorkloadPlan.Build(profile, TestSchemas.LoadGenOnly());
            var random = new Random(20260819);

            const int Draws = 20_000;
            var reads = 0;
            var inserts = 0;
            var updates = 0;
            var deletes = 0;

            for (var i = 0; i < Draws; i++)
            {
                var kind = plan.Next(random, userIndex: 0).Kind;
                switch (kind)
                {
                    case OperationKind.Read: reads++; break;
                    case OperationKind.Insert: inserts++; break;
                    case OperationKind.Update: updates++; break;
                    case OperationKind.Delete: deletes++; break;
                }
            }

            AssertWithinPercent(reads / (double)Draws, 0.70);
            AssertWithinPercent(inserts / (double)Draws, 0.12);
            AssertWithinPercent(updates / (double)Draws, 0.12);
            AssertWithinPercent(deletes / (double)Draws, 0.06);

            static void AssertWithinPercent(double actual, double expected) =>
                Assert.True(Math.Abs(actual - expected) < 0.03,
                    $"Expected fraction near {expected:P1}, got {actual:P1}.");
        }

        [Fact]
        public void Next_draws_from_the_chaos_catalogue_at_roughly_the_configured_intensity()
        {
            var profile = WorkloadProfile.Chaos(); // ChaosIntensityPercent = 60
            var plan = WorkloadPlan.Build(profile, TestSchemas.LoadGenOnly());
            var random = new Random(7);

            const int Draws = 20_000;
            var chaosCount = 0;
            for (var i = 0; i < Draws; i++)
            {
                if (plan.Next(random, userIndex: 0).Kind.IsChaos())
                    chaosCount++;
            }

            var fraction = chaosCount / (double)Draws;
            Assert.True(Math.Abs(fraction - 0.60) < 0.03, $"Expected ~60% chaos draws, got {fraction:P1}.");
        }

        [Fact]
        public void Falls_back_to_a_read_when_the_rolled_category_has_no_operations()
        {
            // 100% insert but no writes are generatable (no LoadGen table): every roll lands
            // in the insert bucket, which is empty, so Next must fall back to a read rather
            // than throwing.
            var profile = WorkloadProfile.ReadOnly();
            profile.SafeMode = false;
            profile.ReadPercent = 0;
            profile.InsertPercent = 100;
            profile.UpdatePercent = 0;
            profile.DeletePercent = 0;

            var plan = WorkloadPlan.Build(profile, TestSchemas.Empty());
            var random = new Random(1);

            for (var i = 0; i < 100; i++)
                Assert.Equal(OperationKind.Read, plan.Next(random, userIndex: 0).Kind);
        }
    }

    public class DeadlockTrapLockOrdering
    {
        private static WorkloadOperation GetDeadlockTrap()
        {
            var profile = WorkloadProfile.Oltp();
            profile.SafeMode = false;
            profile.ChaosMode = true;
            profile.ChaosIntensityPercent = 100;
            profile.ChaosBadQueries = false;
            profile.ChaosResourceBurners = false;
            profile.ChaosConcurrency = true;

            var plan = WorkloadPlan.Build(profile, TestSchemas.LoadGenOnly());
            return plan.ChaosCatalogue.Single(op => op.Name == "Deadlock trap");
        }

        private static OperationContext ContextFor(int userIndex) => new()
        {
            Random = new Random(1),
            Payload = PayloadGenerator.ForRowBudget(0, 1),
            Profile = WorkloadProfile.Oltp(),
            Schema = TestSchemas.LoadGenOnly(),
            UserIndex = userIndex,
        };

        [Fact]
        public void Even_and_odd_users_take_the_two_anchor_rows_in_opposite_orders()
        {
            var trap = GetDeadlockTrap();

            var sqlForUser0 = trap.Build(ContextFor(0)).Sql;
            var sqlForUser1 = trap.Build(ContextFor(1)).Sql;

            Assert.NotEqual(sqlForUser0, sqlForUser1);

            // User 0 (even) locks row 1 before row 2; user 1 (odd) locks row 2 before row 1 —
            // this is what actually closes the deadlock cycle between two concurrent users.
            Assert.True(sqlForUser0.IndexOf("Id = 1", StringComparison.Ordinal) <
                        sqlForUser0.IndexOf("Id = 2", StringComparison.Ordinal));
            Assert.True(sqlForUser1.IndexOf("Id = 2", StringComparison.Ordinal) <
                        sqlForUser1.IndexOf("Id = 1", StringComparison.Ordinal));
        }

        [Fact]
        public void Lock_order_is_consistent_for_every_even_or_every_odd_user()
        {
            var trap = GetDeadlockTrap();

            var user2 = trap.Build(ContextFor(2)).Sql;
            var user4 = trap.Build(ContextFor(4)).Sql;
            Assert.Equal(user2, user4);

            var user1 = trap.Build(ContextFor(1)).Sql;
            var user3 = trap.Build(ContextFor(3)).Sql;
            Assert.Equal(user1, user3);
        }
    }
}
