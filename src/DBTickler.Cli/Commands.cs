using DBTickler.Core.Configuration;
using DBTickler.Core.Data;
using DBTickler.Core.Engine;
using DBTickler.Core.Logging;
using DBTickler.Core.Observability;
using DBTickler.Core.Safety;
using DBTickler.Core.Workloads;

namespace DBTickler.Cli;

internal static class Commands
{
    public static async Task<int> RunAsync(CommandLine commandLine, CancellationToken cancellationToken)
    {
        var connection = Options.ReadConnection(commandLine);
        var workload = Options.ReadWorkload(commandLine);
        var quiet = commandLine.Flag("quiet");
        var verbose = commandLine.Flag("verbose");
        var runSetup = commandLine.Flag("setup");
        var assumeYes = commandLine.Flag("yes");
        var jsonPath = commandLine.String("json");
        var csvPath = commandLine.String("csv");
        var seriesPath = commandLine.String("series-csv");
        var maxErrorRate = commandLine.String("max-error-rate");
        var maxP95 = commandLine.String("max-p95");
        Options.ReportUnknown(commandLine);

        var log = new RunLog { MinimumLevel = verbose ? LogLevel.Trace : quiet ? LogLevel.Warning : LogLevel.Info };
        log.EntryWritten += ConsoleWriter.Write;

        // The pool must have room for every user plus the monitoring connections, or users
        // queue on the pool and the run measures ADO.NET rather than SQL Server.
        var connectionString = connection.BuildConnectionString(maxPoolSize: workload.VirtualUsers + 16);

        if (workload.WillWrite || workload.ChaosMode)
        {
            var risk = await ProductionGuard
                .AssessAsync(connectionString, connection.Server, connection.Database, cancellationToken)
                .ConfigureAwait(false);

            if (risk.RequiresConfirmation && !assumeYes)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine($"This looks like it might be a production system ({risk.Level}):");
                Console.Error.WriteLine(risk.Describe());
                Console.Error.WriteLine();
                Console.Error.WriteLine(
                    "This run will modify data and can cause blocking and deadlocks. " +
                    "Re-run with --yes to proceed, or --safe to run reads only.");
                return ExitCodes.BadUsage;
            }
        }

        if (runSetup)
        {
            var setup = new DatabaseSetup(connectionString, log);
            await setup.SetupAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        log.Info($"Probing {connection.Describe()}…");
        var probe = new SchemaProbe(connectionString);
        var schema = await probe.ProbeAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        log.Info($"Connected to {schema.Server.Describe()}; workload source: {schema.DescribeWorkloadSource()}.");

        var plan = WorkloadPlan.Build(workload, schema);
        if (!plan.Diagnostics.IsValid)
        {
            Console.Error.WriteLine(plan.Diagnostics.FormatErrors());
            return ExitCodes.BadUsage;
        }

        var deadlocks = new DeadlockMonitor(connectionString);
        await deadlocks.PrimeAsync(cancellationToken).ConfigureAwait(false);

        var waits = new WaitStatsMonitor(connectionString);
        IReadOnlyList<WaitStat> waitBaseline = [];
        try
        {
            waitBaseline = await waits.SampleAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            log.Debug($"Wait statistics unavailable: {exception.Message}");
        }

        var factory = new SqlClientSessionFactory(connectionString, connection.Describe());
        var engine = new LoadEngine(factory, log);

        using var progress = quiet ? null : StartProgress(engine, cancellationToken);

        var report = await engine.RunAsync(plan, cancellationToken).ConfigureAwait(false);
        progress?.Dispose();

        Console.WriteLine();
        Console.WriteLine(report.ToText());

        await PrintWaitsAsync(waits, waitBaseline, cancellationToken).ConfigureAwait(false);
        await PrintDeadlocksAsync(deadlocks, cancellationToken).ConfigureAwait(false);

        WriteIfRequested(jsonPath, report.ToJson(), "JSON report");
        WriteIfRequested(csvPath, report.SummaryToCsv(), "CSV summary");
        WriteIfRequested(seriesPath, report.SeriesToCsv(), "throughput series");

        return EvaluateThresholds(report, maxErrorRate, maxP95);
    }

