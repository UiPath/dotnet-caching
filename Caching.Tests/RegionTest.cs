namespace UiPath.Platform.Caching.Tests;

public class RegionTest
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubsitute();

    [Fact]
    public void AllOperations()
    {
        new Region(null).IsNull.Should().BeTrue();
        new Region().IsNull.Should().BeTrue();
        new Region(string.Empty).IsNull.Should().BeTrue();
        new Region("         ").IsNull.Should().BeTrue();
        var value = _fixture.Create<string>().ToUpperInvariant();
        Assert.True(new Region(value) == new Region(value.ToLowerInvariant()));
        Assert.True(new Region(value) == value.ToLowerInvariant());
        Assert.True(new Region(value) != new Region(value.ToLowerInvariant() + _fixture.Create<string>()));
        Assert.True(new Region(value) != value.ToLowerInvariant() + _fixture.Create<string>());
        Assert.True(new Region(value).GetHashCode() != 0);
    }
}
