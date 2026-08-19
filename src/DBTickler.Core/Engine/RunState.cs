namespace DBTickler.Core.Engine;

public enum RunState
{
    Idle,
    Preparing,
    RampingUp,
    Running,
    Stopping,
    Finished,
    Failed,
}

/// <summary>Why a run ended. Reported in the summary so a short run is never a mystery.</summary>
public enum StopReason
{
    /// <summary>Still running.</summary>
    None,

    /// <summary>Configured duration elapsed.</summary>
    DurationReached,

    /// <summary>Operator pressed stop.</summary>
    UserRequested,

    /// <summary>Global error budget exhausted.</summary>
    ErrorBudgetExceeded,

    /// <summary>The workload could not run against this target at all.</summary>
    FatalError,

    /// <summary>Host cancelled the run (application shutting down).</summary>
    Cancelled,
}

public static class StopReasonExtensions
{
    public static string Describe(this StopReason reason) => reason switch
    {
        StopReason.DurationReached => "the configured duration elapsed",
        StopReason.UserRequested => "you stopped it",
        StopReason.ErrorBudgetExceeded => "the error budget was exhausted",
        StopReason.FatalError => "the workload cannot run against this target",
        StopReason.Cancelled => "the application shut down",
        _ => "it is still running",
    };
}
