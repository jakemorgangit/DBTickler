using System.Collections.Concurrent;

namespace DBTickler.Core.Logging;

public enum LogLevel
{
    Trace = 0,
    Debug = 1,
    Info = 2,
    Success = 3,
    Warning = 4,
    Error = 5,
}

public readonly record struct LogEntry(DateTime TimestampUtc, LogLevel Level, string Message)
{
    public string Format() => $"[{TimestampUtc.ToLocalTime():HH:mm:ss}] {Message}";
}

public interface IRunLog
{
    LogLevel MinimumLevel { get; set; }
    void Write(LogLevel level, string message);
}

public static class RunLogExtensions
{
    public static void Trace(this IRunLog log, string message) => log.Write(LogLevel.Trace, message);
    public static void Debug(this IRunLog log, string message) => log.Write(LogLevel.Debug, message);
    public static void Info(this IRunLog log, string message) => log.Write(LogLevel.Info, message);
    public static void Success(this IRunLog log, string message) => log.Write(LogLevel.Success, message);
    public static void Warning(this IRunLog log, string message) => log.Write(LogLevel.Warning, message);
    public static void Error(this IRunLog log, string message) => log.Write(LogLevel.Error, message);
}

/// <summary>
/// A bounded, thread-safe log. Two properties matter for a load generator:
///
/// It cannot grow without limit — v1 appended every statement to a TextBox that was never
/// trimmed, so a long run ended in an out-of-memory crash. Here the oldest entries are
/// discarded once <see cref="Capacity"/> is reached.
///
/// It cannot flood the consumer — entries below <see cref="MinimumLevel"/> are dropped at
/// the source, before any string is formatted, so per-operation tracing costs nothing when
/// it is switched off.
/// </summary>
public sealed class RunLog : IRunLog
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();
    private int _count;
    private long _suppressed;

    public RunLog(int capacity = 5000) => Capacity = capacity;

    public int Capacity { get; }

    public LogLevel MinimumLevel { get; set; } = LogLevel.Info;

    /// <summary>Entries dropped because they were below the minimum level.</summary>
    public long SuppressedCount => Interlocked.Read(ref _suppressed);

    /// <summary>
    /// Raised for each accepted entry. Handlers run on the calling thread — which is a
    /// virtual user's thread — so a UI consumer must marshal and batch rather than touching
    /// controls here.
    /// </summary>
    public event Action<LogEntry>? EntryWritten;

    public void Write(LogLevel level, string message)
    {
        if (level < MinimumLevel)
        {
            Interlocked.Increment(ref _suppressed);
            return;
        }

        var entry = new LogEntry(DateTime.UtcNow, level, message);
        _entries.Enqueue(entry);

        if (Interlocked.Increment(ref _count) > Capacity && _entries.TryDequeue(out _))
            Interlocked.Decrement(ref _count);

        EntryWritten?.Invoke(entry);
    }

    public IReadOnlyList<LogEntry> Snapshot() => [.. _entries];

    public void Clear()
    {
        while (_entries.TryDequeue(out _)) { }
        Interlocked.Exchange(ref _count, 0);
        Interlocked.Exchange(ref _suppressed, 0);
    }
}

/// <summary>A log that discards everything, for tests and headless paths that do not want output.</summary>
public sealed class NullRunLog : IRunLog
{
    public static NullRunLog Instance { get; } = new();
    public LogLevel MinimumLevel { get; set; } = LogLevel.Error;
    public void Write(LogLevel level, string message) { }
}
