namespace UiPath.Caching.Tests.Redis;

public class NullCacheTests(ITestContextAccessor testContextAccessor)
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubstitute();

    [Fact]
    public async Task GetOrAdd_Works()
    {
        var sut = new NullCache();
        CacheKey cacheKey = _fixture.Create<string>();
        var expected = _fixture.Create<TestDto?>();
        var actual = await sut.GetOrAddAsync(cacheKey, _ => Task.FromResult(expected), (CachePolicy?)null, testContextAccessor.Current.CancellationToken);
        actual.Should().NotBeNull();
        actual.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task GetOrAdd_TimeSpan_Works()
    {
        var sut = new NullCache();
        CacheKey cacheKey = _fixture.Create<string>();
        var expected = _fixture.Create<TestDto?>();
        var actual = await sut.GetOrAddAsync(cacheKey, _ => Task.FromResult(expected), TimeSpan.Zero, token: testContextAccessor.Current.CancellationToken);
        actual.Should().NotBeNull();
        actual.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task GetOrAdd_DateTimeOffset_Works()
    {
        var sut = new NullCache();
        CacheKey cacheKey = _fixture.Create<string>();
        var expected = _fixture.Create<TestDto?>();
        var actual = await sut.GetOrAddAsync(cacheKey, _ => Task.FromResult(expected), DateTimeOffset.UtcNow, token: testContextAccessor.Current.CancellationToken);
        actual.Should().NotBeNull();
        actual.Should().BeEquivalentTo(expected);
    }
}
