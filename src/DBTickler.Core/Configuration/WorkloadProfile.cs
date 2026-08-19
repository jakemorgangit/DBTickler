namespace DBTickler.Core.Configuration;

/// <summary>
/// The shape of the load to generate. Every field is a quantity the operator can reason
/// about directly: <see cref="VirtualUsers"/> really is the number of concurrent sessions,
/// <see cref="BatchRows"/> really is the number of rows a write touches.
/// </summary>
public sealed class WorkloadProfile
{
    /// <summary>
    /// Number of concurrent simulated sessions. Each virtual user runs exactly one
    /// operation at a time, so this is the true concurrency the server sees.
    /// </summary>
    public int VirtualUsers { get; set; } = 16;

    /// <summary>Rows touched per write operation.</summary>
    public int BatchRows { get; set; } = 50;

    /// <summary>Bytes of generated payload per inserted/updated row.</summary>
    public int PayloadBytes { get; set; } = 2048;

    /// <summary>Total run length. Zero means run until stopped.</summary>
    public int DurationSeconds { get; set; } = 60;

    /// <summary>
    /// Seconds over which virtual users are started. Starting every user at once produces a
    /// thundering herd whose latency says more about connection-pool warm-up than about the
    /// server, so the default staggers them.
    /// </summary>
    public int RampUpSeconds { get; set; } = 5;

    public int ReadPercent { get; set; } = 70;
    public int InsertPercent { get; set; } = 12;
    public int UpdatePercent { get; set; } = 12;
    public int DeletePercent { get; set; } = 6;

    /// <summary>Mean pause between operations for a given user.</summary>
    public int ThinkTimeMs { get; set; } = 0;

    /// <summary>
    /// Draw think time from an exponential distribution around <see cref="ThinkTimeMs"/>
    /// instead of using it as a fixed pause. Exponential gaps produce Poisson arrivals, which
    /// is what real user populations look like and what makes queueing behaviour show up.
    /// </summary>
    public bool ThinkTimeJitter { get; set; } = true;

    /// <summary>Per-statement timeout.</summary>
    public int CommandTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Total errors across all users before the run aborts. Zero disables the budget.
    /// This is a global count: in v1 the equivalent setting was applied per worker, so the
    /// real threshold was silently multiplied by the thread count.
    /// </summary>
    public int MaxErrors { get; set; } = 500;

    /// <summary>Blocks every write path in the engine, not just in the UI.</summary>
    public bool SafeMode { get; set; } = true;

    public bool ChaosMode { get; set; }
    public bool ChaosBadQueries { get; set; } = true;
    public bool ChaosConcurrency { get; set; } = true;
    public bool ChaosResourceBurners { get; set; } = true;

    /// <summary>
    /// Share of operations drawn from the chaos catalogue when chaos mode is on. Below 100
    /// the run stays a recognisable workload with chaos mixed through it, which is usually
    /// what you want when validating that monitoring notices the difference.
    /// </summary>
    public int ChaosIntensityPercent { get; set; } = 25;

    /// <summary>
    /// Seed for all random choices. A fixed seed makes a run reproducible: the same
    /// operations in the same order per user. Null seeds from the clock.
    /// </summary>
    public int? RandomSeed { get; set; }

    /// <summary>Cap on rows pulled back per read, to keep the client from becoming the bottleneck.</summary>
    public int MaxRowsPerRead { get; set; } = 5000;

    public int TotalWritePercent => InsertPercent + UpdatePercent + DeletePercent;

    /// <summary>True when the profile can issue statements that modify data.</summary>
    public bool WillWrite => !SafeMode && TotalWritePercent > 0;

