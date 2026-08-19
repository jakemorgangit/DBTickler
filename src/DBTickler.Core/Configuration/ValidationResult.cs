namespace DBTickler.Core.Configuration;

/// <summary>
/// Outcome of validating a profile. Errors block a run; warnings are shown but do not.
/// </summary>
public sealed class ValidationResult
{
    private readonly List<string> _errors = [];
    private readonly List<string> _warnings = [];

    public IReadOnlyList<string> Errors => _errors;
    public IReadOnlyList<string> Warnings => _warnings;

    public bool IsValid => _errors.Count == 0;
    public bool HasWarnings => _warnings.Count > 0;

    public void AddError(string message) => _errors.Add(message);
    public void AddWarning(string message) => _warnings.Add(message);

    public void Merge(ValidationResult other)
    {
        _errors.AddRange(other._errors);
        _warnings.AddRange(other._warnings);
    }

    public string FormatErrors() => string.Join(Environment.NewLine, _errors);
    public string FormatWarnings() => string.Join(Environment.NewLine, _warnings);
}
