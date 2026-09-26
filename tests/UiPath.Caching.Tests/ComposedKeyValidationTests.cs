using UiPath.Caching.Tests.Fakes;

namespace UiPath.Caching.Tests;

/// <summary>A key strategy that composes an empty key is refused before any tier is touched, on every path.</summary>
public class ComposedKeyValidationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_cache_refuses_an_empty_composed_key_on_read_and_write()
    {
        using var cache = InMemoryMultilayer.Cache(new InMemoryCacheOptions { CacheKeyStrategy = new EmptyStrategy() });

        var read = async () => await cache.GetAsync<string>("k", policy: null, Ct);
        var write = async () => await cache.SetAsync<string>("k", "v", policy: null, Ct);
        var bySpan = () => SpanReads.Read<string>(cache, "k", Ct);

        await read.Should().ThrowAsync<InvalidOperationException>();
        await write.Should().ThrowAsync<InvalidOperationException>();
        bySpan.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task A_hash_cache_refuses_an_empty_composed_key_on_read_and_write()
    {
        using var cache = InMemoryMultilayer.HashCache(new InMemoryCacheOptions { CacheKeyStrategy = new EmptyStrategy() });

        var read = async () => await cache.GetAsync<string>("k", policy: null, Ct);
        var write = async () => await cache.SetAsync<string>("k", new Dictionary<string, string?> { ["f"] = "v" }, policy: null, Ct);
        var itemBySpan = () => SpanReads.ReadItem<string>(cache, "k", "f", Ct);

        await read.Should().ThrowAsync<InvalidOperationException>();
        await write.Should().ThrowAsync<InvalidOperationException>();
        itemBySpan.Should().Throw<InvalidOperationException>();
    }

    private sealed class EmptyStrategy : ICacheKeyStrategy
    {
        public CacheKey GetCacheKey<T>(CacheKey key) => default;

        public bool TryGetCacheKey<T>(ReadOnlySpan<char> key, Span<char> destination, out int written)
        {
            written = 0;
            return true;
        }
    }
}
