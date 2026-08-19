using DBTickler.Core.Configuration;

namespace DBTickler.Core.Tests.Configuration;

public class WorkloadProfileTests
{
    /// <summary>A profile that passes every Validate() rule, for tests that mutate one field at a time.</summary>
    private static WorkloadProfile Baseline() => new()
    {
        VirtualUsers = 16,
        BatchRows = 50,
        PayloadBytes = 2048,
        DurationSeconds = 60,
        RampUpSeconds = 5,
        ReadPercent = 70,
        InsertPercent = 12,
        UpdatePercent = 12,
        DeletePercent = 6,
        ThinkTimeMs = 25,
        CommandTimeoutSeconds = 30,
        MaxErrors = 500,
        SafeMode = false,
        ChaosIntensityPercent = 25,
        MaxRowsPerRead = 5000,
    };

    public class Validation
    {
        [Fact]
        public void Baseline_profile_is_valid()
        {
            var result = Baseline().Validate();
            Assert.True(result.IsValid, string.Join("; ", result.Errors));
        }

        [Fact]
        public void Default_profile_is_valid_but_warns_that_safe_mode_redirects_writes()
        {
            var result = new WorkloadProfile().Validate();

            Assert.True(result.IsValid, string.Join("; ", result.Errors));
            Assert.Contains(result.Warnings, w => w.Contains("Safe mode is on", StringComparison.OrdinalIgnoreCase));
        }

        [Theory]
        [InlineData(69)]
        [InlineData(101)]
        public void Dml_mix_not_summing_to_100_percent_is_an_error(int readPercent)
        {
            var profile = Baseline();
            profile.ReadPercent = readPercent; // Insert12+Update12+Delete6 = 30, so total != 100

            var result = profile.Validate();

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Contains("must total 100%"));
        }

        [Fact]
        public void Dml_mix_summing_to_exactly_100_percent_has_no_mix_error()
        {
            var profile = Baseline();
            profile.ReadPercent = 25;
            profile.InsertPercent = 25;
            profile.UpdatePercent = 25;
            profile.DeletePercent = 25;

            var result = profile.Validate();

            Assert.DoesNotContain(result.Errors, e => e.Contains("must total 100%"));
        }

        [Theory]
        [InlineData(0, false)]
        [InlineData(1, true)]
        [InlineData(512, true)]
        [InlineData(513, false)]
        public void VirtualUsers_must_be_between_1_and_512(int virtualUsers, bool expectedValid)
        {
            var profile = Baseline();
            profile.VirtualUsers = virtualUsers;

            Assert.Equal(expectedValid, profile.Validate().IsValid);
        }

        [Theory]
        [InlineData(0, false)]
        [InlineData(1, true)]
        [InlineData(100_000, true)]
        [InlineData(100_001, false)]
        public void BatchRows_must_be_between_1_and_100_000(int batchRows, bool expectedValid)
        {
            var profile = Baseline();
            profile.BatchRows = batchRows;

            Assert.Equal(expectedValid, profile.Validate().IsValid);
        }

        [Theory]
        [InlineData(-1, false)]
        [InlineData(0, true)]
        [InlineData(8 * 1024 * 1024, true)]
        [InlineData(8 * 1024 * 1024 + 1, false)]
        public void PayloadBytes_must_be_between_0_and_8mb(int payloadBytes, bool expectedValid)
        {
            var profile = Baseline();
            profile.PayloadBytes = payloadBytes;

            Assert.Equal(expectedValid, profile.Validate().IsValid);
        }

        [Theory]
        [InlineData(-1, false)]
        [InlineData(0, true)] // 0 means unlimited
        [InlineData(86_400, true)]
        [InlineData(86_401, false)]
        public void DurationSeconds_must_be_between_0_and_86400(int durationSeconds, bool expectedValid)
        {
            var profile = Baseline();
            profile.DurationSeconds = durationSeconds;

            Assert.Equal(expectedValid, profile.Validate().IsValid);
        }

        [Fact]
        public void RampUpSeconds_may_not_exceed_the_configured_duration()
        {
            var profile = Baseline();
            profile.DurationSeconds = 60;
            profile.RampUpSeconds = 61;

            Assert.False(profile.Validate().IsValid);
        }

        [Fact]
        public void RampUpSeconds_equal_to_duration_is_allowed()
        {
            var profile = Baseline();
            profile.DurationSeconds = 60;
            profile.RampUpSeconds = 60;

            Assert.True(profile.Validate().IsValid);
        }

        [Fact]
        public void RampUpSeconds_may_not_be_negative()
        {
            var profile = Baseline();
            profile.RampUpSeconds = -1;

            Assert.False(profile.Validate().IsValid);
        }

        [Fact]
        public void RampUpSeconds_is_capped_at_3600_when_duration_is_unlimited()
        {
            var profile = Baseline();
            profile.DurationSeconds = 0;
            profile.RampUpSeconds = 3600;

            Assert.True(profile.Validate().IsValid);

            profile.RampUpSeconds = 3601;
            Assert.False(profile.Validate().IsValid);
        }

