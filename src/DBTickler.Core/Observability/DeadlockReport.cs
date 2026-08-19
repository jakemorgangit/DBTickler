using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace DBTickler.Core.Observability;

public sealed record DeadlockProcess
{
    public required string Id { get; init; }
    public required int SessionId { get; init; }
    public bool IsVictim { get; init; }
    public string? LoginName { get; init; }
    public string? HostName { get; init; }
    public string? ClientApplication { get; init; }
    public string? LockMode { get; init; }
    public string? TransactionName { get; init; }
    public string? WaitResource { get; init; }
    public int Priority { get; init; }
    public long LogUsed { get; init; }
    public string? InputBuffer { get; init; }

    public string ShortInput =>
        string.IsNullOrWhiteSpace(InputBuffer)
            ? "(no statement captured)"
            : InputBuffer.Trim().ReplaceLineEndings(" ") is var text && text.Length > 200
                ? text[..200] + "…"
                : text;
}

public sealed record DeadlockOwner(string ProcessId, string? Mode);

public sealed record DeadlockResource
{
    /// <summary>keylock, pagelock, objectlock, ridlock, exchangeEvent, and so on.</summary>
    public required string Kind { get; init; }
    public string? ObjectName { get; init; }
    public string? IndexName { get; init; }
    public string? Mode { get; init; }
    public IReadOnlyList<DeadlockOwner> Owners { get; init; } = [];
    public IReadOnlyList<DeadlockOwner> Waiters { get; init; } = [];

    public string Describe()
    {
        var target = ObjectName ?? "(unknown object)";
        if (!string.IsNullOrEmpty(IndexName))
            target += $".{IndexName}";
        return $"{Kind} on {target}";
    }
}

/// <summary>
/// One deadlock, parsed from the graph SQL Server recorded. v1 could trigger a deadlock but
/// never showed you one; the graph is where the lesson actually is.
/// </summary>
public sealed record DeadlockReport
{
    public required DateTimeOffset? Timestamp { get; init; }
    public required IReadOnlyList<DeadlockProcess> Processes { get; init; }
    public required IReadOnlyList<DeadlockResource> Resources { get; init; }

    /// <summary>The original graph, so it can be saved and opened in SSMS.</summary>
    public required string Xml { get; init; }

    public IEnumerable<DeadlockProcess> Victims => Processes.Where(process => process.IsVictim);
    public IEnumerable<DeadlockProcess> Survivors => Processes.Where(process => !process.IsVictim);

    /// <summary>True when DBTickler was involved, as opposed to a deadlock from other traffic.</summary>
    public bool InvolvesDbTickler => Processes.Any(process =>
        string.Equals(process.ClientApplication, Configuration.ConnectionProfile.ApplicationName, StringComparison.Ordinal));

    /// <summary>
    /// Stable key for de-duplication. The ring buffer keeps a rolling window, so the same
    /// deadlock is returned on every poll until it ages out.
    /// </summary>
    public string Fingerprint =>
        $"{Timestamp?.UtcDateTime.Ticks ?? 0}:{string.Join(',', Processes.Select(process => process.SessionId).Order())}";

