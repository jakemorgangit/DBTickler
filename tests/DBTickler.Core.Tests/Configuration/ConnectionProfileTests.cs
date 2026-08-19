using DBTickler.Core.Configuration;
using Microsoft.Data.SqlClient;

namespace DBTickler.Core.Tests.Configuration;

public class ConnectionProfileTests
{
    private static ConnectionProfile Baseline() => new()
    {
        Server = "sql01",
        Database = "AdventureWorks2022",
        IntegratedSecurity = true,
        MaxPoolSize = 200,
        ConnectTimeoutSeconds = 15,
    };

    public class ApplicationNameStamping
    {
        [Fact]
        public void Integrated_security_connection_string_always_carries_the_application_name()
        {
            var profile = Baseline();
            profile.IntegratedSecurity = true;

            var builder = new SqlConnectionStringBuilder(profile.BuildConnectionString());

            Assert.Equal("DBTickler", builder.ApplicationName);
        }

        [Fact]
        public void Sql_authentication_connection_string_also_carries_the_application_name()
        {
            var profile = Baseline();
            profile.IntegratedSecurity = false;
            profile.Username = "loadtest";
            profile.Password = "s3cret!";

            var builder = new SqlConnectionStringBuilder(profile.BuildConnectionString());

            Assert.Equal("DBTickler", builder.ApplicationName);
        }

        [Fact]
        public void Application_name_matches_the_public_constant_the_kill_feature_matches_on()
        {
            Assert.Equal("DBTickler", ConnectionProfile.ApplicationName);
        }
    }

    public class AuthenticationMode
    {
        [Fact]
        public void Integrated_security_true_sets_windows_authentication_and_no_credentials()
        {
            var profile = Baseline();
            profile.IntegratedSecurity = true;
            profile.Username = "should-be-ignored";
            profile.Password = "should-be-ignored";

            var builder = new SqlConnectionStringBuilder(profile.BuildConnectionString());

            Assert.True(builder.IntegratedSecurity);
            Assert.True(string.IsNullOrEmpty(builder.UserID));
        }

        [Fact]
        public void Integrated_security_false_sets_sql_authentication_with_username_and_password()
        {
            var profile = Baseline();
            profile.IntegratedSecurity = false;
            profile.Username = "loadtest";
            profile.Password = "s3cret!";

            var builder = new SqlConnectionStringBuilder(profile.BuildConnectionString());

            Assert.False(builder.IntegratedSecurity);
            Assert.Equal("loadtest", builder.UserID);
            Assert.Equal("s3cret!", builder.Password);
        }
    }

    public class PoolSizeAndTimeouts
    {
        [Fact]
        public void Default_max_pool_size_from_the_profile_is_used_when_no_override_is_given()
        {
            var profile = Baseline();
            profile.MaxPoolSize = 250;

            var builder = new SqlConnectionStringBuilder(profile.BuildConnectionString());

            Assert.Equal(250, builder.MaxPoolSize);
        }

        [Fact]
        public void Explicit_override_wins_over_the_profile_value()
        {
            var profile = Baseline();
            profile.MaxPoolSize = 250;

            var builder = new SqlConnectionStringBuilder(profile.BuildConnectionString(maxPoolSize: 900));

            Assert.Equal(900, builder.MaxPoolSize);
        }

        [Theory]
        [InlineData(0, 1)]
        [InlineData(-5, 1)]
        [InlineData(50_000, 32767)]
        public void Max_pool_size_override_is_clamped_to_a_legal_range(int requested, int expected)
        {
            var profile = Baseline();
            var builder = new SqlConnectionStringBuilder(profile.BuildConnectionString(maxPoolSize: requested));

            Assert.Equal(expected, builder.MaxPoolSize);
        }

        [Theory]
        [InlineData(500, 300)]
        [InlineData(0, 1)]
        [InlineData(-10, 1)]
        [InlineData(30, 30)]
        public void Connect_timeout_is_clamped_between_1_and_300_seconds(int configured, int expected)
        {
            var profile = Baseline();
            profile.ConnectTimeoutSeconds = configured;

            var builder = new SqlConnectionStringBuilder(profile.BuildConnectionString());

            Assert.Equal(expected, builder.ConnectTimeout);
        }

