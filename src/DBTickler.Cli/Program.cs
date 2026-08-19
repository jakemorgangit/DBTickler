using System.Globalization;
using DBTickler.Cli;
using DBTickler.Core.Configuration;
using DBTickler.Core.Data;
using DBTickler.Core.Engine;
using DBTickler.Core.Logging;
using DBTickler.Core.Observability;
using DBTickler.Core.Safety;
using DBTickler.Core.Workloads;

var exitCode = await RunAsync(args).ConfigureAwait(false);
return exitCode;

static async Task<int> RunAsync(string[] args)
{
    CommandLine commandLine;
    try
    {
        commandLine = CommandLine.Parse(args);
    }
    catch (ArgumentException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return ExitCodes.BadUsage;
    }

    if (commandLine.Command is "help" or "--help" or "-h")
    {
        Help.Print();
        return ExitCodes.Ok;
    }

    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        // First Ctrl+C asks for a clean stop; the second is left to the runtime so a wedged
        // run can still be killed.
        if (!cancellation.IsCancellationRequested)
        {
            eventArgs.Cancel = true;
            Console.Error.WriteLine();
            Console.Error.WriteLine("Stopping… (press Ctrl+C again to abort immediately)");
            cancellation.Cancel();
        }
    };

    try
    {
        return commandLine.Command switch
        {
            "run" => await Commands.RunAsync(commandLine, cancellation.Token).ConfigureAwait(false),
            "setup" => await Commands.SetupAsync(commandLine, cancellation.Token).ConfigureAwait(false),
            "teardown" => await Commands.TeardownAsync(commandLine, cancellation.Token).ConfigureAwait(false),
            "probe" => await Commands.ProbeAsync(commandLine, cancellation.Token).ConfigureAwait(false),
            "sessions" => await Commands.SessionsAsync(commandLine, cancellation.Token).ConfigureAwait(false),
            "deadlocks" => await Commands.DeadlocksAsync(commandLine, cancellation.Token).ConfigureAwait(false),
            "kill" => await Commands.KillAsync(commandLine, cancellation.Token).ConfigureAwait(false),
            _ => UnknownCommand(commandLine.Command),
        };
    }
    catch (ArgumentException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return ExitCodes.BadUsage;
    }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine("Cancelled.");
        return ExitCodes.Cancelled;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"Error: {exception.Message}");
        return ExitCodes.Failed;
    }
}

static int UnknownCommand(string command)
{
    Console.Error.WriteLine($"Unknown command '{command}'. Run 'dbtickler help' to see the available commands.");
    return ExitCodes.BadUsage;
}

namespace DBTickler.Cli
{
    internal static class ExitCodes
    {
        public const int Ok = 0;
        public const int Failed = 1;
        public const int BadUsage = 2;
        public const int Cancelled = 3;

        /// <summary>The run completed but breached a threshold the caller asked to enforce.</summary>
        public const int ThresholdBreached = 4;
    }

    internal static class Help
    {
        public static void Print()
        {
            Console.WriteLine("""
                DBTickler — SQL Server workload generator and learning tool

                USAGE
                  dbtickler <command> [options]

                COMMANDS
                  run         Generate load against a database
                  setup       Create or upgrade dbo.LoadGen on the target
                  teardown    Drop dbo.LoadGen
                  probe       Report what the target looks like and what workload would run
                  sessions    Show active sessions and blocking chains, once or continuously
                  deadlocks   Print deadlock graphs recorded in system_health
                  kill        Terminate sessions this tool left behind
                  help        Show this text

                CONNECTION (all commands)
                  --server <name>          Server or instance            (default: localhost)
                  --database <name>        Database                      (default: AdventureWorks2022)
                  --user <name>            SQL login; omit for Windows authentication
                  --password <secret>      Password. Prefer DBTICKLER_PASSWORD in scripts
                  --no-encrypt             Disable connection encryption
                  --verify-certificate     Require a trusted server certificate

                RUN OPTIONS
                  --profile <name>         readonly | oltp | write-heavy | chaos  (default: readonly)
                  --users <n>              Concurrent virtual users
                  --duration <seconds>     Run length; 0 runs until Ctrl+C
                  --ramp <seconds>         Spread user starts over this window
                  --batch <rows>           Rows per write operation
                  --payload <bytes>        Generated bytes per written row
                  --mix <r,i,u,d>          DML percentages, must total 100
                  --think <ms>             Mean pause between operations per user
                  --timeout <seconds>      Per-statement timeout
                  --max-errors <n>         Global error budget; 0 disables
                  --seed <n>               Fixed random seed, for a reproducible run
                  --safe                   Force reads only
                  --unsafe                 Allow writes (required by write-bearing profiles)
                  --chaos                  Enable chaos operations
                  --chaos-intensity <pct>  Share of operations drawn from the chaos catalogue
                  --setup                  Run setup before the workload
                  --yes                    Skip the production-safety prompt
                  --json <path>            Write the full report as JSON
                  --csv <path>             Write the per-category summary as CSV
                  --series-csv <path>      Write the per-second throughput series as CSV
                  --quiet                  Only print the final summary
                  --verbose                Log every operation
                  --max-error-rate <pct>   Exit non-zero if the error rate exceeds this
                  --max-p95 <ms>           Exit non-zero if p95 latency exceeds this

                EXAMPLES
                  dbtickler probe --server localhost --database AdventureWorks2022
                  dbtickler run --profile oltp --unsafe --users 32 --duration 120 --json run.json
                  dbtickler run --profile chaos --unsafe --duration 60 --seed 42
                  dbtickler sessions --watch
                  dbtickler run --profile oltp --unsafe --duration 60 --max-p95 250

                Exit codes: 0 ok, 1 error, 2 bad usage, 3 cancelled, 4 threshold breached.
                """);
        }
    }

