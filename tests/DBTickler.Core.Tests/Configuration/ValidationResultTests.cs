using DBTickler.Core.Configuration;

namespace DBTickler.Core.Tests.Configuration;

public class ValidationResultTests
{
    [Fact]
    public void New_result_is_valid_with_no_errors_or_warnings()
    {
        var result = new ValidationResult();

        Assert.True(result.IsValid);
        Assert.False(result.HasWarnings);
        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void AddError_makes_the_result_invalid()
    {
        var result = new ValidationResult();
        result.AddError("boom");

        Assert.False(result.IsValid);
        Assert.Single(result.Errors);
        Assert.Equal("boom", result.Errors[0]);
    }

    [Fact]
    public void AddWarning_does_not_affect_validity()
    {
        var result = new ValidationResult();
        result.AddWarning("heads up");

        Assert.True(result.IsValid);
        Assert.True(result.HasWarnings);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public void Merge_combines_errors_and_warnings_from_both_results_in_order()
    {
        var first = new ValidationResult();
        first.AddError("error-1");
        first.AddWarning("warning-1");

        var second = new ValidationResult();
        second.AddError("error-2");
        second.AddWarning("warning-2");

        first.Merge(second);

        Assert.Equal(["error-1", "error-2"], first.Errors);
        Assert.Equal(["warning-1", "warning-2"], first.Warnings);
        Assert.False(first.IsValid);
    }

    [Fact]
    public void Merge_with_a_clean_result_leaves_validity_unaffected()
    {
        var first = new ValidationResult();
        first.AddWarning("warning-1");

        first.Merge(new ValidationResult());

        Assert.True(first.IsValid);
        Assert.Single(first.Warnings);
    }

    [Fact]
    public void FormatErrors_joins_with_newlines()
    {
        var result = new ValidationResult();
        result.AddError("first");
        result.AddError("second");

        Assert.Equal($"first{Environment.NewLine}second", result.FormatErrors());
    }

    [Fact]
    public void FormatWarnings_joins_with_newlines()
    {
        var result = new ValidationResult();
        result.AddWarning("first");
        result.AddWarning("second");

        Assert.Equal($"first{Environment.NewLine}second", result.FormatWarnings());
    }

    [Fact]
    public void FormatErrors_on_a_clean_result_is_empty_string()
    {
        Assert.Equal("", new ValidationResult().FormatErrors());
    }
}
