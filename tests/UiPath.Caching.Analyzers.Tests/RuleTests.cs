using Xunit;

namespace UiPath.Caching.Analyzers.Tests;

/// <summary>What the rule declares about itself.</summary>
public class RuleTests
{
    // Bulk configuration such as `dotnet_analyzer_diagnostic.category-Performance.severity = warning` must not reach a rule
    // that flags the documented default shape; only its own id or its own category raises it.
    [Theory]
    [InlineData("Design")]
    [InlineData("Globalization")]
    [InlineData("Interoperability")]
    [InlineData("Maintainability")]
    [InlineData("Naming")]
    [InlineData("Performance")]
    [InlineData("Reliability")]
    [InlineData("Security")]
    [InlineData("Style")]
    [InlineData("Usage")]
    public void The_rule_is_not_in_a_shared_category(string shared) =>
        Assert.NotEqual(shared, CapturingGeneratorAnalyzer.Rule.Category);
}
