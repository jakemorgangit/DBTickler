using System.Text.Json;

namespace DBTickler.Core.Configuration;

/// <summary>
/// Reads and writes saved sessions under the user's application data directory.
/// </summary>
public sealed class SessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _directory;

    public SessionStore(string? directory = null) =>
        _directory = directory ?? DefaultDirectory();

    public string Directory => _directory;

    public static string DefaultDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DBTickler",
        "Sessions");

    /// <summary>
    /// Maps a session name to a file name. Session names come from a free-text prompt, so
    /// they can contain path separators, drive letters, or "..", and v1 stripped only spaces
    /// and slashes — enough to leave a name like "..\..\startup" writing outside the sessions
    /// directory. Everything that is not a safe character is replaced here, and the result is
    /// verified to stay inside the store.
    /// </summary>
    public static string ToFileName(string sessionName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionName);

        var sanitized = new string(sessionName
            .Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '_')
            .ToArray())
            .Trim('_');

        if (sanitized.Length == 0)
            sanitized = "session";

        if (sanitized.Length > 100)
            sanitized = sanitized[..100];

        return sanitized + ".json";
    }

    public string GetFilePath(string sessionName)
    {
        var path = Path.GetFullPath(Path.Combine(_directory, ToFileName(sessionName)));
        var root = Path.GetFullPath(_directory) + Path.DirectorySeparatorChar;

        if (!path.StartsWith(root, StringComparison.Ordinal))
            throw new InvalidOperationException($"Session name '{sessionName}' does not map to a safe file path.");

        return path;
    }

    public void Save(SessionConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        System.IO.Directory.CreateDirectory(_directory);
        config.LastUsed = DateTimeOffset.UtcNow;
        config.Version = SessionConfig.CurrentVersion;
        config.ProtectPassword();

        var path = GetFilePath(config.SessionName);

        // Written to a temporary file and moved into place so an interrupted save cannot
        // leave a truncated, unreadable session behind.
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(config, JsonOptions));
        File.Move(temporary, path, overwrite: true);
    }

    public SessionConfig Load(string path)
    {
        var config = JsonSerializer.Deserialize<SessionConfig>(File.ReadAllText(path))
            ?? throw new InvalidDataException($"'{path}' does not contain a valid session.");

        config.UnprotectPassword();
        return config;
    }

    /// <summary>All saved sessions, most recently used first. Unreadable files are skipped.</summary>
    public IReadOnlyList<SessionConfig> LoadAll()
    {
        if (!System.IO.Directory.Exists(_directory))
            return [];

        var sessions = new List<SessionConfig>();
        foreach (var path in System.IO.Directory.EnumerateFiles(_directory, "*.json"))
        {
            try
            {
                sessions.Add(Load(path));
            }
            catch (Exception exception) when (exception is JsonException or IOException or InvalidDataException)
            {
                // One corrupt file must not hide every other saved session.
            }
        }

        sessions.Sort((left, right) => right.LastUsed.CompareTo(left.LastUsed));
        return sessions;
    }

    public bool Delete(string sessionName)
    {
        var path = GetFilePath(sessionName);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }
}