    public ValidationResult Validate()
    {
        var result = new ValidationResult();

        if (VirtualUsers is < 1 or > 512)
            result.AddError("Virtual users must be between 1 and 512.");
        if (BatchRows is < 1 or > 100_000)
            result.AddError("Batch rows must be between 1 and 100,000.");
        if (PayloadBytes is < 0 or > 8 * 1024 * 1024)
            result.AddError("Payload size must be between 0 and 8 MB per row.");
        if (DurationSeconds is < 0 or > 86_400)
            result.AddError("Duration must be between 0 (unlimited) and 86,400 seconds.");
        if (RampUpSeconds < 0 || RampUpSeconds > Math.Max(1, DurationSeconds == 0 ? 3600 : DurationSeconds))
            result.AddError("Ramp-up must be between 0 and the run duration.");
        if (ThinkTimeMs is < 0 or > 600_000)
            result.AddError("Think time must be between 0 and 600,000 ms.");
        if (CommandTimeoutSeconds is < 1 or > 3600)
            result.AddError("Command timeout must be between 1 and 3600 seconds.");
        if (MaxErrors < 0)
            result.AddError("Max errors cannot be negative.");
        if (ChaosIntensityPercent is < 0 or > 100)
            result.AddError("Chaos intensity must be between 0 and 100 percent.");
        if (MaxRowsPerRead is < 1 or > 10_000_000)
            result.AddError("Max rows per read must be between 1 and 10,000,000.");

        foreach (var (name, value) in new[]
                 {
                     ("Read", ReadPercent), ("Insert", InsertPercent),
                     ("Update", UpdatePercent), ("Delete", DeletePercent),
                 })
        {
            if (value is < 0 or > 100)
                result.AddError($"{name} percentage must be between 0 and 100.");
        }

        var total = ReadPercent + TotalWritePercent;
        if (total != 100)
            result.AddError($"DML mix must total 100% (currently {total}%).");

        if (SafeMode && TotalWritePercent > 0)
            result.AddWarning("Safe mode is on: the write share of the mix will be executed as reads.");

        if (ChaosMode && !ChaosBadQueries && !ChaosConcurrency && !ChaosResourceBurners)
            result.AddWarning("Chaos mode is on but no chaos category is selected; the run will behave normally.");

        if (!SafeMode && VirtualUsers > 128)
            result.AddWarning($"{VirtualUsers} writing users is a lot of concurrency — expect heavy blocking.");

        var payloadBudgetMb = (long)PayloadBytes * BatchRows / (1024 * 1024);
        if (payloadBudgetMb > 64)
            result.AddWarning($"Each write op ships roughly {payloadBudgetMb} MB; the network may become the bottleneck before the server does.");

        return result;
    }

    /// <summary>
    /// Applies safe mode by folding the write share into reads, so the engine never has to
    /// re-check the flag on a hot path and a loaded session file cannot smuggle writes past it.
    /// </summary>
    public WorkloadProfile Normalized()
    {
        var copy = Clone();
        if (copy.SafeMode)
        {
            copy.ReadPercent = 100;
            copy.InsertPercent = 0;
            copy.UpdatePercent = 0;
            copy.DeletePercent = 0;
        }
        return copy;
    }

    public WorkloadProfile Clone() => (WorkloadProfile)MemberwiseClone();

    /// <summary>A steady read-mostly OLTP mix; the safe default for a first run.</summary>
    public static WorkloadProfile Oltp() => new()
    {
        VirtualUsers = 16, BatchRows = 50, PayloadBytes = 2048, DurationSeconds = 60,
        ReadPercent = 70, InsertPercent = 12, UpdatePercent = 12, DeletePercent = 6,
        ThinkTimeMs = 25, SafeMode = false,
    };

    /// <summary>Reads only — safe to point at anything you are allowed to query.</summary>
    public static WorkloadProfile ReadOnly() => new()
    {
        VirtualUsers = 16, DurationSeconds = 60,
        ReadPercent = 100, InsertPercent = 0, UpdatePercent = 0, DeletePercent = 0,
        ThinkTimeMs = 0, SafeMode = true,
    };

    /// <summary>Write-heavy, larger batches: log flush, lock escalation, page-split territory.</summary>
    public static WorkloadProfile WriteHeavy() => new()
    {
        VirtualUsers = 32, BatchRows = 500, PayloadBytes = 4096, DurationSeconds = 120,
        ReadPercent = 20, InsertPercent = 45, UpdatePercent = 25, DeletePercent = 10,
        ThinkTimeMs = 0, SafeMode = false,
    };

    /// <summary>Everything on, intensity high. For monitoring and alert validation.</summary>
    public static WorkloadProfile Chaos() => new()
    {
        VirtualUsers = 24, BatchRows = 100, PayloadBytes = 2048, DurationSeconds = 120,
        ReadPercent = 50, InsertPercent = 20, UpdatePercent = 20, DeletePercent = 10,
        ThinkTimeMs = 0, SafeMode = false,
        ChaosMode = true, ChaosIntensityPercent = 60,
    };

    public static IReadOnlyDictionary<string, Func<WorkloadProfile>> Presets { get; } =
        new Dictionary<string, Func<WorkloadProfile>>(StringComparer.OrdinalIgnoreCase)
        {
            ["readonly"] = ReadOnly,
            ["oltp"] = Oltp,
            ["write-heavy"] = WriteHeavy,
            ["chaos"] = Chaos,
        };
}
