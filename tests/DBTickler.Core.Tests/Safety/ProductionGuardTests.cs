using DBTickler.Core.Safety;

namespace DBTickler.Core.Tests.Safety;

/// <summary>
/// Covers <see cref="ProductionGuard.AssessName"/>, the pure name-based half of the guard.
/// <see cref="ProductionGuard.AssessAsync"/> needs a live server and is out of scope here.
/// </summary>
public class ProductionGuardTests
{
    [Theory]
    [InlineData("PRODUCTION")]
    [InlineData("Production")]
    [InlineData("prod")]
    [InlineData("PROD")]
    [InlineData("live")]
    [InlineData("prd")]
    [InlineData("prod-01")]
    [InlineData("live.internal.corp")]
    [InlineData("sql-prod-01")]
    public void Database_names_that_look_like_production_raise_an_elevated_signal(string databaseName)
    {
        var assessment = ProductionGuard.AssessName(server: "sql01", database: databaseName);

        Assert.Equal(RiskLevel.Elevated, assessment.Level);
        Assert.True(assessment.RequiresConfirmation);
        Assert.Single(assessment.Signals);
    }

    [Theory]
    [InlineData("PROD-SQL-01")]
    [InlineData("live-server")]
    public void Server_names_that_look_like_production_raise_an_elevated_signal(string serverName)
    {
        var assessment = ProductionGuard.AssessName(server: serverName, database: "AdventureWorks2022");

        Assert.Equal(RiskLevel.Elevated, assessment.Level);
        Assert.Contains(assessment.Signals, s => s.Contains(serverName));
    }

    [Theory]
    [InlineData("productdb")]     // "prod" is not a whole word here
    [InlineData("reproduction")]  // contains "production" but not as a whole word
    [InlineData("staging")]
    [InlineData("dev")]
    [InlineData("AdventureWorks2022")]
    [InlineData("testlab01")]
    public void Names_that_only_superficially_resemble_the_keywords_do_not_match(string name)
    {
        var assessment = ProductionGuard.AssessName(server: "sql01", database: name);

        Assert.Equal(RiskLevel.Low, assessment.Level);
        Assert.Empty(assessment.Signals);
        Assert.False(assessment.RequiresConfirmation);
    }

    /// <summary>
    /// Underscore- and digit-suffixed names are among the most common real-world server and
    /// database naming conventions, so they have to be caught by a check whose entire job is
    /// to notice them before a destructive run.
    ///
    /// These were a false negative while the check used a <c>\b</c> word boundary: .NET treats
    /// digits and underscore as word characters, so the boundary never fired on "Sales_Prod"
    /// or "PROD01" even though it did on "Sales-Prod". The check now works out the boundary
    /// from the surrounding characters, treating a change of case as a boundary too.
    /// </summary>
    [Theory]
    [InlineData("Sales_Prod")]
    [InlineData("DB_PROD_01")]
    [InlineData("CRM_LIVE")]
    [InlineData("PROD_SERVER")]
    [InlineData("PROD01")]
    [InlineData("SQLPROD2")]
    [InlineData("LiveDB")]
    public void Underscore_or_digit_adjacent_production_like_names_should_also_be_flagged(string name)
    {
        var assessment = ProductionGuard.AssessName(server: "sql01", database: name);

        Assert.Equal(RiskLevel.Elevated, assessment.Level);
        Assert.NotEmpty(assessment.Signals);
    }

    [Fact]
    public void Both_server_and_database_matching_produce_two_signals()
    {
        var assessment = ProductionGuard.AssessName(server: "prod-sql01", database: "prod-orders");

        Assert.Equal(2, assessment.Signals.Count);
        Assert.Equal(RiskLevel.Elevated, assessment.Level);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("   ", "   ")]
    [InlineData(null, "AdventureWorks2022")]
    [InlineData("sql01", null)]
    public void Null_empty_or_whitespace_names_produce_no_signals(string? server, string? database)
    {
        var assessment = ProductionGuard.AssessName(server, database);

        Assert.Equal(RiskLevel.Low, assessment.Level);
        Assert.Empty(assessment.Signals);
    }

    [Fact]
    public void RiskAssessment_None_has_no_signals_and_does_not_require_confirmation()
    {
        Assert.Equal(RiskLevel.Low, RiskAssessment.None.Level);
        Assert.Empty(RiskAssessment.None.Signals);
        Assert.False(RiskAssessment.None.RequiresConfirmation);
        Assert.Equal("No production indicators found.", RiskAssessment.None.Describe());
    }

    [Fact]
    public void Describe_lists_every_signal_as_a_bullet_point()
    {
        var assessment = ProductionGuard.AssessName(server: "prod-01", database: "live-01");
        var description = assessment.Describe();

        Assert.Contains("• ", description);
        Assert.Equal(2, description.Split(Environment.NewLine).Length);
    }

    [Theory]
    [InlineData(RiskLevel.Low, false)]
    [InlineData(RiskLevel.Elevated, true)]
    [InlineData(RiskLevel.High, true)]
    public void RequiresConfirmation_is_true_at_elevated_or_above(RiskLevel level, bool expected)
    {
        var assessment = new RiskAssessment(level, ["signal"]);
        Assert.Equal(expected, assessment.RequiresConfirmation);
    }
}