        [Fact]
        public void Pooling_is_always_enabled()
        {
            var profile = Baseline();
            var builder = new SqlConnectionStringBuilder(profile.BuildConnectionString());

            Assert.True(builder.Pooling);
        }
    }

    public class Redaction
    {
        [Fact]
        public void Redacted_connection_string_never_contains_the_password()
        {
            var profile = Baseline();
            profile.IntegratedSecurity = false;
            profile.Username = "loadtest";
            profile.Password = "SuperSecretPassword123!";

            var redacted = profile.BuildRedactedConnectionString();

            Assert.DoesNotContain("SuperSecretPassword123!", redacted);
        }

        [Fact]
        public void Redacted_connection_string_still_contains_the_server_and_database()
        {
            var profile = Baseline();
            profile.IntegratedSecurity = false;
            profile.Username = "loadtest";
            profile.Password = "hunter2";

            var redacted = profile.BuildRedactedConnectionString();
            var builder = new SqlConnectionStringBuilder(redacted);

            Assert.Equal("sql01", builder.DataSource);
            Assert.Equal("AdventureWorks2022", builder.InitialCatalog);
        }

        [Fact]
        public void Redacted_connection_string_with_no_password_does_not_throw()
        {
            var profile = Baseline();
            profile.IntegratedSecurity = true;

            var redacted = profile.BuildRedactedConnectionString();

            Assert.DoesNotContain("Password", redacted, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Full_connection_string_does_contain_the_real_password_unlike_the_redacted_one()
        {
            // Sanity check that the redaction test above is actually exercising redaction,
            // rather than the password never appearing in the first place.
            var profile = Baseline();
            profile.IntegratedSecurity = false;
            profile.Username = "loadtest";
            profile.Password = "SuperSecretPassword123!";

            Assert.Contains("SuperSecretPassword123!", profile.BuildConnectionString());
        }
    }

    public class Validation
    {
        [Fact]
        public void Valid_profile_has_no_errors()
        {
            var result = Baseline().Validate();
            Assert.True(result.IsValid);
        }

        [Fact]
        public void Missing_server_is_an_error()
        {
            var profile = Baseline();
            profile.Server = "";

            Assert.False(profile.Validate().IsValid);
        }

        [Fact]
        public void Missing_database_is_an_error()
        {
            var profile = Baseline();
            profile.Database = "   ";

            Assert.False(profile.Validate().IsValid);
        }

        [Fact]
        public void Sql_authentication_without_a_username_is_an_error()
        {
            var profile = Baseline();
            profile.IntegratedSecurity = false;
            profile.Username = "";

            Assert.False(profile.Validate().IsValid);
        }

        [Fact]
        public void Disabled_encryption_warns_about_cleartext_traffic()
        {
            var profile = Baseline();
            profile.Encrypt = false;

            var result = profile.Validate();

            Assert.Contains(result.Warnings, w => w.Contains("clear text", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Trust_server_certificate_warns_about_disabled_validation()
        {
            var profile = Baseline();
            profile.Encrypt = true;
            profile.TrustServerCertificate = true;

            var result = profile.Validate();

            Assert.Contains(result.Warnings, w => w.Contains("TrustServerCertificate", StringComparison.OrdinalIgnoreCase));
        }
    }

    public class CloningAndDescribing
    {
        [Fact]
        public void Clone_returns_a_separate_instance_with_equal_values()
        {
            var profile = Baseline();
            var clone = profile.Clone();

            Assert.NotSame(profile, clone);
            Assert.Equal(profile.Server, clone.Server);

            clone.Server = "different-server";
            Assert.NotEqual(clone.Server, profile.Server);
        }

        [Fact]
        public void Describe_combines_server_and_database()
        {
            var profile = Baseline();
            Assert.Equal("sql01/AdventureWorks2022", profile.Describe());
        }
    }
}
