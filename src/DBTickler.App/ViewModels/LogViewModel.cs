using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Windows.Threading;
using DBTickler.Core.Logging;

namespace DBTickler.App.ViewModels;

public sealed class LogLineViewModel
{
    public LogLineViewModel(LogEntry entry)
    {
        Entry = entry;
        Text = entry.Format();
    }

    public LogEntry Entry { get; }
    public string Text { get; }
    public LogLevel Level => Entry.Level;
}

/// <summary>
/// Buffers log entries and flushes them to the UI on a timer.
///
/// v1 called <c>Dispatcher.BeginInvoke</c> for every single statement it executed and
/// appended to a TextBox that was never trimmed. At a few thousand operations a second that
/// saturates the dispatcher queue — the window stops redrawing, and the log itself becomes
/// the reason the run is slow. Here writes go to a lock-free queue and are drained in
/// batches ten times a second, with a hard cap on what is kept.
/// </summary>
public sealed class LogViewModel : ObservableObject, IDisposable
{
    private const int MaxVisibleLines = 2000;
    private const int MaxLinesPerFlush = 200;

    private readonly ConcurrentQueue<LogEntry> _pending = new();
    private readonly DispatcherTimer _flushTimer;
    private readonly RunLog _log;

    private LogLevel _minimumLevel = LogLevel.Info;
    private bool _autoScroll = true;
    private long _dropped;

    public LogViewModel(RunLog log)
    {
        _log = log;
        _log.EntryWritten += OnEntryWritten;

        _flushTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        _flushTimer.Tick += (_, _) => Flush();
        _flushTimer.Start();
    }

    public ObservableCollection<LogLineViewModel> Lines { get; } = [];

    public LogLevel[] AvailableLevels { get; } =
        [LogLevel.Trace, LogLevel.Debug, LogLevel.Info, LogLevel.Warning, LogLevel.Error];

    /// <summary>
    /// Entries below this level are dropped inside <see cref="RunLog"/> before any string is
    /// built, so leaving the level at Info costs nothing on the hot path.
    /// </summary>
    public LogLevel MinimumLevel
    {
        get => _minimumLevel;
        set
        {
            if (!SetProperty(ref _minimumLevel, value)) return;
            _log.MinimumLevel = value;
            OnPropertyChanged(nameof(VerbosityHint));
        }
    }

    public string VerbosityHint => _minimumLevel <= LogLevel.Trace
        ? "Tracing every operation — this costs throughput on a fast run."
        : $"Showing {_minimumLevel} and above.";

    public bool AutoScroll
    {
        get => _autoScroll;
        set => SetProperty(ref _autoScroll, value);
    }

    /// <summary>Raised after a flush so the view can scroll, without the view model touching controls.</summary>
    public event Action? LinesAppended;

    private void OnEntryWritten(LogEntry entry) => _pending.Enqueue(entry);

    private void Flush()
    {
        var appended = 0;
        while (appended < MaxLinesPerFlush && _pending.TryDequeue(out var entry))
        {
            Lines.Add(new LogLineViewModel(entry));
            appended++;
        }

        if (appended == 0) return;

        while (Lines.Count > MaxVisibleLines)
        {
            Lines.RemoveAt(0);
            _dropped++;
        }

        OnPropertyChanged(nameof(StatusText));
        if (AutoScroll) LinesAppended?.Invoke();
    }

    public string StatusText
    {
        get
        {
            var builder = new StringBuilder($"{Lines.Count:N0} line(s)");
            if (_dropped > 0) builder.Append($", {_dropped:N0} older line(s) discarded");
            if (_log.SuppressedCount > 0) builder.Append($", {_log.SuppressedCount:N0} below the current level");
            return builder.ToString();
        }
    }

    public void Clear()
    {
        while (_pending.TryDequeue(out _)) { }
        Lines.Clear();
        _log.Clear();
        _dropped = 0;
        OnPropertyChanged(nameof(StatusText));
    }

    public string ToPlainText() => string.Join(Environment.NewLine, Lines.Select(line => line.Text));

    public void Dispose()
    {
        _flushTimer.Stop();
        _log.EntryWritten -= OnEntryWritten;
    }
}
