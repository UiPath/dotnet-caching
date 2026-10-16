using UiPath.Caching.Tests.Fakes;

namespace UiPath.Caching.Tests;

/// <summary>Each sealed member takes the capability interface on a cache that implements it, and the matching <see cref="CacheKey"/> member on one that does not.</summary>
public class SealedMemberPathTests
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);
    private static readonly DateTimeOffset Deadline = DateTimeOffset.UtcNow.AddMinutes(5);
    private static readonly Func<CancellationToken, Task<string?>> Load = static _ => Task.FromResult<string?>("v");
    private static readonly Func<int, CancellationToken, Task<string?>> LoadState = static (n, _) => Task.FromResult<string?>($"v{n}");
    private static readonly Func<CancellationToken, Task<IDictionary<string, string?>>> LoadHash = static _ => Task.FromResult<IDictionary<string, string?>>(new Dictionary<string, string?> { ["f"] = "v" });

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_capable_cache_answers_every_sealed_member()
    {
        using var owner = InMemoryMultilayer.Cache();
        ICache cache = owner;

        (await cache.GetOrAddAsync(Key("a"), Load, null, Ct)).Should().Be("v");
        (await cache.GetOrAddAsync(Key("b"), Load, Ttl, null, Ct)).Should().Be("v");
        (await cache.GetOrAddAsync(Key("c"), Load, Deadline, null, Ct)).Should().Be("v");
        (await cache.GetOrAddAsync(Key("d"), 1, LoadState, null, Ct)).Should().Be("v1");
        (await cache.GetOrAddAsync(Key("e"), 2, LoadState, Ttl, null, Ct)).Should().Be("v2");
        (await cache.GetOrAddAsync(Key("f"), 3, LoadState, Deadline, null, Ct)).Should().Be("v3");
        (await cache.GetOrAddAsync<string, int>("g", 4, LoadState, null, Ct)).Should().Be("v4");
        (await cache.GetOrAddAsync<string, int>("h", 5, LoadState, Ttl, null, Ct)).Should().Be("v5");
        (await cache.GetOrAddAsync<string, int>("i", 6, LoadState, Deadline, null, Ct)).Should().Be("v6");
        (await cache.GetOrAddWithExpirationAsync<string>("j", _ => Task.FromResult(new GeneratedValue<string>("g", Deadline)), null, Ct)).Should().Be("g");
        (await cache.GetAsync<string>(Key("a"), null, Ct)).Should().Be("v");
        (await cache.GetCacheEntryAsync<string>(Key("b"), null, Ct)).Value.Should().Be("v");
        (await cache.ContainsAsync<string>(Key("c"), Ct)).Should().BeTrue();
        (await cache.RemoveAsync<string>(Key("c"), Ct)).Should().BeTrue();
        (await cache.ContainsAsync<string>(Key("c"), Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task A_cache_without_the_capabilities_gets_the_CacheKey_members()
    {
        var cache = Substitute.For<ICache>();

        await cache.GetOrAddAsync(Key("a"), Load, null, Ct);
        await cache.GetOrAddAsync(Key("b"), Load, Ttl, null, Ct);
        await cache.GetOrAddAsync(Key("c"), Load, Deadline, null, Ct);
        await cache.GetOrAddAsync(Key("d"), 1, LoadState, null, Ct);
        await cache.GetOrAddAsync(Key("e"), 2, LoadState, Ttl, null, Ct);
        await cache.GetOrAddAsync(Key("f"), 3, LoadState, Deadline, null, Ct);
        await cache.GetAsync<string>(Key("a"), null, Ct);
        await cache.GetCacheEntryAsync<string>(Key("b"), null, Ct);
        await cache.ContainsAsync<string>(Key("c"), Ct);
        await cache.RemoveAsync<string>(Key("c"), Ct);

        await cache.Received(1).GetOrAddAsync(new CacheKey("a"), Load, null, Ct);
        await cache.Received(1).GetOrAddAsync(new CacheKey("b"), Load, Ttl, null, Ct);
        await cache.Received(1).GetOrAddAsync(new CacheKey("c"), Load, Deadline, null, Ct);
        await cache.Received(1).GetOrAddAsync(new CacheKey("d"), Arg.Any<Func<CancellationToken, Task<string?>>>(), null, Ct);
        await cache.Received(1).GetOrAddAsync(new CacheKey("e"), Arg.Any<Func<CancellationToken, Task<string?>>>(), Ttl, null, Ct);
        await cache.Received(1).GetOrAddAsync(new CacheKey("f"), Arg.Any<Func<CancellationToken, Task<string?>>>(), Deadline, null, Ct);
        await cache.Received(1).GetAsync<string>(new CacheKey("a"), null, Ct);
        await cache.Received(1).GetCacheEntryAsync<string>(new CacheKey("b"), null, Ct);
        await cache.Received(1).ContainsAsync<string>(new CacheKey("c"), Ct);
        await cache.Received(1).RemoveAsync<string>(new CacheKey("c"), Ct);
    }

    [Fact]
    public async Task A_capable_typed_cache_answers_every_sealed_member()
    {
        using var owner = InMemoryMultilayer.Cache();
        ICache<string> cache = new Cache<string>(owner, new PrefixCacheKeyStrategy("app"));

        (await cache.GetOrAddAsync(Key("a"), Load, Ct)).Should().Be("v");
        (await cache.GetOrAddAsync(Key("b"), Load, Ttl, Ct)).Should().Be("v");
        (await cache.GetOrAddAsync(Key("c"), Load, Deadline, Ct)).Should().Be("v");
        (await cache.GetOrAddAsync(Key("d"), 1, LoadState, Ct)).Should().Be("v1");
        (await cache.GetOrAddAsync(Key("e"), 2, LoadState, Ttl, Ct)).Should().Be("v2");
        (await cache.GetOrAddAsync(Key("f"), 3, LoadState, Deadline, Ct)).Should().Be("v3");
        (await cache.GetOrAddAsync("g", 4, LoadState, Ct)).Should().Be("v4");
        (await cache.GetOrAddAsync("h", 5, LoadState, Ttl, Ct)).Should().Be("v5");
        (await cache.GetOrAddAsync("i", 6, LoadState, Deadline, Ct)).Should().Be("v6");
        (await cache.GetOrAddWithExpirationAsync("j", _ => Task.FromResult(new GeneratedValue<string>("g", Deadline)), Ct)).Should().Be("g");
        (await cache.GetAsync(Key("a"), Ct)).Should().Be("v");
        (await cache.ContainsAsync(Key("c"), Ct)).Should().BeTrue();
        (await cache.RemoveAsync(Key("c"), Ct)).Should().BeTrue();
        (await cache.ContainsAsync(Key("c"), Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task A_typed_cache_without_the_capabilities_gets_the_CacheKey_members()
    {
        var cache = Substitute.For<ICache<string>>();

        await cache.GetOrAddAsync(Key("a"), Load, Ct);
        await cache.GetOrAddAsync(Key("b"), Load, Ttl, Ct);
        await cache.GetOrAddAsync(Key("c"), Load, Deadline, Ct);
        await cache.GetOrAddAsync(Key("d"), 1, LoadState, Ct);
        await cache.GetOrAddAsync(Key("e"), 2, LoadState, Ttl, Ct);
        await cache.GetOrAddAsync(Key("f"), 3, LoadState, Deadline, Ct);
        await cache.GetOrAddAsync("g", 4, LoadState, Ct);
        await cache.GetOrAddAsync("h", 5, LoadState, Ttl, Ct);
        await cache.GetOrAddAsync("i", 6, LoadState, Deadline, Ct);
        (await cache.GetOrAddWithExpirationAsync("j", _ => Task.FromResult(new GeneratedValue<string>("g", Deadline)), Ct)).Should().Be("g");
        await cache.GetAsync(Key("a"), Ct);
        await cache.ContainsAsync(Key("c"), Ct);
        await cache.RemoveAsync(Key("c"), Ct);

        await cache.Received(1).GetOrAddAsync(new CacheKey("a"), Load, Ct);
        await cache.Received(1).GetOrAddAsync(new CacheKey("b"), Load, Ttl, Ct);
        await cache.Received(1).GetOrAddAsync(new CacheKey("c"), Load, Deadline, Ct);
        await cache.Received(1).GetOrAddAsync(new CacheKey("d"), Arg.Any<Func<CancellationToken, Task<string?>>>(), Ct);
        await cache.Received(1).GetOrAddAsync(new CacheKey("e"), Arg.Any<Func<CancellationToken, Task<string?>>>(), Ttl, Ct);
        await cache.Received(1).GetOrAddAsync(new CacheKey("f"), Arg.Any<Func<CancellationToken, Task<string?>>>(), Deadline, Ct);
        await cache.Received(1).GetOrAddAsync(new CacheKey("g"), Arg.Any<Func<CancellationToken, Task<string?>>>(), Ct);
        await cache.Received(1).GetOrAddAsync(new CacheKey("h"), Arg.Any<Func<CancellationToken, Task<string?>>>(), Ttl, Ct);
        await cache.Received(1).GetOrAddAsync(new CacheKey("i"), Arg.Any<Func<CancellationToken, Task<string?>>>(), Deadline, Ct);
        await cache.Received(1).SetAsync(new CacheKey("j"), "g", Deadline, Ct);
        await cache.Received(1).GetAsync(new CacheKey("a"), Ct);
        await cache.Received(1).ContainsAsync(new CacheKey("c"), Ct);
        await cache.Received(1).RemoveAsync(new CacheKey("c"), Ct);
    }

    [Fact]
    public async Task A_capable_hash_cache_answers_every_sealed_member()
    {
        using var owner = InMemoryMultilayer.HashCache();
        IHashCache cache = owner;

        (await cache.GetOrAddAsync(Key("a"), LoadHash, null, Ct))["f"].Should().Be("v");
        (await cache.GetOrAddAsync(Key("b"), LoadHash, Ttl, null, Ct))["f"].Should().Be("v");
        (await cache.GetOrAddAsync(Key("c"), LoadHash, Deadline, null, Ct))["f"].Should().Be("v");
        (await cache.GetOrAddAsync(Key("d"), LoadHash, Deadline, HashCacheSetOption.KeyReplace, null, Ct))["f"].Should().Be("v");
        (await cache.GetItemAsync<string>(Key("a"), "f", null, Ct)).Should().Be("v");
        (await cache.GetAsync<string>(Key("b"), null, Ct))["f"].Should().Be("v");
        (await cache.GetCacheEntryAsync<string>(Key("c"), null, Ct)).Found.Should().BeTrue();
        (await cache.ContainsAsync<string>(Key("d"), Ct)).Should().BeTrue();
        (await cache.RemoveAsync<string>(Key("d"), Ct)).Should().BeTrue();
        (await cache.ContainsAsync<string>(Key("d"), Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task A_hash_cache_without_the_capabilities_gets_the_CacheKey_members()
    {
        var cache = Substitute.For<IHashCache>();

        await cache.GetOrAddAsync(Key("a"), LoadHash, null, Ct);
        await cache.GetOrAddAsync(Key("b"), LoadHash, Ttl, null, Ct);
        await cache.GetOrAddAsync(Key("c"), LoadHash, Deadline, null, Ct);
        await cache.GetOrAddAsync(Key("d"), LoadHash, Deadline, HashCacheSetOption.KeyReplace, null, Ct);
        await cache.GetItemAsync<string>(Key("a"), "f", null, Ct);
        await cache.GetAsync<string>(Key("b"), null, Ct);
        await cache.GetCacheEntryAsync<string>(Key("c"), null, Ct);
        await cache.ContainsAsync<string>(Key("d"), Ct);
        await cache.RemoveAsync<string>(Key("d"), Ct);

        await cache.Received(1).GetOrAddAsync(new CacheKey("a"), LoadHash, null, Ct);
        await cache.Received(1).GetOrAddAsync(new CacheKey("b"), LoadHash, Ttl, null, Ct);
        await cache.Received(1).GetOrAddAsync(new CacheKey("c"), LoadHash, Deadline, null, Ct);
        await cache.Received(1).GetOrAddAsync(new CacheKey("d"), LoadHash, Deadline, HashCacheSetOption.KeyReplace, null, Ct);
        await cache.Received(1).GetItemAsync<string>(new CacheKey("a"), "f", null, Ct);
        await cache.Received(1).GetAsync<string>(new CacheKey("b"), null, Ct);
        await cache.Received(1).GetCacheEntryAsync<string>(new CacheKey("c"), null, Ct);
        await cache.Received(1).ContainsAsync<string>(new CacheKey("d"), Ct);
        await cache.Received(1).RemoveAsync<string>(new CacheKey("d"), Ct);
    }

    [Fact]
    public async Task A_capable_typed_hash_cache_answers_every_sealed_member()
    {
        using var owner = InMemoryMultilayer.HashCache();
        IHashCache<string> cache = new HashCache<string>(owner, new PrefixCacheKeyStrategy("app"));

        (await cache.GetOrAddAsync(Key("a"), LoadHash, Ct))["f"].Should().Be("v");
        (await cache.GetOrAddAsync(Key("b"), LoadHash, Ttl, Ct))["f"].Should().Be("v");
        (await cache.GetOrAddAsync(Key("c"), LoadHash, Deadline, Ct))["f"].Should().Be("v");
        (await cache.GetItemAsync(Key("a"), "f", Ct)).Should().Be("v");
        (await cache.GetAsync(Key("b"), Ct))["f"].Should().Be("v");
        (await cache.GetCacheEntryAsync(Key("c"), Ct)).Found.Should().BeTrue();
        (await cache.ContainsAsync(Key("c"), Ct)).Should().BeTrue();
        (await cache.RemoveAsync(Key("c"), Ct)).Should().BeTrue();
        (await cache.ContainsAsync(Key("c"), Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task A_typed_hash_cache_without_the_capabilities_gets_the_CacheKey_members()
    {
        var cache = Substitute.For<IHashCache<string>>();

        await cache.GetOrAddAsync(Key("a"), LoadHash, Ct);
        await cache.GetOrAddAsync(Key("b"), LoadHash, Ttl, Ct);
        await cache.GetOrAddAsync(Key("c"), LoadHash, Deadline, Ct);
        await cache.GetItemAsync(Key("a"), "f", Ct);
        await cache.GetAsync(Key("b"), Ct);
        await cache.GetCacheEntryAsync(Key("c"), Ct);
        await cache.ContainsAsync(Key("c"), Ct);
        await cache.RemoveAsync(Key("c"), Ct);

        await cache.Received(1).GetOrAddAsync(new CacheKey("a"), LoadHash, Ct);
        await cache.Received(1).GetOrAddAsync(new CacheKey("b"), LoadHash, Ttl, Ct);
        await cache.Received(1).GetOrAddAsync(new CacheKey("c"), LoadHash, Deadline, Ct);
        await cache.Received(1).GetItemAsync(new CacheKey("a"), "f", Ct);
        await cache.Received(1).GetAsync(new CacheKey("b"), Ct);
        await cache.Received(1).GetCacheEntryAsync(new CacheKey("c"), Ct);
        await cache.Received(1).ContainsAsync(new CacheKey("c"), Ct);
        await cache.Received(1).RemoveAsync(new CacheKey("c"), Ct);
    }

    private static Span<char> Key(string key) => key.ToCharArray();
}
