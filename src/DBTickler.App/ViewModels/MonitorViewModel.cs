using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DBTickler.Core.Observability;

namespace DBTickler.App.ViewModels;

/// <summary>A row in the blocking tree, flattened for display with an indent level.</summary>
public sealed class BlockingRowViewModel
{
    public BlockingRowViewModel(BlockingNode node, int depth)
    {
        Depth = depth;
        SessionId = node.Request.SessionId;
        Status = node.Request.Status;
        WaitType = node.Request.WaitDescription;
        WaitTimeMs = node.Request.WaitTimeMs;
        Statement = node.Request.ShortStatement;
        Program = node.Request.ProgramName ?? "";
        BlockedCount = node.TotalBlockedBelow;
        IsHead = depth == 0;
    }

    public int Depth { get; }
    public int SessionId { get; }
    public string Status { get; }
    public string WaitType { get; }
    public long WaitTimeMs { get; }
    public string Statement { get; }
    public string Program { get; }
    public int BlockedCount { get; }
    public bool IsHead { get; }

    public double Indent => Depth * 20.0;

    public string Summary => IsHead
        ? $"SPID {SessionId} — head blocker, {BlockedCount} session(s) waiting behind it"
        : $"SPID {SessionId} — waiting {WaitTimeMs:N0} ms on {WaitType}";
}

public sealed class DeadlockRowViewModel
{
    public DeadlockRowViewModel(DeadlockReport report)
    {
        Report = report;
        Timestamp = report.Timestamp?.ToLocalTime().ToString("HH:mm:ss") ?? "—";
        Victims = string.Join(", ", report.Victims.Select(process => $"SPID {process.SessionId}"));
        Participants = report.Processes.Count;
        Explanation = report.Explain();
        Involves = report.InvolvesDbTickler ? "DBTickler" : "other traffic";
    }

    public DeadlockReport Report { get; }
    public string Timestamp { get; }
    public string Victims { get; }
    public int Participants { get; }
    public string Explanation { get; }
    public string Involves { get; }
    public string Xml => Report.Xml;
}

public sealed class WaitRowViewModel
{
    public WaitRowViewModel(WaitDelta delta)
    {
        WaitType = delta.WaitType;
        TotalMs = delta.WaitTimeMs;
        ResourceMs = delta.ResourceWaitTimeMs;
        Waits = delta.WaitingTasks;
        AverageMs = delta.AverageWaitMs;
        Share = delta.PercentOfTotal;
    }

    public string WaitType { get; }
    public long TotalMs { get; }
    public long ResourceMs { get; }
    public long Waits { get; }
    public double AverageMs { get; }
    public double Share { get; }
}

/// <summary>
/// Polls the server for what it is doing and keeps the observability tabs current. Every
/// poll is best-effort: a login without VIEW SERVER STATE simply gets an explanatory message
/// instead of the panel, and never an error that interrupts the run.
/// </summary>
public sealed class MonitorViewModel : ObservableObject
{
    private readonly SemaphoreSlim _pollGate = new(1, 1);

    private ServerMonitor? _serverMonitor;
    private WaitStatsMonitor? _waitMonitor;
    private DeadlockMonitor? _deadlockMonitor;
    private IReadOnlyList<WaitStat> _waitBaseline = [];

    private string _sessionsStatus = "Not connected.";
    private string _waitsStatus = "Not connected.";
    private string _deadlocksStatus = "Not connected.";
    private int _activeSessionCount;
    private int _blockedSessionCount;

    public ObservableCollection<BlockingRowViewModel> BlockingRows { get; } = [];
    public ObservableCollection<WaitRowViewModel> Waits { get; } = [];
    public ObservableCollection<DeadlockRowViewModel> Deadlocks { get; } = [];

    public string SessionsStatus
    {
        get => _sessionsStatus;
        private set => SetProperty(ref _sessionsStatus, value);
    }

    public string WaitsStatus
    {
        get => _waitsStatus;
        private set => SetProperty(ref _waitsStatus, value);
    }

    public string DeadlocksStatus
    {
        get => _deadlocksStatus;
        private set => SetProperty(ref _deadlocksStatus, value);
    }

    public int ActiveSessionCount
    {
        get => _activeSessionCount;
        private set => SetProperty(ref _activeSessionCount, value);
    }

    public int BlockedSessionCount
    {
        get => _blockedSessionCount;
        private set => SetProperty(ref _blockedSessionCount, value);
    }

    /// <summary>Raised when a new deadlock is captured, so the shell can draw attention to the tab.</summary>
    public event Action<DeadlockReport>? DeadlockCaptured;

