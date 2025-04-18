namespace UiPath.Platform.Caching.Tests.Redis;

public class NullCacheTests
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubstitute();

    [Fact]
    public async Task GetOrAdd_Works()
    {
        var sut = new NullCache();
        CacheKey cacheKey = _fixture.Create<string>();
        var expected = _fixture.Create<TestDto?>();
        var actual = await sut.GetOrAddAsync(cacheKey, _ => Task.FromResult(expected), token: default);
        actual.Should().NotBeNull();
        actual.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task GetOrAdd_TimeSpan_Works()
    {
        var sut = new NullCache();
        CacheKey cacheKey = _fixture.Create<string>();
        var expected = _fixture.Create<TestDto?>();
        var actual = await sut.GetOrAddAsync(cacheKey, _ => Task.FromResult(expected), TimeSpan.Zero, token: default);
        actual.Should().NotBeNull();
        actual.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task GetOrAdd_DateTimeOffset_Works()
    {
        var sut = new NullCache();
        CacheKey cacheKey = _fixture.Create<string>();
        var expected = _fixture.Create<TestDto?>();
        var actual = await sut.GetOrAddAsync(cacheKey, _ => Task.FromResult(expected), DateTimeOffset.UtcNow, token: default);
        actual.Should().NotBeNull();
        actual.Should().BeEquivalentTo(expected);
    }
}
