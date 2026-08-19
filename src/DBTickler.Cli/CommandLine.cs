using System.Globalization;

namespace DBTickler.Cli;

/// <summary>
/// A small argument parser. Hand-rolled rather than taken from a package so the CLI has no
/// dependency beyond the core library and the published binary stays small.
/// </summary>
public sealed class CommandLine
{
    private readonly Dictionary<string, string?> _options = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _consumed = new(StringComparer.OrdinalIgnoreCase);

    private CommandLine(string command, IReadOnlyList<string> positional)
    {
        Command = command;
        Positional = positional;
    }

    public string Command { get; }
    public IReadOnlyList<string> Positional { get; }

    /// <summary>Options supplied but never read — almost always a typo worth reporting.</summary>
    public IEnumerable<string> UnknownOptions => _options.Keys.Where(key => !_consumed.Contains(key));

    public static CommandLine Parse(string[] args)
    {
        var command = args.Length > 0 && !args[0].StartsWith('-') ? args[0] : "help";
        var positional = new List<string>();
        var startIndex = command == "help" && (args.Length == 0 || args[0].StartsWith('-')) ? 0 : 1;

        var result = new CommandLine(command, positional);

        for (var i = startIndex; i < args.Length; i++)
        {
            var argument = args[i];

            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                positional.Add(argument);
                continue;
            }

            var name = argument[2..];
            string? value = null;

            var equalsIndex = name.IndexOf('=', StringComparison.Ordinal);
            if (equalsIndex >= 0)
            {
                value = name[(equalsIndex + 1)..];
                name = name[..equalsIndex];
            }
            else if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                value = args[++i];
            }

            result._options[name] = value;
        }

        return result;
    }

    public bool Has(string name)
    {
        _consumed.Add(name);
        return _options.ContainsKey(name);
    }

    /// <summary>A flag is true when present with no value, or with an explicit true value.</summary>
    public bool Flag(string name, bool defaultValue = false)
    {
        _consumed.Add(name);
        if (!_options.TryGetValue(name, out var value)) return defaultValue;
        if (value is null) return true;
        return bool.TryParse(value, out var parsed) ? parsed : true;
    }

    public string? String(string name, string? defaultValue = null)
    {
        _consumed.Add(name);
        return _options.TryGetValue(name, out var value) && value is not null ? value : defaultValue;
    }

    public int Int(string name, int defaultValue)
    {
        _consumed.Add(name);
        if (!_options.TryGetValue(name, out var value) || value is null) return defaultValue;

        if (!int.TryParse(value, CultureInfo.InvariantCulture, out var parsed))
            throw new ArgumentException($"--{name} expects a whole number, but got '{value}'.");

        return parsed;
    }

    public int? NullableInt(string name)
    {
        _consumed.Add(name);
        if (!_options.TryGetValue(name, out var value) || value is null) return null;

        if (!int.TryParse(value, CultureInfo.InvariantCulture, out var parsed))
            throw new ArgumentException($"--{name} expects a whole number, but got '{value}'.");

        return parsed;
    }
}
