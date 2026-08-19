using DBTickler.Core.Configuration;
using DBTickler.Core.Metrics;

namespace DBTickler.Core.Workloads;

/// <summary>
/// The concrete set of operations a run will draw from, assembled from the profile's DML mix
/// and what probing found on the target. Building this up front means an impossible
/// combination (writes requested, no writable table) is reported once, before the run,
/// instead of as thousands of identical errors during it.
/// </summary>
public sealed class WorkloadPlan
{
    private readonly WorkloadOperation[] _reads;
    private readonly WorkloadOperation[] _inserts;
    private readonly WorkloadOperation[] _updates;
    private readonly WorkloadOperation[] _deletes;
    private readonly WorkloadOperation[] _chaos;

    private readonly int _readThreshold;
    private readonly int _insertThreshold;
    private readonly int _updateThreshold;
    private readonly int _deleteThreshold;
    private readonly int _chaosIntensity;

    private WorkloadPlan(
        WorkloadProfile profile,
        SchemaCapabilities schema,
        WorkloadOperation[] reads,
        WorkloadOperation[] inserts,
        WorkloadOperation[] updates,
        WorkloadOperation[] deletes,
        WorkloadOperation[] chaos,
        ValidationResult diagnostics)
    {
        Profile = profile;
        Schema = schema;
        _reads = reads;
        _inserts = inserts;
        _updates = updates;
        _deletes = deletes;
        _chaos = chaos;
        Diagnostics = diagnostics;

        _readThreshold = profile.ReadPercent;
        _insertThreshold = _readThreshold + profile.InsertPercent;
        _updateThreshold = _insertThreshold + profile.UpdatePercent;
        _deleteThreshold = _updateThreshold + profile.DeletePercent;
        _chaosIntensity = profile.ChaosMode && chaos.Length > 0 ? profile.ChaosIntensityPercent : 0;
    }

    /// <summary>The normalized profile the plan was built from (safe mode already folded in).</summary>
    public WorkloadProfile Profile { get; }

    public SchemaCapabilities Schema { get; }

    /// <summary>Warnings and errors discovered while assembling the plan.</summary>
    public ValidationResult Diagnostics { get; }

    public bool IsRunnable => _reads.Length > 0 || _inserts.Length > 0 || _updates.Length > 0 || _deletes.Length > 0;

    public IReadOnlyList<WorkloadOperation> AllOperations =>
        [.. _reads, .. _inserts, .. _updates, .. _deletes, .. _chaos];

    public IReadOnlyList<WorkloadOperation> ChaosCatalogue => _chaos;

    /// <summary>
    /// Draws the next operation for a virtual user. The user index is passed through because
    /// some operations deliberately behave differently per user — the deadlock trap reverses
    /// its lock order on alternating users so that a cycle actually forms.
    /// </summary>
    public WorkloadOperation Next(Random random, int userIndex)
    {
        if (_chaosIntensity > 0 && random.Next(100) < _chaosIntensity)
            return _chaos[random.Next(_chaos.Length)];

        var roll = random.Next(100);

        if (roll < _readThreshold)
            return Pick(_reads, random) ?? FallbackRead(random);
        if (roll < _insertThreshold)
            return Pick(_inserts, random) ?? FallbackRead(random);
        if (roll < _updateThreshold)
            return Pick(_updates, random) ?? FallbackRead(random);
        if (roll < _deleteThreshold)
            return Pick(_deletes, random) ?? FallbackRead(random);

        return FallbackRead(random);
    }

    private static WorkloadOperation? Pick(WorkloadOperation[] pool, Random random) =>
        pool.Length == 0 ? null : pool[random.Next(pool.Length)];

    private WorkloadOperation FallbackRead(Random random) =>
        Pick(_reads, random) ?? throw new InvalidOperationException(
            "The workload plan has no runnable operations. Check Diagnostics before starting a run.");

    public static WorkloadPlan Build(WorkloadProfile profile, SchemaCapabilities schema)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(schema);

        var normalized = profile.Normalized();
        var diagnostics = new ValidationResult();

        var reads = new List<WorkloadOperation>();
        if (schema.HasAdventureWorks)
            reads.AddRange(AdventureWorksOperations.Reads());
        else if (schema.AdventureWorksTablesFound.Count > 0)
            diagnostics.AddWarning(
                $"Only {schema.AdventureWorksTablesFound.Count} of {SchemaProbe.AdventureWorksTables.Length} " +
                "AdventureWorks tables were found, so the sample workload was skipped in favour of a generic one.");

        if (schema.HasLoadGenTable)
            reads.AddRange(LoadGenOperations.Reads());

        reads.AddRange(GenericTableOperations.Reads(schema.Tables));

        if (reads.Count == 0)
        {
            reads.Add(GenericTableOperations.MetadataScan());
            diagnostics.AddWarning(
                "No user tables were found to read from; falling back to system catalogue scans. " +
                "Run Setup to create dbo.LoadGen for a more representative workload.");
        }

        var inserts = new List<WorkloadOperation>();
        var updates = new List<WorkloadOperation>();
        var deletes = new List<WorkloadOperation>();

        if (normalized.TotalWritePercent > 0)
        {
            if (schema.CanGenerateWrites)
            {
                inserts.Add(LoadGenOperations.Insert());
                updates.Add(LoadGenOperations.Update());
                deletes.Add(LoadGenOperations.Delete());
            }
            else
            {
                diagnostics.AddError(
                    "The profile asks for writes but dbo.LoadGen does not exist on the target. " +
                    "Run Setup first, or enable Safe mode to run reads only.");
            }
        }

        var chaos = normalized.ChaosMode
            ? ChaosOperations.Build(schema, normalized)
            : [];

        if (normalized.ChaosMode && chaos.Count == 0)
            diagnostics.AddWarning("Chaos mode is on but no chaos operation applies to this target.");

        if (profile.SafeMode && profile.TotalWritePercent > 0)
            diagnostics.AddWarning(
                $"Safe mode redirected {profile.TotalWritePercent}% of the mix from writes to reads.");

        return new WorkloadPlan(
            normalized,
            schema,
            [.. reads],
            [.. inserts],
            [.. updates],
            [.. deletes],
            [.. chaos],
            diagnostics);
    }

    /// <summary>One-line description of what the run will do, for the log and the report header.</summary>
    public string Describe()
    {
        var parts = new List<string>
        {
            $"{_reads.Length} read op(s)",
        };
        if (_inserts.Length + _updates.Length + _deletes.Length > 0)
            parts.Add($"{_inserts.Length + _updates.Length + _deletes.Length} write op(s)");
        if (_chaos.Length > 0)
            parts.Add($"{_chaos.Length} chaos op(s) at {_chaosIntensity}% intensity");

        return $"{string.Join(", ", parts)} from {Schema.DescribeWorkloadSource()}";
    }
}
