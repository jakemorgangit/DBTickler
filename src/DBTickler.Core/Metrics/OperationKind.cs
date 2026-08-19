namespace DBTickler.Core.Metrics;

/// <summary>
/// Category an operation is counted under. Chaos operations are counted separately from
/// the ordinary mix so a chaos run's throughput figures stay comparable to a normal one.
/// </summary>
public enum OperationKind
{
    Read = 0,
    Insert = 1,
    Update = 2,
    Delete = 3,
    ChaosRead = 4,
    ChaosWrite = 5,
}

public static class OperationKindExtensions
{
    public static readonly OperationKind[] All =
        [OperationKind.Read, OperationKind.Insert, OperationKind.Update,
         OperationKind.Delete, OperationKind.ChaosRead, OperationKind.ChaosWrite];

    public static bool IsWrite(this OperationKind kind) =>
        kind is OperationKind.Insert or OperationKind.Update
             or OperationKind.Delete or OperationKind.ChaosWrite;

    public static bool IsChaos(this OperationKind kind) =>
        kind is OperationKind.ChaosRead or OperationKind.ChaosWrite;

    public static string DisplayName(this OperationKind kind) => kind switch
    {
        OperationKind.Read => "Read",
        OperationKind.Insert => "Insert",
        OperationKind.Update => "Update",
        OperationKind.Delete => "Delete",
        OperationKind.ChaosRead => "Chaos read",
        OperationKind.ChaosWrite => "Chaos write",
        _ => kind.ToString(),
    };
}
