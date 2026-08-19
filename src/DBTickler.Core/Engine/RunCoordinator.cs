using DBTickler.Core.Logging;

namespace DBTickler.Core.Engine;

/// <summary>
/// Shared stop state for a run. Every virtual user consults this, so the error budget is a
/// single global count rather than a per-worker one — v1 compared each worker's own error
/// count against the configured limit, quietly multiplying the real threshold by the thread
/// count.
/// </summary>
internal sealed class RunCoordinator
{
    /// <summary>
    /// How many "the workload cannot run here" errors are tolerated before the run is
    /// abandoned. A handful can be legitimate — one operation referencing an object the probe
    /// did not check — but a steady stream means every operation will fail the same way.
    /// </summary>
    private const int FatalErrorThreshold = 10;

    private readonly CancellationTokenSource _cancellation;
    private readonly IRunLog _log;
    private readonly int _maxErrors;

    private long _errorCount;
    private int _fatalCandidates;
    private int _stopReason = (int)StopReason.None;
    private string? _fatalDetail;

    public RunCoordinator(CancellationTokenSource cancellation, IRunLog log, int maxErrors)
    {
        _cancellation = cancellation;
        _log = log;
        _maxErrors = maxErrors;
    }

    public bool ShouldStop => _cancellation.IsCancellationRequested;

    public StopReason Reason => (StopReason)Volatile.Read(ref _stopReason);

    /// <summary>Extra context for a fatal stop, shown alongside the reason.</summary>
    public string? FatalDetail => Volatile.Read(ref _fatalDetail);

    public long ErrorCount => Interlocked.Read(ref _errorCount);

    public void ReportError()
    {
        var total = Interlocked.Increment(ref _errorCount);

        if (_maxErrors > 0 && total >= _maxErrors)
        {
            if (TrySetReason(StopReason.ErrorBudgetExceeded))
                _log.Error($"Error budget of {_maxErrors:N0} reached — stopping the run.");
            _cancellation.Cancel();
        }
    }

    /// <summary>
    /// Records an error that suggests the workload cannot run against this target at all.
    /// The run is abandoned only once these accumulate, so one unlucky operation does not
    /// end an otherwise healthy run.
    /// </summary>
    public void ReportFatalCandidate(string operationName, string detail)
    {
        var count = Interlocked.Increment(ref _fatalCandidates);

        if (count == 1)
            _log.Warning($"'{operationName}' failed in a way that suggests a schema or permission mismatch: {detail}");

        if (count >= FatalErrorThreshold)
            ReportFatal($"{count} operations failed with missing-object or permission errors");
    }

    public void ReportFatal(string detail)
    {
        if (TrySetReason(StopReason.FatalError))
        {
            Interlocked.Exchange(ref _fatalDetail, detail);
            _log.Error($"Stopping: {detail}.");
        }

        _cancellation.Cancel();
    }

    public void RequestStop(StopReason reason)
    {
        TrySetReason(reason);
        _cancellation.Cancel();
    }

    /// <summary>Records the reason if none has been set yet; the first reason wins.</summary>
    public bool TrySetReason(StopReason reason) =>
        Interlocked.CompareExchange(ref _stopReason, (int)reason, (int)StopReason.None) == (int)StopReason.None;
}
