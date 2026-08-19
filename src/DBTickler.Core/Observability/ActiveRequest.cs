namespace DBTickler.Core.Observability;

/// <summary>A session currently doing something on the server, as seen through the DMVs.</summary>
public sealed record ActiveRequest
{
    public required int SessionId { get; init; }
    public required int? BlockingSessionId { get; init; }
    public required string Status { get; init; }
    public required string Command { get; init; }
    public required string? WaitType { get; init; }
    public required long WaitTimeMs { get; init; }
    public required string? WaitResource { get; init; }
    public required long CpuTimeMs { get; init; }
    public required long LogicalReads { get; init; }
    public required long ElapsedMs { get; init; }
    public required string? LoginName { get; init; }
    public required string? HostName { get; init; }
    public required string? ProgramName { get; init; }
    public required string? DatabaseName { get; init; }
    public required string? StatementText { get; init; }

    /// <summary>True when this session was started by DBTickler rather than by someone else.</summary>
    public bool IsOurs => string.Equals(ProgramName, Configuration.ConnectionProfile.ApplicationName, StringComparison.Ordinal);

    public bool IsBlocked => BlockingSessionId is > 0;

    /// <summary>Wait type with the benign "no wait" placeholders normalised away.</summary>
    public string WaitDescription => string.IsNullOrEmpty(WaitType) ? "—" : WaitType;

    public string ShortStatement =>
        string.IsNullOrWhiteSpace(StatementText)
            ? "(no statement text)"
            : StatementText.Trim().ReplaceLineEndings(" ") is var text && text.Length > 160
                ? text[..160] + "…"
                : text;
}

/// <summary>
/// A node in a blocking tree: one session, with everything waiting on it beneath.
/// </summary>
public sealed record BlockingNode(ActiveRequest Request, IReadOnlyList<BlockingNode> Blocked)
{
    /// <summary>Total sessions stuck behind this one, at any depth.</summary>
    public int TotalBlockedBelow => Blocked.Count + Blocked.Sum(child => child.TotalBlockedBelow);

    /// <summary>Longest wait anywhere in this subtree — how bad the chain has become.</summary>
    public long MaxWaitMsBelow =>
        Blocked.Count == 0 ? 0 : Blocked.Max(child => Math.Max(child.Request.WaitTimeMs, child.MaxWaitMsBelow));
}
