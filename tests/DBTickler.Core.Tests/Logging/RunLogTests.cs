using DBTickler.Core.Logging;

namespace DBTickler.Core.Tests.Logging;

public class RunLogTests
{
    [Fact]
    public void Entries_at_or_above_the_minimum_level_are_kept()
    {
        var log = new RunLog { MinimumLevel = LogLevel.Info };
        log.Info("hello");
        log.Warning("careful");

        var snapshot = log.Snapshot();
        Assert.Equal(2, snapshot.Count);
        Assert.Equal(0, log.SuppressedCount);
    }

    [Fact]
    public void Entries_below_the_minimum_level_are_dropped_and_counted_as_suppressed()
    {
        var log = new RunLog { MinimumLevel = LogLevel.Warning };
        log.Info("ignored");
        log.Debug("also ignored");
        log.Error("kept");

        var snapshot = log.Snapshot();
        Assert.Single(snapshot);
        Assert.Equal(LogLevel.Error, snapshot[0].Level);
        Assert.Equal(2, log.SuppressedCount);
    }

    [Fact]
    public void EntryWritten_fires_only_for_accepted_entries()
    {
        var log = new RunLog { MinimumLevel = LogLevel.Warning };
        var received = new List<LogEntry>();
        log.EntryWritten += entry => received.Add(entry);

        log.Debug("suppressed, no event");
        log.Error("accepted, fires the event");

        var entry = Assert.Single(received);
        Assert.Equal("accepted, fires the event", entry.Message);
    }

    [Fact]
    public void Oldest_entries_are_discarded_once_capacity_is_exceeded()
    {
        var log = new RunLog(capacity: 5);
        for (var i = 0; i < 10; i++)
            log.Info($"entry-{i}");

        var snapshot = log.Snapshot();

        Assert.Equal(5, snapshot.Count);
        // The newest entries should have survived, the oldest evicted.
        Assert.Equal("entry-9", snapshot[^1].Message);
        Assert.DoesNotContain(snapshot, e => e.Message == "entry-0");
    }

    [Fact]
    public void Clear_resets_entries_and_suppressed_count()
    {
        var log = new RunLog { MinimumLevel = LogLevel.Error };
        log.Info("suppressed");
        log.Error("kept");

        log.Clear();

        Assert.Empty(log.Snapshot());
        Assert.Equal(0, log.SuppressedCount);
    }

    [Fact]
    public void All_convenience_methods_map_to_the_matching_level()
    {
        var log = new RunLog { MinimumLevel = LogLevel.Trace };
        log.Trace("t");
        log.Debug("d");
        log.Info("i");
        log.Success("s");
        log.Warning("w");
        log.Error("e");

        var levels = log.Snapshot().Select(entry => entry.Level).ToArray();
        Assert.Equal(
            [LogLevel.Trace, LogLevel.Debug, LogLevel.Info, LogLevel.Success, LogLevel.Warning, LogLevel.Error],
            levels);
    }

    [Fact]
    public void LogEntry_Format_includes_a_timestamp_and_the_message()
    {
        var entry = new LogEntry(DateTime.UtcNow, LogLevel.Info, "something happened");
        Assert.Contains("something happened", entry.Format());
        Assert.StartsWith("[", entry.Format());
    }

    public class NullRunLogTests
    {
        [Fact]
        public void Discards_everything_without_throwing()
        {
            var log = NullRunLog.Instance;
            log.Write(LogLevel.Error, "does nothing");
            log.Info("also nothing");
            // No observable state to assert on; the point is that nothing above throws.
        }

        [Fact]
        public void Default_minimum_level_is_error()
        {
            Assert.Equal(LogLevel.Error, NullRunLog.Instance.MinimumLevel);
        }

        [Fact]
        public void Instance_is_a_singleton()
        {
            Assert.Same(NullRunLog.Instance, NullRunLog.Instance);
        }
    }
}