        [Theory]
        [InlineData(-1, false)]
        [InlineData(0, true)]
        [InlineData(600_000, true)]
        [InlineData(600_001, false)]
        public void ThinkTimeMs_must_be_between_0_and_600_000(int thinkTimeMs, bool expectedValid)
        {
            var profile = Baseline();
            profile.ThinkTimeMs = thinkTimeMs;

            Assert.Equal(expectedValid, profile.Validate().IsValid);
        }

        [Theory]
        [InlineData(0, false)]
        [InlineData(1, true)]
        [InlineData(3600, true)]
        [InlineData(3601, false)]
        public void CommandTimeoutSeconds_must_be_between_1_and_3600(int timeout, bool expectedValid)
        {
            var profile = Baseline();
            profile.CommandTimeoutSeconds = timeout;

            Assert.Equal(expectedValid, profile.Validate().IsValid);
        }

        [Fact]
        public void MaxErrors_cannot_be_negative()
        {
            var profile = Baseline();
            profile.MaxErrors = -1;

            Assert.False(profile.Validate().IsValid);
        }

        [Fact]
        public void MaxErrors_of_zero_is_valid_and_means_unlimited()
        {
            var profile = Baseline();
            profile.MaxErrors = 0;

            Assert.True(profile.Validate().IsValid);
        }

        [Theory]
        [InlineData(-1, false)]
        [InlineData(0, true)]
        [InlineData(100, true)]
        [InlineData(101, false)]
        public void ChaosIntensityPercent_must_be_between_0_and_100(int intensity, bool expectedValid)
        {
            var profile = Baseline();
            profile.ChaosIntensityPercent = intensity;

            Assert.Equal(expectedValid, profile.Validate().IsValid);
        }

        [Theory]
        [InlineData(0, false)]
        [InlineData(1, true)]
        [InlineData(10_000_000, true)]
        [InlineData(10_000_001, false)]
        public void MaxRowsPerRead_must_be_between_1_and_10_million(int maxRows, bool expectedValid)
        {
            var profile = Baseline();
            profile.MaxRowsPerRead = maxRows;

            Assert.Equal(expectedValid, profile.Validate().IsValid);
        }

        [Theory]
        [InlineData(-1, false)]
        [InlineData(101, false)]
        public void Each_dml_percentage_individually_must_be_0_to_100(int badValue, bool expectedValid)
        {
            var profile = Baseline();
            profile.ReadPercent = badValue;

            Assert.Equal(expectedValid, profile.Validate().IsValid);
        }

