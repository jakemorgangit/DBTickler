using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using DBTickler.Core.Data;
using DBTickler.Core.Metrics;

namespace DBTickler.App.ViewModels;

public sealed class CategoryRowViewModel
{
    public CategoryRowViewModel(OperationStats stats)
    {
        Category = stats.Kind.DisplayName();
        Operations = stats.Operations;
        Errors = stats.Errors;
        Rows = stats.Rows;
        P50 = stats.Latency.P50;
        P95 = stats.Latency.P95;
        P99 = stats.Latency.P99;
        Max = stats.Latency.Max;
    }

    public string Category { get; }
    public long Operations { get; }
    public long Errors { get; }
    public long Rows { get; }
    public double P50 { get; }
    public double P95 { get; }
    public double P99 { get; }
    public double Max { get; }
}

public sealed class ErrorRowViewModel
{
    public ErrorRowViewModel(string description, long count, double share)
    {
        Description = description;
        Count = count;
        Share = share;
    }

    public string Description { get; }
    public long Count { get; }
    public double Share { get; }
}

/// <summary>
/// The live figures. Latency percentiles are the headline here — a load generator that
/// reports only operations per second, as v1 did, tells you the server accepted work but
/// nothing about whether it kept up.
/// </summary>
public sealed class MetricsPanelViewModel : ObservableObject
{
    private const int MaxChartPoints = 300;

    private MetricsSnapshot _snapshot = MetricsSnapshot.Empty;
    private IReadOnlyList<double> _throughputSeries = [];
    private IReadOnlyList<double> _latencySeries = [];
    private IReadOnlyList<double> _errorSeries = [];

    public MetricsSnapshot Snapshot => _snapshot;

    public ObservableCollection<CategoryRowViewModel> Categories { get; } = [];
    public ObservableCollection<ErrorRowViewModel> Errors { get; } = [];

    public IReadOnlyList<double> ThroughputSeries
    {
        get => _throughputSeries;
        private set => SetProperty(ref _throughputSeries, value);
    }

    public IReadOnlyList<double> LatencySeries
    {
        get => _latencySeries;
        private set => SetProperty(ref _latencySeries, value);
    }

    public IReadOnlyList<double> ErrorSeries
    {
        get => _errorSeries;
        private set => SetProperty(ref _errorSeries, value);
    }

    public string Elapsed => $"{_snapshot.Elapsed.TotalSeconds:F0} s";
    public string Operations => _snapshot.TotalOperations.ToString("N0");
    public string ErrorCount => _snapshot.TotalErrors.ToString("N0");
    public string Rows => _snapshot.TotalRows.ToString("N0");
    public string Throughput => $"{_snapshot.RecentOperationsPerSecond():N0}/s";
    public string AverageThroughput => $"{_snapshot.OperationsPerSecond:N0}/s average";
    public string ActiveUsers => _snapshot.ActiveUsers.ToString("N0");
    public string ErrorRate => _snapshot.ErrorRate.ToString("P2");

    public string LatencyP50 => Format(_snapshot.Latency.P50);
    public string LatencyP95 => Format(_snapshot.Latency.P95);
    public string LatencyP99 => Format(_snapshot.Latency.P99);
    public string LatencyMax => Format(_snapshot.Latency.Max);

    public string DeadlockVictims => _snapshot.DeadlockVictims.ToString("N0");
    public string Timeouts => _snapshot.Timeouts.ToString("N0");

    /// <summary>True once anything has gone wrong, so the view can draw attention to it.</summary>
    public bool HasErrors => _snapshot.TotalErrors > 0;

    public void Update(MetricsSnapshot snapshot)
    {
        _snapshot = snapshot;

        // Charts keep a trailing window rather than the whole run, so a long run does not
        // compress into an unreadable smear.
        var series = snapshot.Series.Count > MaxChartPoints
            ? snapshot.Series.Skip(snapshot.Series.Count - MaxChartPoints).ToList()
            : snapshot.Series.ToList();

        ThroughputSeries = series.Select(sample => (double)sample.Operations).ToList();
        LatencySeries = series.Select(sample => sample.MeanLatencyMs).ToList();
        ErrorSeries = series.Select(sample => (double)sample.Errors).ToList();

        SyncCategories(snapshot);
        SyncErrors(snapshot);

        OnPropertyChanged(nameof(Elapsed));
        OnPropertyChanged(nameof(Operations));
        OnPropertyChanged(nameof(ErrorCount));
        OnPropertyChanged(nameof(Rows));
        OnPropertyChanged(nameof(Throughput));
        OnPropertyChanged(nameof(AverageThroughput));
        OnPropertyChanged(nameof(ActiveUsers));
        OnPropertyChanged(nameof(ErrorRate));
        OnPropertyChanged(nameof(LatencyP50));
        OnPropertyChanged(nameof(LatencyP95));
        OnPropertyChanged(nameof(LatencyP99));
        OnPropertyChanged(nameof(LatencyMax));
        OnPropertyChanged(nameof(DeadlockVictims));
        OnPropertyChanged(nameof(Timeouts));
        OnPropertyChanged(nameof(HasErrors));
        OnPropertyChanged(nameof(Snapshot));
    }

    private void SyncCategories(MetricsSnapshot snapshot)
    {
        Categories.Clear();
        foreach (var stats in snapshot.ByKind)
            Categories.Add(new CategoryRowViewModel(stats));
    }

    private void SyncErrors(MetricsSnapshot snapshot)
    {
        Errors.Clear();
        if (snapshot.TotalErrors == 0) return;

        foreach (var (kind, count) in snapshot.ErrorsByKind.OrderByDescending(pair => pair.Value))
        {
            Errors.Add(new ErrorRowViewModel(
                SqlErrorClassifier.DescribeKind(kind),
                count,
                (double)count / snapshot.TotalErrors));
        }
    }

    public void Reset() => Update(MetricsSnapshot.Empty);

    private static string Format(double milliseconds) => milliseconds switch
    {
        <= 0 => "—",
        < 10 => $"{milliseconds:F2} ms",
        < 1000 => $"{milliseconds:F1} ms",
        _ => $"{milliseconds / 1000:F2} s",
    };
}
