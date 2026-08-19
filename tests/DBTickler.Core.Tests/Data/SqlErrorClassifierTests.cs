using DBTickler.Core.Data;

namespace DBTickler.Core.Tests.Data;

/// <summary>
/// Covers the paths reachable without a real <see cref="Microsoft.Data.SqlClient.SqlException"/>,
/// which cannot be constructed outside the driver itself.
/// </summary>
public class SqlErrorClassifierTests
{
    public class Classification
    {
        [Fact]
        public void TimeoutException_is_classified_as_command_timeout() =>
            Assert.Equal(SqlFailureKind.CommandTimeout, SqlErrorClassifier.Classify(new TimeoutException()));

        [Fact]
        public void Pool_exhaustion_message_is_classified_as_a_connection_failure()
        {
            var exception = new InvalidOperationException(
                "Timeout expired. The timeout period elapsed prior to obtaining a connection from the pool.");

            Assert.Equal(SqlFailureKind.Connection, SqlErrorClassifier.Classify(exception));
        }

        [Fact]
        public void Pool_message_match_is_case_insensitive()
        {
            var exception = new InvalidOperationException("connection POOL is exhausted");
            Assert.Equal(SqlFailureKind.Connection, SqlErrorClassifier.Classify(exception));
        }

        [Fact]
        public void InvalidOperationException_unrelated_to_pooling_is_classified_as_other()
        {
            var exception = new InvalidOperationException("The connection is already open.");
            Assert.Equal(SqlFailureKind.Other, SqlErrorClassifier.Classify(exception));
        }

        [Fact]
        public void An_unrecognised_exception_type_is_classified_as_other() =>
            Assert.Equal(SqlFailureKind.Other, SqlErrorClassifier.Classify(new ArgumentException("bad argument")));

        [Fact]
        public void Default_constructed_invalid_operation_exception_classifies_as_other_without_throwing()
        {
            var exception = new InvalidOperationException();
            Assert.Equal(SqlFailureKind.Other, SqlErrorClassifier.Classify(exception));
        }
    }

    public class ErrorNumberExtraction
    {
        [Fact]
        public void Non_sql_exceptions_have_no_error_number() =>
            Assert.Null(SqlErrorClassifier.ErrorNumber(new TimeoutException()));

        [Fact]
        public void Generic_exceptions_have_no_error_number() =>
            Assert.Null(SqlErrorClassifier.ErrorNumber(new InvalidOperationException("pool")));
    }

    public class TransientAndFatalMappings
    {
        [Theory]
        [InlineData(SqlFailureKind.DeadlockVictim, true)]
        [InlineData(SqlFailureKind.LockTimeout, true)]
        [InlineData(SqlFailureKind.Connection, true)]
        [InlineData(SqlFailureKind.CommandTimeout, false)]
        [InlineData(SqlFailureKind.MissingObject, false)]
        [InlineData(SqlFailureKind.Permission, false)]
        [InlineData(SqlFailureKind.Killed, false)]
        [InlineData(SqlFailureKind.DataError, false)]
        [InlineData(SqlFailureKind.ResourceExhausted, false)]
        [InlineData(SqlFailureKind.Other, false)]
        public void IsTransient_matches_the_documented_set(SqlFailureKind kind, bool expected) =>
            Assert.Equal(expected, SqlErrorClassifier.IsTransient(kind));

        [Theory]
        [InlineData(SqlFailureKind.MissingObject, true)]
        [InlineData(SqlFailureKind.Permission, true)]
        [InlineData(SqlFailureKind.DeadlockVictim, false)]
        [InlineData(SqlFailureKind.LockTimeout, false)]
        [InlineData(SqlFailureKind.CommandTimeout, false)]
        [InlineData(SqlFailureKind.Connection, false)]
        [InlineData(SqlFailureKind.Killed, false)]
        [InlineData(SqlFailureKind.DataError, false)]
        [InlineData(SqlFailureKind.ResourceExhausted, false)]
        [InlineData(SqlFailureKind.Other, false)]
        public void IsFatalForRun_matches_the_documented_set(SqlFailureKind kind, bool expected) =>
            Assert.Equal(expected, SqlErrorClassifier.IsFatalForRun(kind));

        [Fact]
        public void Every_SqlFailureKind_value_is_covered_by_the_transient_and_fatal_theories()
        {
            // Guards against a new enum member being added without updating the theories above.
            var covered = new[]
            {
                SqlFailureKind.Other, SqlFailureKind.DeadlockVictim, SqlFailureKind.LockTimeout,
                SqlFailureKind.CommandTimeout, SqlFailureKind.Connection, SqlFailureKind.MissingObject,
                SqlFailureKind.Permission, SqlFailureKind.Killed, SqlFailureKind.DataError,
                SqlFailureKind.ResourceExhausted,
            };

            Assert.Equal(Enum.GetValues<SqlFailureKind>().OrderBy(v => v), covered.OrderBy(v => v));
        }
    }

    public class Descriptions
    {
        [Theory]
        [InlineData(SqlFailureKind.DeadlockVictim, "Deadlock victim (1205)")]
        [InlineData(SqlFailureKind.LockTimeout, "Lock request timeout (1222)")]
        [InlineData(SqlFailureKind.CommandTimeout, "Command timeout")]
        [InlineData(SqlFailureKind.Connection, "Connection failure")]
        [InlineData(SqlFailureKind.MissingObject, "Missing object or column")]
        [InlineData(SqlFailureKind.Permission, "Permission denied")]
        [InlineData(SqlFailureKind.Killed, "Session killed")]
        [InlineData(SqlFailureKind.DataError, "Data error")]
        [InlineData(SqlFailureKind.ResourceExhausted, "Server resource exhausted")]
        [InlineData(SqlFailureKind.Other, "Other")]
        public void DescribeKind_returns_the_documented_label(SqlFailureKind kind, string expected) =>
            Assert.Equal(expected, SqlErrorClassifier.DescribeKind(kind));

        [Fact]
        public void DescribeKind_labels_are_all_distinct()
        {
            var labels = Enum.GetValues<SqlFailureKind>().Select(SqlErrorClassifier.DescribeKind).ToList();
            Assert.Equal(labels.Count, labels.Distinct().Count());
        }
    }
}
