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

    [Fact]
    public async Task Batch_argument_members_pass_the_argument_to_the_generator()
    {
        var token = testContextAccessor.Current.CancellationToken;
        var source = new Source("v");
        KeyValuePair<CacheKey, int>[] entries = [new("a", 1), new("b", 2)];

        var actual = new[]
        {
            await ((ICache)NullCache.Instance).GetOrAddAsync<string, int, Source>(entries, source, static (s, states, _) => s.LoadAsync(states), policy: null, token),
            await ((ICache)NullCache.Instance).GetOrAddAsync<string, int, Source>(entries, source, static (s, states, _) => s.LoadAsync(states), TimeSpan.FromMinutes(1), policy: null, token),
            await ((ICache)NullCache.Instance).GetOrAddAsync<string, int, Source>(entries, source, static (s, states, _) => s.LoadAsync(states), DateTimeOffset.UtcNow.AddMinutes(1), policy: null, token),
        };

        actual.Should().AllSatisfy(result => result.Should().Equal(new KeyValuePair<int, string?>(1, "v1"), new KeyValuePair<int, string?>(2, "v2")));
        source.Runs.Should().Be(3);
    }

    private sealed class Source(string prefix)
    {
        public int Runs { get; private set; }

        public Task<KeyValuePair<int, string?>[]> LoadAsync(int[] states)
        {
            Runs++;
            return Task.FromResult(Array.ConvertAll(states, s => new KeyValuePair<int, string?>(s, prefix + s)));
        }
    }
}
