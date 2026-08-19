using DBTickler.Core.Data;

namespace DBTickler.Core.Tests.Data;

public class SqlIdentifierTests
{
    [Fact]
    public void Quote_wraps_a_plain_identifier_in_brackets() =>
        Assert.Equal("[Simple]", SqlIdentifier.Quote("Simple"));

    [Fact]
    public void Quote_doubles_an_embedded_closing_bracket_the_way_QUOTENAME_does() =>
        Assert.Equal("[Order]]Table]", SqlIdentifier.Quote("Order]Table"));

    [Fact]
    public void Quote_doubles_every_closing_bracket_when_there_are_several() =>
        Assert.Equal("[a]]b]]c]", SqlIdentifier.Quote("a]b]c"));

    [Fact]
    public void Quote_leaves_an_opening_bracket_alone() =>
        Assert.Equal("[a[b]", SqlIdentifier.Quote("a[b"));

    [Fact]
    public void Quote_handles_an_empty_string() =>
        Assert.Equal("[]", SqlIdentifier.Quote(""));

    [Fact]
    public void Quote_throws_for_null() =>
        Assert.Throws<ArgumentNullException>(() => SqlIdentifier.Quote(null!));

    [Fact]
    public void QuoteTwoPart_quotes_schema_and_name_independently_and_joins_with_a_dot() =>
        Assert.Equal("[dbo].[My]]Table]", SqlIdentifier.QuoteTwoPart("dbo", "My]Table"));

    [Fact]
    public void QuoteTwoPart_quotes_a_schema_name_that_itself_needs_escaping() =>
        Assert.Equal("[my]]schema].[Table]", SqlIdentifier.QuoteTwoPart("my]schema", "Table"));
}
