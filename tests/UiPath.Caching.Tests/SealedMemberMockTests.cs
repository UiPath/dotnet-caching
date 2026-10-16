using Moq;
using UiPath.Caching.Tests.Fakes;

namespace UiPath.Caching.Tests;

/// <summary>The span, state and generated-expiration members of the cache interfaces are sealed: a proxy cannot intercept them, so on a mock they run and reach the <see cref="CacheKey"/> members the test set up.</summary>
public class SealedMemberMockTests
{
    private static readonly DateTimeOffset Deadline = DateTimeOffset.UtcNow.AddMinutes(7);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(MockBehavior.Strict)]
    [InlineData(MockBehavior.Loose)]
    public async Task A_moq_cache_serves_the_sealed_members_from_its_CacheKey_setups(MockBehavior behavior)
    {
        var mock = new Mock<ICache>(behavior);
        mock.Setup(c => c.GetAsync<string>(new CacheKey("user:42"), null, It.IsAny<CancellationToken>())).Returns(new ValueTask<string?>("hit"));
        mock.Setup(c => c.GetOrAddAsync(It.IsAny<CacheKey>(), It.IsAny<Func<CancellationToken, Task<string?>>>(), null, It.IsAny<CancellationToken>()))
            .Returns((CacheKey _, Func<CancellationToken, Task<string?>> generator, CachePolicy? _, CancellationToken token) => new ValueTask<string?>(generator(token)));
        mock.Setup(c => c.GetCacheEntryAsync<string>(new CacheKey("jwt"), null, It.IsAny<CancellationToken>())).Returns(new ValueTask<ICacheEntry<string?>>(new TestCacheEntry<string?> { Expiration = DateTimeOffset.MinValue }));
        mock.Setup(c => c.SetAsync<string>(new CacheKey("jwt"), "token", Deadline, null, It.IsAny<CancellationToken>())).Returns(new ValueTask<bool>(true));
        var cache = mock.Object;

        (await SpanGet(cache)).Should().Be("hit");
        (await SpanGetOrAdd(cache)).Should().Be("span");
        (await SpanGetOrAddWithState(cache)).Should().Be("span:1");
        (await cache.GetOrAddAsync<string, int>("k", 2, static (n, _) => Task.FromResult<string?>($"state:{n}"), null, Ct)).Should().Be("state:2");
        (await cache.GetOrAddWithExpirationAsync<string>("jwt", _ => Task.FromResult(new GeneratedValue<string>("token", Deadline)), null, Ct)).Should().Be("token");

        mock.Verify(c => c.SetAsync<string>(new CacheKey("jwt"), "token", Deadline, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(MockBehavior.Strict)]
    [InlineData(MockBehavior.Loose)]
    public async Task A_moq_typed_cache_serves_the_sealed_members_from_its_CacheKey_setups(MockBehavior behavior)
    {
        var mock = new Mock<ICache<string>>(behavior);
        mock.Setup(c => c.GetAsync(new CacheKey("user:42"), It.IsAny<CancellationToken>())).Returns(new ValueTask<string?>("hit"));
        mock.Setup(c => c.GetOrAddAsync(It.IsAny<CacheKey>(), It.IsAny<Func<CancellationToken, Task<string?>>>(), It.IsAny<CancellationToken>()))
            .Returns((CacheKey _, Func<CancellationToken, Task<string?>> generator, CancellationToken token) => new ValueTask<string?>(generator(token)));
        mock.Setup(c => c.GetAsync(new CacheKey("jwt"), It.IsAny<CancellationToken>())).Returns(new ValueTask<string?>((string?)null));
        mock.Setup(c => c.ContainsAsync(new CacheKey("jwt"), It.IsAny<CancellationToken>())).Returns(new ValueTask<bool>(false));
        mock.Setup(c => c.SetAsync(new CacheKey("jwt"), "token", Deadline, It.IsAny<CancellationToken>())).Returns(new ValueTask<bool>(true));
        var cache = mock.Object;

        (await SpanGet(cache)).Should().Be("hit");
        (await SpanGetOrAdd(cache)).Should().Be("span");
        (await SpanGetOrAddWithState(cache)).Should().Be("span:1");
        (await cache.GetOrAddAsync("k", 2, static (n, _) => Task.FromResult<string?>($"state:{n}"), Ct)).Should().Be("state:2");
        (await cache.GetOrAddWithExpirationAsync("jwt", _ => Task.FromResult(new GeneratedValue<string>("token", Deadline)), Ct)).Should().Be("token");

        mock.Verify(c => c.SetAsync(new CacheKey("jwt"), "token", Deadline, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_moq_and_an_nsubstitute_hash_cache_serve_the_span_reads_from_their_CacheKey_setups()
    {
        var mock = new Mock<IHashCache>(MockBehavior.Strict);
        mock.Setup(c => c.GetItemAsync<string>(new CacheKey("user:42"), "f", null, It.IsAny<CancellationToken>())).Returns(new ValueTask<string?>("moq"));
        var substitute = Substitute.For<IHashCache<string>>();
        ValueTaskStubs.Returns(substitute.GetItemAsync(new CacheKey("user:42"), "f", Arg.Any<CancellationToken>()), "nsub");

        (await SpanGetItem(mock.Object)).Should().Be("moq");
        (await SpanGetItem(substitute)).Should().Be("nsub");
    }

    [Fact]
    public async Task An_nsubstitute_cache_serves_the_span_read_from_its_CacheKey_setup()
    {
        var cache = Substitute.For<ICache>();
        ValueTaskStubs.Returns(cache.GetAsync<string>(new CacheKey("user:42"), null, Arg.Any<CancellationToken>()), "hit");
        var typed = Substitute.For<ICache<string>>();
        ValueTaskStubs.Returns(typed.GetAsync(new CacheKey("user:42"), Arg.Any<CancellationToken>()), "typed");

        (await SpanGet(cache)).Should().Be("hit");
        (await SpanGet(typed)).Should().Be("typed");
    }

    private static Span<char> Key(string key) => key.ToCharArray();

    private static ValueTask<string?> SpanGet(ICache cache) => cache.GetAsync<string>(Key("user:42"), null);

    private static ValueTask<string?> SpanGet(ICache<string> cache) => cache.GetAsync(Key("user:42"));

    private static ValueTask<string?> SpanGetOrAdd(ICache cache) => cache.GetOrAddAsync(Key("k"), _ => Task.FromResult<string?>("span"), null);

    private static ValueTask<string?> SpanGetOrAdd(ICache<string> cache) => cache.GetOrAddAsync(Key("k"), _ => Task.FromResult<string?>("span"));

    private static ValueTask<string?> SpanGetOrAddWithState(ICache cache) => cache.GetOrAddAsync<string, int>(Key("k"), 1, static (n, _) => Task.FromResult<string?>($"span:{n}"), null);

    private static ValueTask<string?> SpanGetOrAddWithState(ICache<string> cache) => cache.GetOrAddAsync(Key("k"), 1, static (n, _) => Task.FromResult<string?>($"span:{n}"));

    private static ValueTask<string?> SpanGetItem(IHashCache cache) => cache.GetItemAsync<string>(Key("user:42"), "f", null);

    private static ValueTask<string?> SpanGetItem(IHashCache<string> cache) => cache.GetItemAsync(Key("user:42"), "f");
}
