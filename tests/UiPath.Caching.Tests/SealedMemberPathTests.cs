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
    private static readonly Func<int, CancellationToken, Task<IDictionary<string, string?>>> LoadHashState = static (n, _) => Task.FromResult<IDictionary<string, string?>>(new Dictionary<string, string?> { ["f"] = $"v{n}" });
    private static readonly Func<int, CancellationToken, Task<GeneratedValue<string>>> LoadGenerated = static (n, _) => Task.FromResult(new GeneratedValue<string>($"g{n}", Deadline));
    private static readonly Func<string, int[], CancellationToken, Task<KeyValuePair<int, string?>[]>> LoadBatch = static (prefix, states, _) => Task.FromResult(states.Select(s => new KeyValuePair<int, string?>(s, $"{prefix}{s}")).ToArray());

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

    [Fact]
    public async Task A_capable_cache_answers_the_argument_and_state_members()
    {
        using var owner = InMemoryMultilayer.Cache();
        ICache cache = owner;
        ICache<string> typed = new Cache<string>(owner, new PrefixCacheKeyStrategy("app"));

        (await cache.GetOrAddAsync(Entries("a"), "p", LoadBatch, null, Ct)).Single().Value.Should().Be("p1");
        (await cache.GetOrAddAsync(Entries("b"), "p", LoadBatch, Ttl, null, Ct)).Single().Value.Should().Be("p1");
        (await cache.GetOrAddAsync(Entries("c"), "p", LoadBatch, Deadline, null, Ct)).Single().Value.Should().Be("p1");
        (await cache.GetOrAddWithExpirationAsync("d", 1, LoadGenerated, null, Ct)).Should().Be("g1");
        (await typed.GetOrAddAsync(Entries("a"), "q", LoadBatch, Ct)).Single().Value.Should().Be("q1");
        (await typed.GetOrAddAsync(Entries("b"), "q", LoadBatch, Ttl, Ct)).Single().Value.Should().Be("q1");
        (await typed.GetOrAddAsync(Entries("c"), "q", LoadBatch, Deadline, Ct)).Single().Value.Should().Be("q1");
        (await typed.GetOrAddWithExpirationAsync("d", 2, LoadGenerated, Ct)).Should().Be("g2");
    }

    [Fact]
    public async Task A_cache_without_the_capabilities_gets_the_closure_members_for_the_argument_and_state_members()
    {
        var cache = Substitute.For<ICache>();
        var typed = Substitute.For<ICache<string>>();

        await cache.GetOrAddAsync(Entries("a"), "p", LoadBatch, null, Ct);
        await cache.GetOrAddAsync(Entries("b"), "p", LoadBatch, Ttl, null, Ct);
        await cache.GetOrAddAsync(Entries("c"), "p", LoadBatch, Deadline, null, Ct);
        (await cache.GetOrAddWithExpirationAsync("d", 1, LoadGenerated, null, Ct)).Should().Be("g1");
        await typed.GetOrAddAsync(Entries("a"), "q", LoadBatch, Ct);
        await typed.GetOrAddAsync(Entries("b"), "q", LoadBatch, Ttl, Ct);
        await typed.GetOrAddAsync(Entries("c"), "q", LoadBatch, Deadline, Ct);
        (await typed.GetOrAddWithExpirationAsync("d", 2, LoadGenerated, Ct)).Should().Be("g2");

        await cache.Received(1).GetOrAddAsync(Arg.Any<KeyValuePair<CacheKey, int>[]>(), Arg.Any<Func<int[], CancellationToken, Task<KeyValuePair<int, string?>[]>>>(), null, Ct);
        await cache.Received(1).GetOrAddAsync(Arg.Any<KeyValuePair<CacheKey, int>[]>(), Arg.Any<Func<int[], CancellationToken, Task<KeyValuePair<int, string?>[]>>>(), Ttl, null, Ct);
        await cache.Received(1).GetOrAddAsync(Arg.Any<KeyValuePair<CacheKey, int>[]>(), Arg.Any<Func<int[], CancellationToken, Task<KeyValuePair<int, string?>[]>>>(), Deadline, null, Ct);
        await cache.Received(1).SetAsync<string>(new CacheKey("d"), "g1", Deadline, null, Ct);
        await typed.Received(1).GetOrAddAsync(Arg.Any<KeyValuePair<CacheKey, int>[]>(), Arg.Any<Func<int[], CancellationToken, Task<KeyValuePair<int, string?>[]>>>(), Ct);
        await typed.Received(1).GetOrAddAsync(Arg.Any<KeyValuePair<CacheKey, int>[]>(), Arg.Any<Func<int[], CancellationToken, Task<KeyValuePair<int, string?>[]>>>(), Ttl, Ct);
        await typed.Received(1).GetOrAddAsync(Arg.Any<KeyValuePair<CacheKey, int>[]>(), Arg.Any<Func<int[], CancellationToken, Task<KeyValuePair<int, string?>[]>>>(), Deadline, Ct);
        await typed.Received(1).SetAsync(new CacheKey("d"), "g2", Deadline, Ct);
    }

    [Fact]
    public async Task A_capable_hash_cache_answers_the_state_members()
    {
        using var owner = InMemoryMultilayer.HashCache();
        IHashCache cache = owner;
        IHashCache<string> typed = new HashCache<string>(owner, new PrefixCacheKeyStrategy("app"));

        (await cache.GetOrAddAsync("a", 1, LoadHashState, null, Ct))["f"].Should().Be("v1");
        (await cache.GetOrAddAsync("a", 9, LoadHashState, null, Ct))["f"].Should().Be("v1", "a hit");
        (await cache.GetOrAddAsync("b", 2, LoadHashState, Ttl, null, Ct))["f"].Should().Be("v2");
        (await cache.GetOrAddAsync("c", 3, LoadHashState, Deadline, null, Ct))["f"].Should().Be("v3");
        (await cache.GetOrAddAsync("d", 4, LoadHashState, Deadline, HashCacheSetOption.KeyReplace, null, Ct))["f"].Should().Be("v4");
        (await cache.GetOrAddAsync(Key("e"), 5, LoadHashState, null, Ct))["f"].Should().Be("v5");
        (await cache.GetOrAddAsync(Key("f"), 6, LoadHashState, Ttl, null, Ct))["f"].Should().Be("v6");
        (await cache.GetOrAddAsync(Key("g"), 7, LoadHashState, Deadline, null, Ct))["f"].Should().Be("v7");
        (await cache.GetOrAddAsync(Key("h"), 8, LoadHashState, Deadline, HashCacheSetOption.KeyReplace, null, Ct))["f"].Should().Be("v8");
        (await typed.GetOrAddAsync("a", 1, LoadHashState, Ct))["f"].Should().Be("v1");
        (await typed.GetOrAddAsync("b", 2, LoadHashState, Ttl, Ct))["f"].Should().Be("v2");
        (await typed.GetOrAddAsync("c", 3, LoadHashState, Deadline, Ct))["f"].Should().Be("v3");
        (await typed.GetOrAddAsync(Key("e"), 5, LoadHashState, Ct))["f"].Should().Be("v5");
        (await typed.GetOrAddAsync(Key("f"), 6, LoadHashState, Ttl, Ct))["f"].Should().Be("v6");
        (await typed.GetOrAddAsync(Key("g"), 7, LoadHashState, Deadline, Ct))["f"].Should().Be("v7");
    }

    [Fact]
    public async Task A_hash_cache_without_the_capabilities_gets_the_closure_members_for_the_state_members()
    {
        var cache = Substitute.For<IHashCache>();
        var typed = Substitute.For<IHashCache<string>>();

        await cache.GetOrAddAsync("a", 1, LoadHashState, null, Ct);
        await cache.GetOrAddAsync("b", 2, LoadHashState, Ttl, null, Ct);
        await cache.GetOrAddAsync("c", 3, LoadHashState, Deadline, null, Ct);
        await cache.GetOrAddAsync("d", 4, LoadHashState, Deadline, HashCacheSetOption.KeyReplace, null, Ct);
        await cache.GetOrAddAsync(Key("e"), 5, LoadHashState, null, Ct);
        await cache.GetOrAddAsync(Key("f"), 6, LoadHashState, Ttl, null, Ct);
        await cache.GetOrAddAsync(Key("g"), 7, LoadHashState, Deadline, null, Ct);
        await cache.GetOrAddAsync(Key("h"), 8, LoadHashState, Deadline, HashCacheSetOption.KeyReplace, null, Ct);
        await typed.GetOrAddAsync("a", 1, LoadHashState, Ct);
        await typed.GetOrAddAsync("b", 2, LoadHashState, Ttl, Ct);
        await typed.GetOrAddAsync("c", 3, LoadHashState, Deadline, Ct);
        await typed.GetOrAddAsync(Key("e"), 5, LoadHashState, Ct);
        await typed.GetOrAddAsync(Key("f"), 6, LoadHashState, Ttl, Ct);
        await typed.GetOrAddAsync(Key("g"), 7, LoadHashState, Deadline, Ct);

        await cache.Received(1).GetOrAddAsync(new CacheKey("a"), Arg.Any<Func<CancellationToken, Task<IDictionary<string, string?>>>>(), null, Ct);
        await cache.Received(1).GetOrAddAsync(new CacheKey("b"), Arg.Any<Func<CancellationToken, Task<IDictionary<string, string?>>>>(), Ttl, null, Ct);
        await cache.Received(1).GetOrAddAsync(new CacheKey("c"), Arg.Any<Func<CancellationToken, Task<IDictionary<string, string?>>>>(), Deadline, null, Ct);
        await cache.Received(1).GetOrAddAsync(new CacheKey("d"), Arg.Any<Func<CancellationToken, Task<IDictionary<string, string?>>>>(), Deadline, HashCacheSetOption.KeyReplace, null, Ct);
        await cache.Received(1).GetOrAddAsync(new CacheKey("e"), Arg.Any<Func<CancellationToken, Task<IDictionary<string, string?>>>>(), null, Ct);
        await cache.Received(1).GetOrAddAsync(new CacheKey("f"), Arg.Any<Func<CancellationToken, Task<IDictionary<string, string?>>>>(), Ttl, null, Ct);
        await cache.Received(1).GetOrAddAsync(new CacheKey("g"), Arg.Any<Func<CancellationToken, Task<IDictionary<string, string?>>>>(), Deadline, null, Ct);
        await cache.Received(1).GetOrAddAsync(new CacheKey("h"), Arg.Any<Func<CancellationToken, Task<IDictionary<string, string?>>>>(), Deadline, HashCacheSetOption.KeyReplace, null, Ct);
        await typed.Received(1).GetOrAddAsync(new CacheKey("a"), Arg.Any<Func<CancellationToken, Task<IDictionary<string, string?>>>>(), Ct);
        await typed.Received(1).GetOrAddAsync(new CacheKey("b"), Arg.Any<Func<CancellationToken, Task<IDictionary<string, string?>>>>(), Ttl, Ct);
        await typed.Received(1).GetOrAddAsync(new CacheKey("c"), Arg.Any<Func<CancellationToken, Task<IDictionary<string, string?>>>>(), Deadline, Ct);
        await typed.Received(1).GetOrAddAsync(new CacheKey("e"), Arg.Any<Func<CancellationToken, Task<IDictionary<string, string?>>>>(), Ct);
        await typed.Received(1).GetOrAddAsync(new CacheKey("f"), Arg.Any<Func<CancellationToken, Task<IDictionary<string, string?>>>>(), Ttl, Ct);
        await typed.Received(1).GetOrAddAsync(new CacheKey("g"), Arg.Any<Func<CancellationToken, Task<IDictionary<string, string?>>>>(), Deadline, Ct);
    }

    private static KeyValuePair<CacheKey, int>[] Entries(string key) => [new(key, 1)];

    private static Span<char> Key(string key) => key.ToCharArray();
}