    private static IDisposable StartProgress(LoadEngine engine, CancellationToken cancellationToken)
    {
        var timer = new Timer(_ =>
        {
            if (cancellationToken.IsCancellationRequested || !engine.IsRunning) return;

            var snapshot = engine.Metrics.Snapshot();
            Console.Error.Write(
                $"\r  {snapshot.Elapsed.TotalSeconds,6:F0}s  " +
                $"{snapshot.TotalOperations,10:N0} ops  " +
                $"{snapshot.RecentOperationsPerSecond(),8:F0}/s  " +
                $"p95 {snapshot.Latency.P95,7:F1} ms  " +
                $"{snapshot.TotalErrors,6:N0} err  " +
                $"{snapshot.ActiveUsers,4} users   ");
        }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

        return timer;
    }

    private static async Task PrintWaitsAsync(
        WaitStatsMonitor waits, IReadOnlyList<WaitStat> baseline, CancellationToken cancellationToken)
    {
        if (baseline.Count == 0) return;

        try
        {
            var current = await waits.SampleAsync(cancellationToken).ConfigureAwait(false);
            var deltas = WaitStatsMonitor.Diff(baseline, current, topN: 10);
            if (deltas.Count == 0) return;

            ConsoleWriter.Rule("Top waits during the run");
            Console.WriteLine($"{"Wait type",-38} {"Total ms",12} {"Waits",10} {"Avg ms",9}  Share");
            foreach (var delta in deltas)
            {
                Console.WriteLine(
                    $"{Truncate(delta.WaitType, 38),-38} {delta.WaitTimeMs,12:N0} {delta.WaitingTasks,10:N0} " +
                    $"{delta.AverageWaitMs,9:F1}  {delta.PercentOfTotal,6:P1}");
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Could not read wait statistics: {exception.Message}");
        }
    }

    private static async Task PrintDeadlocksAsync(DeadlockMonitor monitor, CancellationToken cancellationToken)
    {
        try
        {
            var reports = await monitor.PollAsync(cancellationToken).ConfigureAwait(false);
            if (reports.Count == 0) return;

            ConsoleWriter.Rule($"Deadlocks captured ({reports.Count})");
            foreach (var report in reports.Take(5))
            {
                Console.WriteLine(report.Explain());
                Console.WriteLine();
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Could not read deadlock graphs: {exception.Message}");
        }
    }

    private static int EvaluateThresholds(RunReport report, string? maxErrorRate, string? maxP95)
    {
        var breached = false;

        if (maxErrorRate is not null && double.TryParse(maxErrorRate, out var errorRateLimit))
        {
            var actual = report.ErrorRate * 100;
            if (actual > errorRateLimit)
            {
                Console.Error.WriteLine(
                    $"Threshold breached: error rate {actual:F2}% exceeds the limit of {errorRateLimit:F2}%.");
                breached = true;
            }
        }

        if (maxP95 is not null && double.TryParse(maxP95, out var p95Limit))
        {
            if (report.Latency.P95 > p95Limit)
            {
                Console.Error.WriteLine(
                    $"Threshold breached: p95 latency {report.Latency.P95:F1} ms exceeds the limit of {p95Limit:F1} ms.");
                breached = true;
            }
        }

        if (breached) return ExitCodes.ThresholdBreached;
        return report.StopReason == StopReason.FatalError ? ExitCodes.Failed : ExitCodes.Ok;
    }

    private static void WriteIfRequested(string? path, string content, string description)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(path, content);
        Console.Error.WriteLine($"Wrote {description} to {path}");
    }

    public static async Task<int> SetupAsync(CommandLine commandLine, CancellationToken cancellationToken)
    {
        var connection = Options.ReadConnection(commandLine);
        var rows = commandLine.Int("rows", 20_000);
        Options.ReportUnknown(commandLine);

        var log = new RunLog();
        log.EntryWritten += ConsoleWriter.Write;

        var setup = new DatabaseSetup(connection.BuildConnectionString(), log);
        await setup.SetupAsync(rows, cancellationToken).ConfigureAwait(false);
        return ExitCodes.Ok;
    }

    public static async Task<int> TeardownAsync(CommandLine commandLine, CancellationToken cancellationToken)
    {
        var connection = Options.ReadConnection(commandLine);
        var assumeYes = commandLine.Flag("yes");
        Options.ReportUnknown(commandLine);

        if (!assumeYes)
        {
            Console.Error.WriteLine($"This will drop dbo.LoadGen from {connection.Describe()}. Re-run with --yes to confirm.");
            return ExitCodes.BadUsage;
        }

        var log = new RunLog();
        log.EntryWritten += ConsoleWriter.Write;

        var setup = new DatabaseSetup(connection.BuildConnectionString(), log);
        await setup.TeardownAsync(cancellationToken).ConfigureAwait(false);
        return ExitCodes.Ok;
    }

    public static async Task<int> ProbeAsync(CommandLine commandLine, CancellationToken cancellationToken)
    {
        var connection = Options.ReadConnection(commandLine);
        var workload = Options.ReadWorkload(commandLine);
        Options.ReportUnknown(commandLine);

        var connectionString = connection.BuildConnectionString();
        var schema = await new SchemaProbe(connectionString)
            .ProbeAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        ConsoleWriter.Rule("Target");
        Console.WriteLine($"  Server        : {schema.Server.Describe()}");
        Console.WriteLine($"  Database      : {schema.Server.DatabaseName}");
        Console.WriteLine($"  Engine        : {(schema.Server.IsAzure ? "Azure SQL" : "SQL Server")} " +
                          $"(major version {schema.Server.MajorVersion})");
        Console.WriteLine($"  system_health : {(schema.Server.SupportsSystemHealthDeadlocks ? "deadlock graphs available" : "not available")}");

        ConsoleWriter.Rule("Schema");
        Console.WriteLine($"  dbo.LoadGen        : {(schema.HasLoadGenTable ? $"present, {schema.LoadGenRowCount:N0} rows" : "missing — run 'dbtickler setup'")}");
        Console.WriteLine($"  AdventureWorks     : {(schema.HasAdventureWorks ? "complete" : $"{schema.AdventureWorksTablesFound.Count}/{SchemaProbe.AdventureWorksTables.Length} tables")}");
        Console.WriteLine($"  Discovered tables  : {schema.Tables.Count}");
        foreach (var table in schema.Tables.Take(10))
            Console.WriteLine($"      {table}");

        var plan = WorkloadPlan.Build(workload, schema);

        ConsoleWriter.Rule("Planned workload");
        Console.WriteLine($"  {plan.Describe()}");
        foreach (var operation in plan.AllOperations)
            Console.WriteLine($"      {operation.Name,-32} {operation.Explanation}");

        if (plan.Diagnostics.HasWarnings)
        {
            ConsoleWriter.Rule("Warnings");
            foreach (var warning in plan.Diagnostics.Warnings)
                Console.WriteLine($"  • {warning}");
        }

        if (!plan.Diagnostics.IsValid)
        {
            ConsoleWriter.Rule("Errors");
            foreach (var error in plan.Diagnostics.Errors)
                Console.WriteLine($"  • {error}");
            return ExitCodes.Failed;
        }

        var risk = await ProductionGuard
            .AssessAsync(connectionString, connection.Server, connection.Database, cancellationToken)
            .ConfigureAwait(false);

        if (risk.Signals.Count > 0)
        {
            ConsoleWriter.Rule($"Production indicators ({risk.Level})");
            Console.WriteLine(risk.Describe());
        }

        return ExitCodes.Ok;
    }

    public static async Task<int> SessionsAsync(CommandLine commandLine, CancellationToken cancellationToken)
    {
        var connection = Options.ReadConnection(commandLine);
        var watch = commandLine.Flag("watch");
        var intervalSeconds = commandLine.Int("interval", 2);
        Options.ReportUnknown(commandLine);

        var monitor = new ServerMonitor(connection.BuildConnectionString());

        do
        {
            var requests = await monitor.GetActiveRequestsAsync(cancellationToken).ConfigureAwait(false);
            var chains = BlockingAnalyzer.BuildChains(requests);

            if (watch) Console.Clear();
            ConsoleWriter.Rule($"Active sessions ({requests.Count}) — {DateTime.Now:HH:mm:ss}");

            Console.WriteLine($"{"SPID",6} {"Status",-12} {"Wait",-24} {"Wait ms",9} {"Blocked by",11}  Statement");
            foreach (var request in requests.Take(30))
            {
                Console.WriteLine(
                    $"{request.SessionId,6} {Truncate(request.Status, 12),-12} " +
                    $"{Truncate(request.WaitDescription, 24),-24} {request.WaitTimeMs,9:N0} " +
                    $"{(request.IsBlocked ? request.BlockingSessionId!.Value.ToString() : "—"),11}  " +
                    $"{Truncate(request.ShortStatement, 60)}");
            }

            if (chains.Count > 0)
            {
                ConsoleWriter.Rule($"Blocking chains ({chains.Count})");
                foreach (var chain in chains)
                    PrintChain(chain, 0);
            }

            if (watch)
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(intervalSeconds, 1, 60)), cancellationToken)
                    .ConfigureAwait(false);
        }
        while (watch && !cancellationToken.IsCancellationRequested);

        return ExitCodes.Ok;
    }