        [Fact]
        public void Safe_mode_with_a_nonzero_write_share_warns_that_writes_become_reads()
        {
            var profile = Baseline();
            profile.SafeMode = true;

            var result = profile.Validate();

            Assert.Contains(result.Warnings, w => w.Contains("Safe mode is on", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Safe_mode_with_no_writes_requested_does_not_warn()
        {
            var profile = Baseline();
            profile.SafeMode = true;
            profile.ReadPercent = 100;
            profile.InsertPercent = 0;
            profile.UpdatePercent = 0;
            profile.DeletePercent = 0;

            var result = profile.Validate();

            Assert.DoesNotContain(result.Warnings, w => w.Contains("Safe mode is on", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Chaos_mode_with_no_category_selected_warns_that_the_run_behaves_normally()
        {
            var profile = Baseline();
            profile.ChaosMode = true;
            profile.ChaosBadQueries = false;
            profile.ChaosConcurrency = false;
            profile.ChaosResourceBurners = false;

            var result = profile.Validate();

            Assert.Contains(result.Warnings, w => w.Contains("no chaos category is selected", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Chaos_mode_with_a_category_selected_does_not_warn_about_missing_categories()
        {
            var profile = Baseline();
            profile.ChaosMode = true;
            profile.ChaosBadQueries = true;
            profile.ChaosConcurrency = false;
            profile.ChaosResourceBurners = false;

            var result = profile.Validate();

            Assert.DoesNotContain(result.Warnings, w => w.Contains("no chaos category is selected", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Heavy_write_concurrency_above_128_users_warns_about_blocking()
        {
            var profile = Baseline();
            profile.SafeMode = false;
            profile.VirtualUsers = 129;

            var result = profile.Validate();

            Assert.Contains(result.Warnings, w => w.Contains("heavy blocking", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Write_concurrency_at_128_users_does_not_warn_about_blocking()
        {
            var profile = Baseline();
            profile.SafeMode = false;
            profile.VirtualUsers = 128;

            var result = profile.Validate();

            Assert.DoesNotContain(result.Warnings, w => w.Contains("heavy blocking", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Large_payload_budget_warns_about_network_bottleneck()
        {
            var profile = Baseline();
            profile.PayloadBytes = 1_000_000;
            profile.BatchRows = 100; // ~95 MB per write op

            var result = profile.Validate();

            Assert.Contains(result.Warnings, w => w.Contains("bottleneck", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Modest_payload_budget_does_not_warn_about_network_bottleneck()
        {
            var profile = Baseline();
            profile.PayloadBytes = 2048;
            profile.BatchRows = 50;

            var result = profile.Validate();

            Assert.DoesNotContain(result.Warnings, w => w.Contains("bottleneck", StringComparison.OrdinalIgnoreCase));
        }
    }

    public class Presets
    {
        public static IEnumerable<object[]> AllPresetNames() =>
            WorkloadProfile.Presets.Keys.Select(name => new object[] { name });

        [Theory]
        [MemberData(nameof(AllPresetNames))]
        public void Every_preset_produces_a_valid_profile(string presetName)
        {
            var profile = WorkloadProfile.Presets[presetName]();
            var result = profile.Validate();

            Assert.True(result.IsValid, $"Preset '{presetName}' is invalid: {result.FormatErrors()}");
        }

        [Fact]
        public void Presets_dictionary_contains_the_four_documented_names()
        {
            Assert.Equal(4, WorkloadProfile.Presets.Count);
            Assert.Contains("readonly", WorkloadProfile.Presets.Keys, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("oltp", WorkloadProfile.Presets.Keys, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("write-heavy", WorkloadProfile.Presets.Keys, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("chaos", WorkloadProfile.Presets.Keys, StringComparer.OrdinalIgnoreCase);
        }

        [Fact]
        public void Preset_lookup_is_case_insensitive()
        {
            Assert.True(WorkloadProfile.Presets.ContainsKey("OLTP"));
            Assert.True(WorkloadProfile.Presets.ContainsKey("ReadOnly"));
        }

        [Fact]
        public void ReadOnly_preset_is_reads_only_and_safe()
        {
            var profile = WorkloadProfile.ReadOnly();
            Assert.True(profile.SafeMode);
            Assert.Equal(100, profile.ReadPercent);
            Assert.Equal(0, profile.TotalWritePercent);
            Assert.False(profile.WillWrite);
        }

        [Fact]
        public void Chaos_preset_has_chaos_mode_enabled()
        {
            var profile = WorkloadProfile.Chaos();
            Assert.True(profile.ChaosMode);
            Assert.True(profile.ChaosIntensityPercent > 0);
        }
    }

    public class Normalization
    {
        [Fact]
        public void Safe_mode_folds_the_entire_write_share_into_reads()
        {
            var profile = Baseline();
            profile.SafeMode = true;
            profile.ReadPercent = 50;
            profile.InsertPercent = 20;
            profile.UpdatePercent = 20;
            profile.DeletePercent = 10;

            var normalized = profile.Normalized();

            Assert.Equal(100, normalized.ReadPercent);
            Assert.Equal(0, normalized.InsertPercent);
            Assert.Equal(0, normalized.UpdatePercent);
            Assert.Equal(0, normalized.DeletePercent);
            Assert.Equal(0, normalized.TotalWritePercent);
        }

        [Fact]
        public void Non_safe_mode_leaves_the_mix_unchanged()
        {
            var profile = Baseline();
            profile.SafeMode = false;
            profile.ReadPercent = 50;
            profile.InsertPercent = 20;
            profile.UpdatePercent = 20;
            profile.DeletePercent = 10;

            var normalized = profile.Normalized();

            Assert.Equal(50, normalized.ReadPercent);
            Assert.Equal(20, normalized.InsertPercent);
            Assert.Equal(20, normalized.UpdatePercent);
            Assert.Equal(10, normalized.DeletePercent);
        }

        [Fact]
        public void Normalized_and_Clone_both_return_a_different_instance()
        {
            var profile = Baseline();

            Assert.NotSame(profile, profile.Normalized());
            Assert.NotSame(profile, profile.Clone());
        }

        [Fact]
        public void Mutating_the_clone_does_not_affect_the_original()
        {
            var profile = Baseline();
            var clone = profile.Clone();

            clone.VirtualUsers = 999;

            Assert.NotEqual(999, profile.VirtualUsers);
        }
    }

    public class DerivedProperties
    {
        [Fact]
        public void TotalWritePercent_sums_insert_update_delete()
        {
            var profile = Baseline();
            profile.InsertPercent = 10;
            profile.UpdatePercent = 20;
            profile.DeletePercent = 5;

            Assert.Equal(35, profile.TotalWritePercent);
        }

        [Fact]
        public void WillWrite_is_false_in_safe_mode_even_with_writes_configured()
        {
            var profile = Baseline();
            profile.SafeMode = true;
            profile.InsertPercent = 50;

            Assert.False(profile.WillWrite);
        }

        [Fact]
        public void WillWrite_is_true_outside_safe_mode_with_a_nonzero_write_share()
        {
            var profile = Baseline();
            profile.SafeMode = false;
            profile.InsertPercent = 1;

            Assert.True(profile.WillWrite);
        }

        [Fact]
        public void WillWrite_is_false_outside_safe_mode_with_zero_write_share()
        {
            var profile = Baseline();
            profile.SafeMode = false;
            profile.ReadPercent = 100;
            profile.InsertPercent = 0;
            profile.UpdatePercent = 0;
            profile.DeletePercent = 0;

            Assert.False(profile.WillWrite);
        }
    }
}
