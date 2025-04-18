namespace UiPath.Platform.Caching.Tests.Redis;

public class NullHashCacheTests
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubstitute();

    [Fact]
    public async Task GetOrAdd_Works()
    {
        var sut = new NullHashCache();
        IDictionary<string, TestDto?> expected = _fixture.Create<IDictionary<string, TestDto?>>();
        CacheKey cacheKey = _fixture.Create<string>();
        var actual = await sut.GetOrAddAsync(cacheKey, _ => Task.FromResult(expected), CancellationToken.None);
        actual.Should().NotBeNull();
        actual.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task GetOrAdd_TimeSpan_Works()
    {
        var sut = new NullHashCache();
        IDictionary<string, TestDto?> expected = _fixture.Create<IDictionary<string, TestDto?>>();
        CacheKey cacheKey = _fixture.Create<string>();
        var actual = await sut.GetOrAddAsync(cacheKey, _ => Task.FromResult(expected), TimeSpan.Zero, CancellationToken.None);
        actual.Should().NotBeNull();
        actual.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task GetOrAdd_DateTimeOffset_Works()
    {
        var sut = new NullHashCache();
        IDictionary<string, TestDto?> expected = _fixture.Create<IDictionary<string, TestDto?>>();
        CacheKey cacheKey = _fixture.Create<string>();
        var actual = await sut.GetOrAddAsync(cacheKey, _ => Task.FromResult(expected), DateTimeOffset.UtcNow, CancellationToken.None);
        actual.Should().NotBeNull();
        actual.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task GetOrAdd_DateTimeOffset_HashCacheSetOption_Works()
    {
        var sut = new NullHashCache();
        IDictionary<string, TestDto?> expected = _fixture.Create<IDictionary<string, TestDto?>>();
        CacheKey cacheKey = _fixture.Create<string>();
        var actual = await sut.GetOrAddAsync(cacheKey, _ => Task.FromResult(expected), DateTimeOffset.UtcNow, HashCacheSetOption.KeyReplace, CancellationToken.None);
        actual.Should().NotBeNull();
        actual.Should().BeEquivalentTo(expected);
    }
}