    private static void PrintChain(BlockingNode node, int depth)
    {
        var indent = new string(' ', depth * 4);
        var arrow = depth == 0 ? "HEAD" : "└──▶";
        Console.WriteLine(
            $"  {indent}{arrow} SPID {node.Request.SessionId} " +
            $"[{node.Request.Status}] {node.Request.WaitDescription} " +
            $"{(depth == 0 ? $"blocking {node.TotalBlockedBelow} session(s)" : $"waiting {node.Request.WaitTimeMs:N0} ms")}");
        Console.WriteLine($"  {indent}     {Truncate(node.Request.ShortStatement, 100)}");

        foreach (var child in node.Blocked)
            PrintChain(child, depth + 1);
    }

    public static async Task<int> DeadlocksAsync(CommandLine commandLine, CancellationToken cancellationToken)
    {
        var connection = Options.ReadConnection(commandLine);
        var limit = commandLine.Int("limit", 10);
        var showXml = commandLine.Flag("xml");
        var onlyOurs = commandLine.Flag("mine");
        Options.ReportUnknown(commandLine);

        var monitor = new DeadlockMonitor(connection.BuildConnectionString());
        var reports = await monitor.PollAsync(cancellationToken).ConfigureAwait(false);

        var filtered = onlyOurs
            ? reports.Where(report => report.InvolvesDbTickler).ToList()
            : reports.ToList();

        if (filtered.Count == 0)
        {
            Console.WriteLine("No deadlock graphs found in the system_health ring buffer.");
            return ExitCodes.Ok;
        }

        foreach (var report in filtered.Take(limit))
        {
            ConsoleWriter.Rule($"Deadlock at {report.Timestamp?.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
            Console.WriteLine(report.Explain());

            if (showXml)
            {
                Console.WriteLine();
                Console.WriteLine(report.Xml);
            }
        }

        return ExitCodes.Ok;
    }

    public static async Task<int> KillAsync(CommandLine commandLine, CancellationToken cancellationToken)
    {
        var connection = Options.ReadConnection(commandLine);
        Options.ReportUnknown(commandLine);

        var monitor = new ServerMonitor(connection.BuildConnectionString());
        var killed = await monitor.KillOwnSessionsAsync(cancellationToken).ConfigureAwait(false);

        Console.WriteLine(killed == 0
            ? "No DBTickler sessions were connected."
            : $"Terminated {killed} DBTickler session(s).");
        return ExitCodes.Ok;
    }

    private static string Truncate(string? value, int length)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return value.Length <= length ? value : value[..(length - 1)] + "…";
    }
}
