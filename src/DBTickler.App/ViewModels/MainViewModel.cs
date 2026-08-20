using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using DBTickler.App.Services;
using DBTickler.Core.Configuration;
using DBTickler.Core.Data;
using DBTickler.Core.Engine;
using DBTickler.Core.Logging;
using DBTickler.Core.Observability;
using DBTickler.Core.Safety;
using DBTickler.Core.Workloads;

namespace DBTickler.App.ViewModels;

/// <summary>
/// Coordinates the whole window: connection details, workload settings, the run itself, and
/// the observability panels.
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly IUserInteraction _interaction;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly RunLog _log = new(capacity: 10_000);
    private readonly SessionStore _sessionStore = new();
    private readonly DispatcherTimer _metricsTimer;
    private readonly DispatcherTimer _monitorTimer;

    private LoadEngine? _engine;
    private CancellationTokenSource? _runCancellation;
    private CancellationTokenSource? _blockingCancellation;
    private RunReport? _lastReport;
    private SchemaCapabilities _schema = SchemaCapabilities.Empty;

    private string _server = "localhost";
    private string _database = "AdventureWorks2022";
    private bool _useIntegratedSecurity = true;
    private string _username = "";
    private string _password = "";
    private bool _encrypt = true;
    private bool _trustServerCertificate = true;

    private string _statusText = "Idle. Test the connection to begin.";
    private string _serverDescription = "Not connected.";
    private string _schemaDescription = "Target not probed yet.";
    private string _selectedPreset = "readonly";
    private string? _selectedSessionName;
    private bool _isDarkTheme = true;
    private bool _isBlocking;
    private RunState _runState = RunState.Idle;

    public MainViewModel(IUserInteraction interaction)
    {
        _interaction = interaction;

        Log = new LogViewModel(_log);
        Metrics = new MetricsPanelViewModel();
        Monitor = new MonitorViewModel();
        Workload = new WorkloadSettingsViewModel();

        Monitor.DeadlockCaptured += report =>
            _log.Warning($"Deadlock captured: {report.Victims.Count()} victim(s) among {report.Processes.Count} session(s).");

        TestConnectionCommand = new AsyncRelayCommand(TestConnectionAsync, () => !IsRunning, ReportError);
        SetupCommand = new AsyncRelayCommand(SetupAsync, () => !IsRunning, ReportError);
        TeardownCommand = new AsyncRelayCommand(TeardownAsync, () => !IsRunning, ReportError);
        StartCommand = new AsyncRelayCommand(StartAsync, () => !IsRunning, ReportError);
        StopCommand = new AsyncRelayCommand(StopAsync, () => IsRunning, ReportError);
        ForceBlockingCommand = new AsyncRelayCommand(ForceBlockingAsync, onError: ReportError);
        ForceDeadlockCommand = new AsyncRelayCommand(ForceDeadlockAsync, onError: ReportError);
        KillSessionsCommand = new AsyncRelayCommand(KillSessionsAsync, onError: ReportError);
        ExportReportCommand = new RelayCommand(ExportReport, () => _lastReport is not null);
        SaveSessionCommand = new RelayCommand(SaveSession);
        DeleteSessionCommand = new RelayCommand(DeleteSession, () => _selectedSessionName is not null);
        LoadSessionFileCommand = new RelayCommand(LoadSessionFile);
        ClearLogCommand = new RelayCommand(() => Log.Clear());
        ApplyPresetCommand = new RelayCommand(() => Workload.ApplyPreset(SelectedPreset));
        LaunchAnotherInstanceCommand = new RelayCommand(LaunchAnotherInstance);

        _metricsTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(250) };
        _metricsTimer.Tick += (_, _) => RefreshMetrics();

        _monitorTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _monitorTimer.Tick += async (_, _) => await PollMonitorsAsync().ConfigureAwait(true);

        Workload.ApplyPreset("readonly");
        RefreshSavedSessions();
    }

    public LogViewModel Log { get; }
    public MetricsPanelViewModel Metrics { get; }
    public MonitorViewModel Monitor { get; }
    public WorkloadSettingsViewModel Workload { get; }

    public ObservableCollection<string> SavedSessions { get; } = [];
    public ObservableCollection<string> PlannedOperations { get; } = [];

    public AsyncRelayCommand TestConnectionCommand { get; }
    public AsyncRelayCommand SetupCommand { get; }
    public AsyncRelayCommand TeardownCommand { get; }
    public AsyncRelayCommand StartCommand { get; }
    public AsyncRelayCommand StopCommand { get; }
    public AsyncRelayCommand ForceBlockingCommand { get; }
    public AsyncRelayCommand ForceDeadlockCommand { get; }
    public AsyncRelayCommand KillSessionsCommand { get; }
    public RelayCommand ExportReportCommand { get; }
    public RelayCommand SaveSessionCommand { get; }
    public RelayCommand DeleteSessionCommand { get; }
    public RelayCommand LoadSessionFileCommand { get; }
    public RelayCommand ClearLogCommand { get; }
    public RelayCommand ApplyPresetCommand { get; }
    public RelayCommand LaunchAnotherInstanceCommand { get; }

    public string Server
    {
        get => _server;
        set { if (SetProperty(ref _server, value)) RefreshRiskHint(); }
    }

    public string Database
    {
        get => _database;
        set { if (SetProperty(ref _database, value)) RefreshRiskHint(); }
    }

    public bool UseIntegratedSecurity
    {
        get => _useIntegratedSecurity;
        set
        {
            if (!SetProperty(ref _useIntegratedSecurity, value)) return;
            OnPropertyChanged(nameof(UsesSqlLogin));
        }
    }

    public bool UsesSqlLogin => !_useIntegratedSecurity;

    public string Username
    {
        get => _username;
        set => SetProperty(ref _username, value);
    }

    /// <summary>
    /// Set from the view's PasswordBox. WPF deliberately does not expose PasswordBox.Password
    /// as a bindable property, so the view pushes it here on change.
    /// </summary>
    public string Password
    {
        get => _password;
        set => SetProperty(ref _password, value);
    }

    public bool Encrypt
    {
        get => _encrypt;
        set => SetProperty(ref _encrypt, value);
    }

    public bool TrustServerCertificate
    {
        get => _trustServerCertificate;
        set => SetProperty(ref _trustServerCertificate, value);
    }

    public string SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (!SetProperty(ref _selectedPreset, value)) return;
            Workload.ApplyPreset(value);
        }
    }

    public string? SelectedSessionName
    {
        get => _selectedSessionName;
        set
        {
            if (!SetProperty(ref _selectedSessionName, value)) return;
            DeleteSessionCommand.RaiseCanExecuteChanged();
            if (value is not null) LoadSession(value);
        }
    }

    public bool IsDarkTheme
    {
        get => _isDarkTheme;
        set
        {
            if (!SetProperty(ref _isDarkTheme, value)) return;
            OnPropertyChanged(nameof(ThemeButtonText));
            ThemeChanged?.Invoke(value);
        }
    }

    public string ThemeButtonText => _isDarkTheme ? "🌙 Dark" : "☀ Light";

    public event Action<bool>? ThemeChanged;

    public RunState RunState
    {
        get => _runState;
        private set
        {
            if (!SetProperty(ref _runState, value)) return;
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(IsIdle));
            OnPropertyChanged(nameof(RunStateText));
            StartCommand.RaiseCanExecuteChanged();
            StopCommand.RaiseCanExecuteChanged();
            TestConnectionCommand.RaiseCanExecuteChanged();
            SetupCommand.RaiseCanExecuteChanged();
            TeardownCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsRunning => _runState is RunState.Preparing or RunState.RampingUp or RunState.Running or RunState.Stopping;
    public bool IsIdle => !IsRunning;

    public string RunStateText => _runState switch
    {
        RunState.Preparing => "Preparing…",
        RunState.RampingUp => "Ramping up…",
        RunState.Running => "Running",
        RunState.Stopping => "Stopping…",
        RunState.Finished => "Finished",
        RunState.Failed => "Failed",
        _ => "Idle",
    };

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string ServerDescription
    {
        get => _serverDescription;
        private set => SetProperty(ref _serverDescription, value);
    }

    public string SchemaDescription
    {
        get => _schemaDescription;
        private set => SetProperty(ref _schemaDescription, value);
    }

    private string _riskHint = "";
    public string RiskHint
    {
        get => _riskHint;
        private set => SetProperty(ref _riskHint, value);
    }

    public bool IsBlocking
    {
        get => _isBlocking;
        private set
        {
            if (!SetProperty(ref _isBlocking, value)) return;
            OnPropertyChanged(nameof(BlockingButtonText));
        }
    }

    public string BlockingButtonText => _isBlocking ? "Release lock" : "Force blocking";

    /// <summary>Seconds the manual blocking action holds its lock.</summary>
    public int BlockingSeconds { get; set; } = 15;

    private void RefreshRiskHint()
    {
        var risk = ProductionGuard.AssessName(_server, _database);
        RiskHint = risk.Signals.Count == 0 ? "" : risk.Describe();
    }

    private ConnectionProfile BuildConnectionProfile() => new()
    {
        Server = Server,
        Database = Database,
        IntegratedSecurity = UseIntegratedSecurity,
        Username = Username,
        Password = Password,
        Encrypt = Encrypt,
        TrustServerCertificate = TrustServerCertificate,
    };

    private string BuildConnectionString(int? poolSize = null) =>
        BuildConnectionProfile().BuildConnectionString(poolSize);

    private async Task TestConnectionAsync()
    {
        var profile = BuildConnectionProfile();
        var validation = profile.Validate();
        if (!validation.IsValid)
        {
            _interaction.ShowError("Connection settings", validation.FormatErrors());
            return;
        }

        StatusText = "Testing connection…";
        _log.Info($"Connecting to {profile.Describe()}…");

        try
        {
            var probe = new SchemaProbe(profile.BuildConnectionString());
            _schema = await probe.ProbeAsync().ConfigureAwait(true);

            ServerDescription = _schema.Server.Describe();
            SchemaDescription = DescribeSchema(_schema);
            StatusText = "Connected.";
            _log.Success($"Connected to {_schema.Server.Describe()}.");
            _log.Info(SchemaDescription);

            RefreshPlanPreview();
        }
        catch (Exception exception)
        {
            ServerDescription = "Not connected.";
            StatusText = "Connection failed.";
            _log.Error($"Connection failed: {exception.Message}");
            _interaction.ShowError("Connection failed", exception.Message);
        }
    }

    private static string DescribeSchema(SchemaCapabilities schema)
    {
        var parts = new List<string>
        {
            schema.HasLoadGenTable
                ? $"dbo.LoadGen present ({schema.LoadGenRowCount:N0} rows)"
                : "dbo.LoadGen missing — run Setup to enable writes",
        };

        if (schema.HasAdventureWorks)
            parts.Add("AdventureWorks sample schema detected");
        else if (schema.Tables.Count > 0)
            parts.Add($"{schema.Tables.Count} user table(s) discovered for reads");

        return string.Join(" · ", parts);
    }

    private void RefreshPlanPreview()
    {
        PlannedOperations.Clear();

        try
        {
            var plan = WorkloadPlan.Build(Workload.ToProfile(), _schema);
            foreach (var operation in plan.AllOperations)
                PlannedOperations.Add($"{operation.Name} — {operation.Explanation}");
        }
        catch (Exception)
        {
            // The preview is a convenience; a profile that cannot form a plan is reported
            // properly when the operator presses Start.
        }
    }

    private async Task SetupAsync()
    {
        var connectionString = BuildConnectionString();
        var rowsText = _interaction.PromptForText(
            "Set up database objects",
            "DBTickler will create dbo.LoadGen and fill it so reads have data to hit.\n\n" +
            "How many rows should it contain?",
            "20000");

        if (rowsText is null) return;
        if (!int.TryParse(rowsText, out var rows) || rows < 0)
        {
            _interaction.ShowError("Setup", $"'{rowsText}' is not a valid row count.");
            return;
        }

        StatusText = "Setting up database objects…";
        var setup = new DatabaseSetup(connectionString, _log);
        await setup.SetupAsync(rows).ConfigureAwait(true);

        _schema = await new SchemaProbe(connectionString).ProbeAsync().ConfigureAwait(true);
        SchemaDescription = DescribeSchema(_schema);
        StatusText = "Setup complete.";
        RefreshPlanPreview();
    }

    private async Task TeardownAsync()
    {
        if (!_interaction.Confirm(
                "Remove database objects",
                $"This drops dbo.LoadGen from {Database} on {Server}, discarding every row in it.\n\n" +
                "Nothing else is touched.",
                "Drop the table"))
        {
            return;
        }

        var connectionString = BuildConnectionString();
        StatusText = "Dropping dbo.LoadGen…";
        await new DatabaseSetup(connectionString, _log).TeardownAsync().ConfigureAwait(true);

        _schema = await new SchemaProbe(connectionString).ProbeAsync().ConfigureAwait(true);
        SchemaDescription = DescribeSchema(_schema);
        StatusText = "dbo.LoadGen removed.";
    }

    private async Task StartAsync()
    {
        var connectionProfile = BuildConnectionProfile();
        var connectionValidation = connectionProfile.Validate();
        if (!connectionValidation.IsValid)
        {
            _interaction.ShowError("Connection settings", connectionValidation.FormatErrors());
            return;
        }

        var workloadProfile = Workload.ToProfile();
        var workloadValidation = workloadProfile.Validate();
        if (!workloadValidation.IsValid)
        {
            _interaction.ShowError("Workload settings", workloadValidation.FormatErrors());
            return;
        }

        // Room for every user plus the monitoring connections. Without this the users queue
        // on the ADO.NET pool and the run measures the client, not the server.
        var connectionString = connectionProfile.BuildConnectionString(workloadProfile.VirtualUsers + 16);

        if (workloadProfile.WillWrite || workloadProfile.ChaosMode)
        {
            var risk = await ProductionGuard
                .AssessAsync(connectionString, connectionProfile.Server, connectionProfile.Database)
                .ConfigureAwait(true);

            if (risk.RequiresConfirmation)
            {
                var proceed = _interaction.Confirm(
                    "This may be a production system",
                    $"DBTickler found signs that {connectionProfile.Describe()} is not a lab instance:\n\n" +
                    risk.Describe() +
                    "\n\nThis run will modify data and can cause blocking and deadlocks.",
                    "Run it anyway");

                if (!proceed)
                {
                    _log.Warning("Run cancelled at the production-safety prompt.");
                    return;
                }
            }
        }

        RunState = RunState.Preparing;
        StatusText = "Probing target…";

        try
        {
            _schema = await new SchemaProbe(connectionString).ProbeAsync().ConfigureAwait(true);
            ServerDescription = _schema.Server.Describe();
            SchemaDescription = DescribeSchema(_schema);

            var plan = WorkloadPlan.Build(workloadProfile, _schema);
            if (!plan.Diagnostics.IsValid)
            {
                RunState = RunState.Idle;
                StatusText = "Cannot start.";
                _interaction.ShowError("Cannot start the run", plan.Diagnostics.FormatErrors());
                return;
            }

            RefreshPlanPreview();
            Metrics.Reset();

            await Monitor.AttachAsync(connectionString).ConfigureAwait(true);

            _runCancellation = new CancellationTokenSource();
            var factory = new SqlClientSessionFactory(connectionString, connectionProfile.Describe());
            _engine = new LoadEngine(factory, _log);
            _engine.StateChanged += OnEngineStateChanged;

            _metricsTimer.Start();
            _monitorTimer.Start();

            _lastReport = null;
            ExportReportCommand.RaiseCanExecuteChanged();

            var report = await _engine.RunAsync(plan, _runCancellation.Token).ConfigureAwait(true);

            _lastReport = report;
            ExportReportCommand.RaiseCanExecuteChanged();
            StatusText = $"Finished — {report.TotalOperations:N0} operations, p95 {report.Latency.P95:F1} ms.";
        }
        catch (Exception exception)
        {
            RunState = RunState.Failed;
            StatusText = "Run failed.";
            _log.Error($"Run failed: {exception.Message}");
            _interaction.ShowError("Run failed", exception.Message);
        }
        finally
        {
            _metricsTimer.Stop();
            _monitorTimer.Stop();
            RefreshMetrics();
            await PollMonitorsAsync().ConfigureAwait(true);

            if (_engine is not null)
                _engine.StateChanged -= OnEngineStateChanged;

            _runCancellation?.Dispose();
            _runCancellation = null;

            if (RunState is not RunState.Failed)
                RunState = RunState.Finished;
        }
    }

    /// <summary>
    /// The engine raises this from whichever thread happened to finish the work — a
    /// thread-pool thread once its awaits stop capturing the UI context. Setting RunState
    /// re-evaluates commands, and WPF's CanExecuteChanged plumbing may only be touched from
    /// the dispatcher thread, so the hop is mandatory rather than defensive.
    /// </summary>
    private void OnEngineStateChanged(RunState state) => OnUiThread(() => RunState = state);

    private void OnUiThread(Action action)
    {
        if (_dispatcher.CheckAccess())
            action();
        else
            _dispatcher.BeginInvoke(action);
    }

    private async Task StopAsync()
    {
        if (_engine is null) return;

        StatusText = "Stopping…";
        _log.Info("Stop requested.");
        _engine.RequestStop();

        // Cancelling gets the client to let go; killing the sessions server-side is what
        // stops a statement that is already executing, such as a long WAITFOR inside a chaos
        // operation.
        try
        {
            var monitor = new ServerMonitor(BuildConnectionString());
            var killed = await monitor.KillOwnSessionsAsync().ConfigureAwait(true);
            if (killed > 0)
                _log.Info($"Terminated {killed} DBTickler session(s) on the server.");
        }
        catch (Exception exception)
        {
            _log.Warning($"Could not terminate sessions on the server: {exception.Message}");
        }
    }

    private async Task ForceBlockingAsync()
    {
        if (IsBlocking)
        {
            await _blockingCancellation!.CancelAsync().ConfigureAwait(true);
            return;
        }

        if (!_schema.HasLoadGenTable)
        {
            _interaction.ShowError(
                "Force blocking",
                "dbo.LoadGen does not exist on the target yet. Run Setup first — the blocking " +
                "demonstration locks a row in that table rather than one of your own.");
            return;
        }

        _blockingCancellation = new CancellationTokenSource();
        IsBlocking = true;

        try
        {
            var actions = new ManualActions(BuildConnectionString(), _log);
            await actions
                .ForceBlockingAsync(TimeSpan.FromSeconds(BlockingSeconds), _blockingCancellation.Token)
                .ConfigureAwait(true);
        }
        finally
        {
            IsBlocking = false;
            _blockingCancellation.Dispose();
            _blockingCancellation = null;
        }
    }

    private async Task ForceDeadlockAsync()
    {
        if (!_schema.HasLoadGenTable)
        {
            _interaction.ShowError(
                "Create deadlock",
                "dbo.LoadGen does not exist on the target yet. Run Setup first — the deadlock " +
                "is produced between two rows in that table.");
            return;
        }

        var actions = new ManualActions(BuildConnectionString(), _log);
        await actions.ForceDeadlockAsync().ConfigureAwait(true);

        // The graph reaches system_health a moment after the deadlock resolves.
        await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(true);
        await PollMonitorsAsync().ConfigureAwait(true);
    }

    private async Task KillSessionsAsync()
    {
        var monitor = new ServerMonitor(BuildConnectionString());
        var killed = await monitor.KillOwnSessionsAsync().ConfigureAwait(true);

        _log.Info(killed == 0
            ? "No DBTickler sessions were connected."
            : $"Terminated {killed} DBTickler session(s).");
    }

    private void RefreshMetrics()
    {
        if (_engine is null) return;
        Metrics.Update(_engine.Metrics.Snapshot());
    }

    private async Task PollMonitorsAsync()
    {
        try
        {
            await Monitor.PollAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _log.Debug($"Monitor poll failed: {exception.Message}");
        }
    }

    private void ExportReport()
    {
        if (_lastReport is null) return;

        var suggested = $"dbtickler-{_lastReport.StartedAt:yyyyMMdd-HHmmss}";
        var path = _interaction.PromptForSavePath(
            "Export run report",
            "JSON report (*.json)|*.json|CSV summary (*.csv)|*.csv|Throughput series (*.csv)|*.csv",
            suggested + ".json");

        if (path is null) return;

        try
        {
            var content = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".csv" => _lastReport.SummaryToCsv(),
                _ => _lastReport.ToJson(),
            };

            File.WriteAllText(path, content);
            _log.Success($"Report written to {path}");
        }
        catch (Exception exception)
        {
            _interaction.ShowError("Export failed", exception.Message);
        }
    }

    private void RefreshSavedSessions()
    {
        SavedSessions.Clear();
        foreach (var session in _sessionStore.LoadAll())
            SavedSessions.Add(session.SessionName);
    }

    private void SaveSession()
    {
        var name = _interaction.PromptForText("Save session", "Name for this configuration:",
            SelectedSessionName ?? "My session");
        if (string.IsNullOrWhiteSpace(name)) return;

        try
        {
            var config = SessionConfig.From(name, BuildConnectionProfile(), Workload.ToProfile());
            _sessionStore.Save(config);
            RefreshSavedSessions();
            SetProperty(ref _selectedSessionName, name, nameof(SelectedSessionName));
            _log.Success($"Session '{name}' saved.");
        }
        catch (Exception exception)
        {
            _interaction.ShowError("Save failed", exception.Message);
        }
    }

    private void LoadSession(string name)
    {
        try
        {
            var config = _sessionStore.LoadAll().FirstOrDefault(session => session.SessionName == name);
            if (config is null) return;

            ApplySession(config);
            _log.Info($"Session '{name}' loaded.");
        }
        catch (Exception exception)
        {
            _interaction.ShowError("Load failed", exception.Message);
        }
    }

    private void LoadSessionFile()
    {
        var path = _interaction.PromptForOpenPath(
            "Load session", "Session files (*.json)|*.json", _sessionStore.Directory);
        if (path is null) return;

        try
        {
            ApplySession(_sessionStore.Load(path));
            _log.Info($"Session loaded from {path}.");
        }
        catch (Exception exception)
        {
            _interaction.ShowError("Load failed", exception.Message);
        }
    }

    private void ApplySession(SessionConfig config)
    {
        var connection = config.ToConnectionProfile();
        Server = connection.Server;
        Database = connection.Database;
        UseIntegratedSecurity = connection.IntegratedSecurity;
        Username = connection.Username;
        Password = connection.Password;
        Encrypt = connection.Encrypt;
        TrustServerCertificate = connection.TrustServerCertificate;

        Workload.LoadFrom(config.ToWorkloadProfile());
        PasswordRestored?.Invoke(connection.Password);
    }

    /// <summary>Lets the view push a loaded password back into its PasswordBox.</summary>
    public event Action<string>? PasswordRestored;

    private void DeleteSession()
    {
        if (SelectedSessionName is not { } name) return;

        if (!_interaction.Confirm("Delete session", $"Delete the saved session '{name}'?", "Delete"))
            return;

        try
        {
            _sessionStore.Delete(name);
            RefreshSavedSessions();
            SetProperty(ref _selectedSessionName, null, nameof(SelectedSessionName));
        }
        catch (Exception exception)
        {
            _interaction.ShowError("Delete failed", exception.Message);
        }
    }

    /// <summary>
    /// Starts a second copy of the application, for driving several databases or servers at
    /// once. Each instance keeps its own configuration and metrics.
    /// </summary>
    private void LaunchAnotherInstance()
    {
        try
        {
            var executable = Environment.ProcessPath;
            if (string.IsNullOrEmpty(executable))
            {
                _interaction.ShowError("New window", "Could not determine the path to the running executable.");
                return;
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(executable)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception exception)
        {
            _interaction.ShowError("New window", exception.Message);
        }
    }

    private void ReportError(Exception exception)
    {
        _log.Error(exception.Message);
        _interaction.ShowError("Something went wrong", exception.Message);
    }

    public void Dispose()
    {
        _metricsTimer.Stop();
        _monitorTimer.Stop();
        _runCancellation?.Cancel();
        _runCancellation?.Dispose();
        _blockingCancellation?.Cancel();
        _blockingCancellation?.Dispose();
        Log.Dispose();
    }
}
