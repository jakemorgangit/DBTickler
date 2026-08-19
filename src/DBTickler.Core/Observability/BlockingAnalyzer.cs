namespace DBTickler.Core.Observability;

/// <summary>
/// Turns a flat list of sessions into blocking trees rooted at the head blockers.
///
/// This is the view that makes blocking legible: the DMV tells you session 62 is blocked by
/// session 57, but what an operator needs is the one session at the head of the chain that
/// everything else is queued behind.
/// </summary>
public static class BlockingAnalyzer
{
    /// <summary>
    /// Builds the blocking forest. Roots are sessions that block at least one other session
    /// and are not themselves blocked.
    /// </summary>
    public static IReadOnlyList<BlockingNode> BuildChains(IReadOnlyList<ActiveRequest> requests)
    {
        ArgumentNullException.ThrowIfNull(requests);

        var bySession = new Dictionary<int, ActiveRequest>();
        foreach (var request in requests)
            bySession[request.SessionId] = request;

        var blockedBy = new Dictionary<int, List<ActiveRequest>>();
        foreach (var request in requests)
        {
            if (request.BlockingSessionId is not { } blocker || blocker <= 0 || blocker == request.SessionId)
                continue;

            if (!blockedBy.TryGetValue(blocker, out var children))
                blockedBy[blocker] = children = [];
            children.Add(request);
        }

        if (blockedBy.Count == 0) return [];

        var roots = new List<BlockingNode>();
        foreach (var (blockerId, _) in blockedBy)
        {
            // A head blocker either has no request row of its own (it is sleeping with an open
            // transaction) or has one that is not itself blocked.
            var isBlockedItself = bySession.TryGetValue(blockerId, out var blockerRequest)
                                  && blockerRequest.BlockingSessionId is > 0
                                  && blockedBy.ContainsKey(blockerRequest.BlockingSessionId.Value);

            if (isBlockedItself) continue;

            var head = blockerRequest ?? Placeholder(blockerId);
            roots.Add(BuildNode(head, blockedBy, []));
        }

        // In a pure cycle — every participant blocked by another in the same ring, which
        // SQL Server's deadlock resolver is about to break — no session qualifies as a head,
        // so the whole thing would otherwise vanish from the display at exactly the moment it
        // is most interesting. Root it at the longest waiter instead.
        if (roots.Count == 0)
        {
            var entryPoint = blockedBy.Keys
                .Select(sessionId => bySession.TryGetValue(sessionId, out var request) ? request : Placeholder(sessionId))
                .OrderByDescending(request => request.WaitTimeMs)
                .First();

            roots.Add(BuildNode(entryPoint, blockedBy, []));
        }

        return roots
            .OrderByDescending(node => node.TotalBlockedBelow)
            .ThenByDescending(node => node.MaxWaitMsBelow)
            .ToList();
    }

    private static BlockingNode BuildNode(
        ActiveRequest request,
        Dictionary<int, List<ActiveRequest>> blockedBy,
        HashSet<int> visited)
    {
        // A session records exactly one blocker, so the graph is a forest — except in the
        // instant before SQL Server's deadlock resolver fires, when it can contain a cycle.
        // Tracking visited sessions stops the walk going round the ring forever, and stopping
        // at the closing edge rather than repeating the session keeps each one counted once.
        visited.Add(request.SessionId);

        if (!blockedBy.TryGetValue(request.SessionId, out var children))
            return new BlockingNode(request, []);

        var nodes = new List<BlockingNode>(children.Count);
        foreach (var child in children.OrderByDescending(c => c.WaitTimeMs))
        {
            if (visited.Contains(child.SessionId))
                continue;

            nodes.Add(BuildNode(child, blockedBy, visited));
        }

        return new BlockingNode(request, nodes);
    }

    /// <summary>
    /// Stands in for a head blocker that has no row in sys.dm_exec_requests — a session that
    /// holds locks while idle. These are the most common head blockers in practice and would
    /// otherwise be missing from the tree entirely.
    /// </summary>
    private static ActiveRequest Placeholder(int sessionId) => new()
    {
        SessionId = sessionId,
        BlockingSessionId = null,
        Status = "sleeping",
        Command = "(idle with open transaction)",
        WaitType = null,
        WaitTimeMs = 0,
        WaitResource = null,
        CpuTimeMs = 0,
        LogicalReads = 0,
        ElapsedMs = 0,
        LoginName = null,
        HostName = null,
        ProgramName = null,
        DatabaseName = null,
        StatementText = null,
    };
}