    /// <summary>
    /// Plain-English narrative of the cycle. This is the feature that turns a deadlock graph
    /// from something you have to already understand into something that teaches you.
    /// </summary>
    public string Explain()
    {
        var builder = new StringBuilder();
        var victims = Victims.ToList();

        builder.Append(CultureInfo.InvariantCulture, $"{Processes.Count} sessions deadlocked");
        if (Timestamp is { } when)
            builder.Append(CultureInfo.InvariantCulture, $" at {when.ToLocalTime():HH:mm:ss}");
        builder.AppendLine(".");

        foreach (var resource in Resources)
        {
            foreach (var waiter in resource.Waiters)
            {
                var waitingProcess = Processes.FirstOrDefault(process => process.Id == waiter.ProcessId);
                var holders = resource.Owners
                    .Select(owner => Processes.FirstOrDefault(process => process.Id == owner.ProcessId))
                    .Where(process => process is not null && process.Id != waiter.ProcessId)
                    .Select(process => $"session {process!.SessionId}")
                    .Distinct()
                    .ToList();

                if (waitingProcess is null || holders.Count == 0)
                    continue;

                builder.AppendLine(
                    $"  • Session {waitingProcess.SessionId} wanted a {waiter.Mode ?? "?"} lock on " +
                    $"{resource.Describe()}, held by {string.Join(" and ", holders)}.");
            }
        }

        if (victims.Count > 0)
        {
            var names = string.Join(", ", victims.Select(victim => $"session {victim.SessionId}"));
            builder.AppendLine(
                $"  → SQL Server rolled back {names} as the victim, because it had done the least work " +
                "to undo. The other sessions completed normally.");
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// Extracts every deadlock graph in a chunk of extended-event XML. Written as a pure
    /// function over a string so it can be tested against captured graphs without a server.
    /// </summary>
    public static IReadOnlyList<DeadlockReport> ParseAll(string eventXml)
    {
        if (string.IsNullOrWhiteSpace(eventXml))
            return [];

        XDocument document;
        try
        {
            document = XDocument.Parse(eventXml);
        }
        catch (System.Xml.XmlException)
        {
            return [];
        }

        var reports = new List<DeadlockReport>();

        // The graph sits at a different depth depending on the server version and on whether
        // the XML came from the ring buffer or from a file target, so it is located by name
        // anywhere in the document rather than by a fixed path.
        foreach (var deadlock in document.Descendants("deadlock"))
        {
            var timestamp = ReadTimestamp(deadlock);
            var victimIds = deadlock
                .Descendants("victimProcess")
                .Select(element => (string?)element.Attribute("id"))
                .Where(id => id is not null)
                .Select(id => id!)
                .ToHashSet(StringComparer.Ordinal);

            var processes = deadlock
                .Descendants("process")
                .Select(element => ReadProcess(element, victimIds))
                .ToList();

            var resources = ReadResources(deadlock);

            reports.Add(new DeadlockReport
            {
                Timestamp = timestamp,
                Processes = processes,
                Resources = resources,
                Xml = deadlock.ToString(),
            });
        }

        return reports;
    }

    private static DateTimeOffset? ReadTimestamp(XElement deadlock)
    {
        // The event wrapper carries the timestamp; when only the graph is present, fall back
        // to when the victim's transaction started.
        var eventElement = deadlock.Ancestors("event").FirstOrDefault();
        var raw = (string?)eventElement?.Attribute("timestamp")
                  ?? (string?)deadlock.Descendants("process").FirstOrDefault()?.Attribute("lasttranstarted");

        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
    }

    private static DeadlockProcess ReadProcess(XElement element, HashSet<string> victimIds)
    {
        var id = (string?)element.Attribute("id") ?? "";
        return new DeadlockProcess
        {
            Id = id,
            SessionId = ParseInt(element.Attribute("spid")) ?? 0,
            IsVictim = victimIds.Contains(id),
            LoginName = (string?)element.Attribute("loginname"),
            HostName = (string?)element.Attribute("hostname"),
            ClientApplication = (string?)element.Attribute("clientapp"),
            LockMode = (string?)element.Attribute("lockMode"),
            TransactionName = (string?)element.Attribute("transactionname"),
            WaitResource = (string?)element.Attribute("waitresource"),
            Priority = ParseInt(element.Attribute("priority")) ?? 0,
            LogUsed = ParseLong(element.Attribute("logused")) ?? 0,
            InputBuffer = element.Element("inputbuf")?.Value?.Trim(),
        };
    }

    private static List<DeadlockResource> ReadResources(XElement deadlock)
    {
        var resourceList = deadlock.Element("resource-list");
        if (resourceList is null) return [];

        var resources = new List<DeadlockResource>();
        foreach (var element in resourceList.Elements())
        {
            resources.Add(new DeadlockResource
            {
                Kind = element.Name.LocalName,
                ObjectName = (string?)element.Attribute("objectname"),
                IndexName = (string?)element.Attribute("indexname"),
                Mode = (string?)element.Attribute("mode"),
                Owners = ReadParticipants(element.Element("owner-list"), "owner"),
                Waiters = ReadParticipants(element.Element("waiter-list"), "waiter"),
            });
        }

        return resources;
    }

    private static List<DeadlockOwner> ReadParticipants(XElement? list, string elementName)
    {
        if (list is null) return [];

        return list.Elements(elementName)
            .Select(element => new DeadlockOwner(
                (string?)element.Attribute("id") ?? "",
                (string?)element.Attribute("mode")))
            .ToList();
    }

    private static int? ParseInt(XAttribute? attribute) =>
        int.TryParse((string?)attribute, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static long? ParseLong(XAttribute? attribute) =>
        long.TryParse((string?)attribute, CultureInfo.InvariantCulture, out var value) ? value : null;
}
