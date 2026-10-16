namespace UiPath.Caching.Tests;

/// <summary>Every call shape the removed 3.0 extensions served compiles on each public cache class, which does not inherit the interfaces' sealed members.</summary>
public class ConcreteCacheCallShapeTests
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);
    private static readonly DateTimeOffset Deadline = DateTimeOffset.UtcNow.AddMinutes(5);
    private static readonly Func<CancellationToken, Task<string?>> Load = static _ => Task.FromResult<string?>("v");
    private static readonly Func<int, CancellationToken, Task<string?>> LoadState = static (n, _) => Task.FromResult<string?>($"v{n}");
    private static readonly Func<CancellationToken, Task<GeneratedValue<string>>> LoadGenerated = static _ => Task.FromResult(new GeneratedValue<string>("g", Deadline));
    private static readonly Func<CancellationToken, Task<IDictionary<string, string?>>> LoadHash = static _ => Task.FromResult<IDictionary<string, string?>>(new Dictionary<string, string?> { ["f"] = "v" });

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Cache_of_T_takes_every_3_0_extension_shape()
    {
        var cache = new Cache<string>(NullCache.Instance);

        (await cache.GetAsync(Key(), Ct)).Should().BeNull();
        (await cache.GetOrAddAsync(Key(), Load, Ct)).Should().Be("v");
        (await cache.GetOrAddAsync(Key(), Load, Ttl, Ct)).Should().Be("v");
        (await cache.GetOrAddAsync(Key(), Load, Deadline, Ct)).Should().Be("v");
        (await cache.GetOrAddAsync(Key(), 1, LoadState, Ct)).Should().Be("v1");
        (await cache.GetOrAddAsync(Key(), 2, LoadState, Ttl, Ct)).Should().Be("v2");
        (await cache.GetOrAddAsync(Key(), 3, LoadState, Deadline, Ct)).Should().Be("v3");
        (await cache.GetOrAddAsync("k", 4, LoadState, Ct)).Should().Be("v4");
        (await cache.GetOrAddAsync("k", 5, LoadState, Ttl, Ct)).Should().Be("v5");
        (await cache.GetOrAddAsync("k", 6, LoadState, Deadline, Ct)).Should().Be("v6");
        (await cache.GetOrAddWithExpirationAsync("k", LoadGenerated, Ct)).Should().Be("g");
        (await cache.ContainsAsync(Key(), Ct)).Should().BeFalse();
        (await cache.RemoveAsync(Key(), Ct)).Should().BeTrue();
    }

    [Fact]
    public async Task HashCache_of_T_takes_every_3_0_extension_shape()
    {
        var cache = new HashCache<string>(NullHashCache.Instance);

        (await cache.GetAsync(Key(), Ct)).Should().BeEmpty();
        (await cache.GetItemAsync(Key(), "f", Ct)).Should().BeNull();
        (await cache.GetCacheEntryAsync(Key(), Ct)).Found.Should().BeFalse();
        (await cache.GetOrAddAsync(Key(), LoadHash, Ct)).Should().ContainKey("f");
        (await cache.GetOrAddAsync(Key(), LoadHash, Ttl, Ct)).Should().ContainKey("f");
        (await cache.GetOrAddAsync(Key(), LoadHash, Deadline, Ct)).Should().ContainKey("f");
        (await cache.ContainsAsync(Key(), Ct)).Should().BeFalse();
        (await cache.RemoveAsync(Key(), Ct)).Should().BeTrue();
    }

    [Fact]
    public async Task NullCache_takes_every_3_0_extension_shape()
    {
        var cache = NullCache.Instance;

        (await cache.GetAsync<string>(Key(), null, Ct)).Should().BeNull();
        (await cache.GetCacheEntryAsync<string>(Key(), null, Ct)).Found.Should().BeFalse();
        (await cache.GetOrAddAsync(Key(), Load, null, Ct)).Should().Be("v");
        (await cache.GetOrAddAsync(Key(), Load, Ttl, null, Ct)).Should().Be("v");
        (await cache.GetOrAddAsync(Key(), Load, Deadline, null, Ct)).Should().Be("v");
        (await cache.GetOrAddAsync(Key(), 1, LoadState, null, Ct)).Should().Be("v1");
        (await cache.GetOrAddAsync(Key(), 2, LoadState, Ttl, null, Ct)).Should().Be("v2");
        (await cache.GetOrAddAsync(Key(), 3, LoadState, Deadline, null, Ct)).Should().Be("v3");
        (await cache.GetOrAddAsync<string, int>("k", 4, LoadState, null, Ct)).Should().Be("v4");
        (await cache.GetOrAddAsync<string, int>("k", 5, LoadState, Ttl, null, Ct)).Should().Be("v5");
        (await cache.GetOrAddAsync<string, int>("k", 6, LoadState, Deadline, null, Ct)).Should().Be("v6");
        (await cache.GetOrAddAsync<string, int>("k", 7, LoadState, Ct)).Should().Be("v7");
        (await cache.GetOrAddWithExpirationAsync<string>("k", LoadGenerated, null, Ct)).Should().Be("g");
        (await cache.GetOrAddWithExpirationAsync<string>("k", LoadGenerated, Ct)).Should().Be("g");
        (await cache.ContainsAsync<string>(Key(), Ct)).Should().BeFalse();
        (await cache.RemoveAsync<string>(Key(), Ct)).Should().BeTrue();
    }

    [Fact]
    public async Task NullHashCache_takes_every_3_0_extension_shape()
    {
        var cache = NullHashCache.Instance;

        (await cache.GetAsync<string>(Key(), null, Ct)).Should().BeEmpty();
        (await cache.GetItemAsync<string>(Key(), "f", null, Ct)).Should().BeNull();
        (await cache.GetCacheEntryAsync<string>(Key(), null, Ct)).Found.Should().BeFalse();
        (await cache.GetOrAddAsync(Key(), LoadHash, null, Ct)).Should().ContainKey("f");
        (await cache.GetOrAddAsync(Key(), LoadHash, Ttl, null, Ct)).Should().ContainKey("f");
        (await cache.GetOrAddAsync(Key(), LoadHash, Deadline, null, Ct)).Should().ContainKey("f");
        (await cache.GetOrAddAsync(Key(), LoadHash, Deadline, HashCacheSetOption.HashReplace, null, Ct)).Should().ContainKey("f");
        (await cache.ContainsAsync<string>(Key(), Ct)).Should().BeFalse();
        (await cache.RemoveAsync<string>(Key(), Ct)).Should().BeTrue();
    }

    private static Span<char> Key() => "k".ToCharArray();
}