    /// <summary>Points the monitors at a target and takes the baselines a run will be measured against.</summary>
    public async Task AttachAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        _serverMonitor = new ServerMonitor(connectionString);
        _waitMonitor = new WaitStatsMonitor(connectionString);
        _deadlockMonitor = new DeadlockMonitor(connectionString);

        Waits.Clear();
        Deadlocks.Clear();
        BlockingRows.Clear();

        try
        {
            _waitBaseline = await _waitMonitor.SampleAsync(cancellationToken).ConfigureAwait(true);
            WaitsStatus = "Baseline captured; waits will appear once the run generates some.";
        }
        catch (Exception exception)
        {
            _waitBaseline = [];
            WaitsStatus = $"Wait statistics unavailable — {exception.Message}";
        }

        // Deadlocks already in the ring buffer belong to whatever happened before this run,
        // so they are marked as seen rather than reported as ours.
        await _deadlockMonitor.PrimeAsync(cancellationToken).ConfigureAwait(true);
        DeadlocksStatus = "Watching system_health for deadlock graphs.";
    }

    public void Detach()
    {
        _serverMonitor = null;
        _waitMonitor = null;
        _deadlockMonitor = null;
        _waitBaseline = [];
        SessionsStatus = "Not connected.";
    }

    /// <summary>
    /// Refreshes every panel. Overlapping calls are dropped rather than queued: the timer
    /// fires on a fixed interval, and a slow server would otherwise build a backlog of polls
    /// that all arrive at once.
    /// </summary>
    public async Task PollAsync(CancellationToken cancellationToken = default)
    {
        if (_serverMonitor is null) return;
        if (!await _pollGate.WaitAsync(0, cancellationToken).ConfigureAwait(true)) return;

        try
        {
            await PollSessionsAsync(cancellationToken).ConfigureAwait(true);
            await PollWaitsAsync(cancellationToken).ConfigureAwait(true);
            await PollDeadlocksAsync(cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            _pollGate.Release();
        }
    }

    private async Task PollSessionsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var requests = await _serverMonitor!.GetActiveRequestsAsync(cancellationToken).ConfigureAwait(true);
            ActiveSessionCount = requests.Count;
            BlockedSessionCount = requests.Count(request => request.IsBlocked);

            var chains = BlockingAnalyzer.BuildChains(requests);

            BlockingRows.Clear();
            foreach (var chain in chains)
                AddChain(chain, 0);

            SessionsStatus = chains.Count == 0
                ? $"{requests.Count} active session(s), nothing blocked."
                : $"{chains.Count} blocking chain(s), {BlockedSessionCount} session(s) waiting.";
        }
        catch (Exception exception)
        {
            SessionsStatus = $"Could not read sessions — {exception.Message}";
        }
    }

    private void AddChain(BlockingNode node, int depth)
    {
        BlockingRows.Add(new BlockingRowViewModel(node, depth));
        foreach (var child in node.Blocked)
            AddChain(child, depth + 1);
    }

    private async Task PollWaitsAsync(CancellationToken cancellationToken)
    {
        if (_waitMonitor is null || _waitBaseline.Count == 0) return;

        try
        {
            var current = await _waitMonitor.SampleAsync(cancellationToken).ConfigureAwait(true);
            var deltas = WaitStatsMonitor.Diff(_waitBaseline, current);

            Waits.Clear();
            foreach (var delta in deltas)
                Waits.Add(new WaitRowViewModel(delta));

            WaitsStatus = deltas.Count == 0
                ? "No significant waits accumulated yet."
                : $"Top {deltas.Count} wait type(s) since the run started.";
        }
        catch (Exception exception)
        {
            WaitsStatus = $"Could not read wait statistics — {exception.Message}";
        }
    }

    private async Task PollDeadlocksAsync(CancellationToken cancellationToken)
    {
        if (_deadlockMonitor is null) return;

        try
        {
            var fresh = await _deadlockMonitor.PollAsync(cancellationToken).ConfigureAwait(true);
            foreach (var report in fresh)
            {
                Deadlocks.Insert(0, new DeadlockRowViewModel(report));
                DeadlockCaptured?.Invoke(report);
            }

            if (Deadlocks.Count > 0)
                DeadlocksStatus = $"{Deadlocks.Count} deadlock graph(s) captured.";
        }
        catch (Exception exception)
        {
            DeadlocksStatus = $"Could not read deadlock graphs — {exception.Message}";
        }
    }
}
