using DBTickler.Core.Workloads;

namespace DBTickler.Core.Tests.Workloads;

public class SchemaProbeTests
{
    [Theory]
    [InlineData("16.0.1000.6", 16)]
    [InlineData("15.0.2000.5", 15)]
    [InlineData("8.00.194", 8)]
    [InlineData("15", 15)]        // no dot at all: the whole string is the "first part"
    [InlineData("", 0)]
    [InlineData("   ", 0)]
    [InlineData("abc.1.2", 0)]    // non-numeric major version
    [InlineData("16.abc", 16)]    // only the first segment needs to parse
    public void ParseMajorVersion_extracts_the_leading_numeric_segment(string productVersion, int expectedMajor) =>
        Assert.Equal(expectedMajor, SchemaProbe.ParseMajorVersion(productVersion));

    [Fact]
    public void ParseMajorVersion_is_null_tolerant()
    {
        // Nullable is enabled, but the underlying implementation is a simple null check before
        // any parsing, so passing null explicitly must not throw.
        Assert.Equal(0, SchemaProbe.ParseMajorVersion(null!));
    }
}