    internal static class ConsoleWriter
    {
        public static void Write(LogEntry entry)
        {
            var previous = Console.ForegroundColor;
            Console.ForegroundColor = entry.Level switch
            {
                LogLevel.Error => ConsoleColor.Red,
                LogLevel.Warning => ConsoleColor.Yellow,
                LogLevel.Success => ConsoleColor.Green,
                LogLevel.Debug or LogLevel.Trace => ConsoleColor.DarkGray,
                _ => previous,
            };

            Console.Error.WriteLine(entry.Format());
            Console.ForegroundColor = previous;
        }

        public static void Rule(string title)
        {
            Console.WriteLine();
            Console.WriteLine(title);
            Console.WriteLine(new string('─', Math.Min(78, Math.Max(20, title.Length))));
        }
    }

    internal static class Options
    {
        public static ConnectionProfile ReadConnection(CommandLine commandLine)
        {
            var user = commandLine.String("user");
            var password = commandLine.String("password")
                           ?? Environment.GetEnvironmentVariable("DBTICKLER_PASSWORD")
                           ?? "";

            var profile = new ConnectionProfile
            {
                Server = commandLine.String("server", "localhost")!,
                Database = commandLine.String("database", "AdventureWorks2022")!,
                IntegratedSecurity = string.IsNullOrEmpty(user),
                Username = user ?? "",
                Password = password,
                Encrypt = !commandLine.Flag("no-encrypt"),
                TrustServerCertificate = !commandLine.Flag("verify-certificate"),
            };

            var validation = profile.Validate();
            if (!validation.IsValid)
                throw new ArgumentException(validation.FormatErrors());

            return profile;
        }

        public static WorkloadProfile ReadWorkload(CommandLine commandLine)
        {
            var presetName = commandLine.String("profile", "readonly")!;
            if (!WorkloadProfile.Presets.TryGetValue(presetName, out var factory))
            {
                throw new ArgumentException(
                    $"Unknown profile '{presetName}'. Available: {string.Join(", ", WorkloadProfile.Presets.Keys)}.");
            }

            var workload = factory();

            workload.VirtualUsers = commandLine.Int("users", workload.VirtualUsers);
            workload.DurationSeconds = commandLine.Int("duration", workload.DurationSeconds);
            workload.RampUpSeconds = commandLine.Int("ramp", workload.RampUpSeconds);
            workload.BatchRows = commandLine.Int("batch", workload.BatchRows);
            workload.PayloadBytes = commandLine.Int("payload", workload.PayloadBytes);
            workload.ThinkTimeMs = commandLine.Int("think", workload.ThinkTimeMs);
            workload.CommandTimeoutSeconds = commandLine.Int("timeout", workload.CommandTimeoutSeconds);
            workload.MaxErrors = commandLine.Int("max-errors", workload.MaxErrors);
            workload.RandomSeed = commandLine.NullableInt("seed");

            if (commandLine.String("mix") is { } mix)
                ApplyMix(workload, mix);

            // Writing is opt-in on the command line even when the chosen preset writes, so
            // a command copied from a README or a chat message cannot start modifying data
            // just because of the preset it names.
            var wantsSafe = commandLine.Flag("safe");
            var wantsUnsafe = commandLine.Flag("unsafe");

            if (wantsSafe)
            {
                workload.SafeMode = true;
            }
            else if (wantsUnsafe)
            {
                workload.SafeMode = false;
            }
            else if (!workload.SafeMode)
            {
                workload.SafeMode = true;
                Console.Error.WriteLine(
                    $"The '{presetName}' profile includes writes, but --unsafe was not given, " +
                    "so this run is read-only. Add --unsafe to allow writes.");
            }

            if (commandLine.Flag("chaos")) workload.ChaosMode = true;
            workload.ChaosIntensityPercent = commandLine.Int("chaos-intensity", workload.ChaosIntensityPercent);

            // Ramp-up defaults come from the preset and can exceed a short explicit duration.
            if (workload.DurationSeconds > 0 && workload.RampUpSeconds > workload.DurationSeconds)
                workload.RampUpSeconds = workload.DurationSeconds;

            var validation = workload.Validate();
            if (!validation.IsValid)
                throw new ArgumentException(validation.FormatErrors());

            return workload;
        }

        private static void ApplyMix(WorkloadProfile workload, string mix)
        {
            var parts = mix.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 4)
                throw new ArgumentException("--mix expects four comma-separated percentages: reads,inserts,updates,deletes.");

            var values = new int[4];
            for (var i = 0; i < 4; i++)
            {
                if (!int.TryParse(parts[i], CultureInfo.InvariantCulture, out values[i]))
                    throw new ArgumentException($"--mix contains '{parts[i]}', which is not a number.");
            }

            workload.ReadPercent = values[0];
            workload.InsertPercent = values[1];
            workload.UpdatePercent = values[2];
            workload.DeletePercent = values[3];
        }

        public static void ReportUnknown(CommandLine commandLine)
        {
            foreach (var unknown in commandLine.UnknownOptions)
                Console.Error.WriteLine($"Warning: unrecognised option --{unknown}");
        }
    }
}
