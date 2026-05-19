namespace UiPath.Platform.Caching.Tests.Redis;

public class NullHashCacheTests(ITestContextAccessor testContextAccessor)
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubstitute();

    [Fact]
    public async Task GetOrAdd_Works()
    {
        var sut = new NullHashCache();
        IDictionary<string, TestDto?> expected = _fixture.Create<IDictionary<string, TestDto?>>();
        CacheKey cacheKey = _fixture.Create<string>();
        var actual = await sut.GetOrAddAsync(cacheKey, _ => Task.FromResult(expected), (CachePolicy?)null, testContextAccessor.Current.CancellationToken);
        actual.Should().NotBeNull();
        actual.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task GetOrAdd_TimeSpan_Works()
    {
        var sut = new NullHashCache();
        IDictionary<string, TestDto?> expected = _fixture.Create<IDictionary<string, TestDto?>>();
        CacheKey cacheKey = _fixture.Create<string>();
        var actual = await sut.GetOrAddAsync(cacheKey, _ => Task.FromResult(expected), TimeSpan.Zero, token: testContextAccessor.Current.CancellationToken);
        actual.Should().NotBeNull();
        actual.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task GetOrAdd_DateTimeOffset_Works()
    {
        var sut = new NullHashCache();
        IDictionary<string, TestDto?> expected = _fixture.Create<IDictionary<string, TestDto?>>();
        CacheKey cacheKey = _fixture.Create<string>();
        var actual = await sut.GetOrAddAsync(cacheKey, _ => Task.FromResult(expected), DateTimeOffset.UtcNow, (CachePolicy?)null, testContextAccessor.Current.CancellationToken);
        actual.Should().NotBeNull();
        actual.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task GetOrAdd_DateTimeOffset_HashCacheSetOption_Works()
    {
        var sut = new NullHashCache();
        IDictionary<string, TestDto?> expected = _fixture.Create<IDictionary<string, TestDto?>>();
        CacheKey cacheKey = _fixture.Create<string>();
        var actual = await sut.GetOrAddAsync(cacheKey, _ => Task.FromResult(expected), expiration: DateTimeOffset.UtcNow, setOption: HashCacheSetOption.KeyReplace, token: testContextAccessor.Current.CancellationToken);
        actual.Should().NotBeNull();
        actual.Should().BeEquivalentTo(expected);
    }
}
