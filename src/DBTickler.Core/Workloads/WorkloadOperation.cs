using DBTickler.Core.Configuration;
using DBTickler.Core.Data;
using DBTickler.Core.Metrics;

namespace DBTickler.Core.Workloads;

/// <summary>
/// Per-virtual-user state handed to an operation when it builds its statement. Each user
/// owns its own <see cref="Random"/>: sharing one instance across concurrent workers, as v1
/// did, is a data race that can leave the generator returning a constant.
/// </summary>
public sealed class OperationContext
{
    public required Random Random { get; init; }
    public required PayloadGenerator Payload { get; init; }
    public required WorkloadProfile Profile { get; init; }
    public required SchemaCapabilities Schema { get; init; }

    /// <summary>Zero-based index of the virtual user, for operations that partition work by user.</summary>
    public required int UserIndex { get; init; }

    public int Timeout => Profile.CommandTimeoutSeconds;
    public int MaxRows => Profile.MaxRowsPerRead;
}

/// <summary>One named unit of work the engine can execute.</summary>
public sealed class WorkloadOperation
{
    public required string Name { get; init; }
    public required OperationKind Kind { get; init; }
    public required Func<OperationContext, SqlRequest> Build { get; init; }

    /// <summary>
    /// Short description of what the operation is meant to provoke, shown in the UI so the
    /// tool teaches rather than just generating traffic.
    /// </summary>
    public string Explanation { get; init; } = "";

    public override string ToString() => Name;
}
