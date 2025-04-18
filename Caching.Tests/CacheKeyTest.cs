namespace UiPath.Platform.Caching.Tests;

public class CacheKeyTest
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubstitute();

    [Fact]
    public void AllOperations()
    {
        new CacheKey(null).IsNull.Should().BeTrue();
        new CacheKey().IsNull.Should().BeTrue();
        new CacheKey(string.Empty).IsNull.Should().BeTrue();
        new CacheKey("         ").IsNull.Should().BeTrue();
        var value = _fixture.Create<string>().ToUpperInvariant();
        Assert.True(new CacheKey(value) == new CacheKey(value.ToLowerInvariant()));
        Assert.True(new CacheKey(value) == value.ToLowerInvariant());
        Assert.True(new CacheKey(value) != new CacheKey(value.ToLowerInvariant() + _fixture.Create<string>()));
        Assert.True(new CacheKey(value) != value.ToLowerInvariant() + _fixture.Create<string>());
        Assert.True(new CacheKey(value).GetHashCode() != 0);
        Assert.True(new CacheKey(value).GetHashCode() == new CacheKey(value.ToLowerInvariant()).GetHashCode());
    }
}
