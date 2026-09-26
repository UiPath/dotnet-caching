using UiPath.Caching.Tests.Fakes;

namespace UiPath.Caching.Tests;

/// <summary>The <c>Span&lt;char&gt;</c> reads answer as the <see cref="CacheKey"/> reads do, over real in-memory tiers.</summary>
public class SpanKeyReadTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_cache_reads_by_span_what_it_wrote_by_key()
    {
        using var cache = InMemoryMultilayer.Cache();
        (await cache.SetAsync<string>(" User:42 ", "v", policy: null, Ct)).Should().BeTrue();

        SpanReads.Read<string>(cache, "User:42", Ct).Should().Be("v");
        SpanReads.Read<string>(cache, " user:42", Ct).Should().Be("v");
        SpanReads.Read<string>(cache, "user:43", Ct).Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_cache_rejects_an_empty_span_as_it_rejects_an_empty_key(string key)
    {
        using var cache = InMemoryMultilayer.Cache();

        var act = () => SpanReads.Read<string>(cache, key, Ct);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task A_cache_reads_by_span_a_key_longer_than_the_stack_buffer()
    {
        using var cache = InMemoryMultilayer.Cache();
        var key = string.Concat(Enumerable.Repeat("k", 300));
        (await cache.SetAsync<string>(key, "v", policy: null, Ct)).Should().BeTrue();

        SpanReads.Read<string>(cache, key, Ct).Should().Be("v");
    }

    [Fact]
    public async Task A_cache_with_a_prefix_strategy_reads_by_span_what_it_wrote_by_key()
    {
        using var cache = InMemoryMultilayer.Cache(new InMemoryCacheOptions { CacheKeyStrategy = new PrefixCacheKeyStrategy("tier") });
        (await cache.SetAsync<string>("User:42", "v", policy: null, Ct)).Should().BeTrue();

        SpanReads.Read<string>(cache, "User:42", Ct).Should().Be("v");
    }

    [Fact]
    public async Task A_cache_with_a_custom_strategy_reads_by_span_what_it_wrote_by_key()
    {
        using var cache = InMemoryMultilayer.Cache(new InMemoryCacheOptions { CacheKeyStrategy = new SuffixStrategy() });
        (await cache.SetAsync<string>("User:42", "v", policy: null, Ct)).Should().BeTrue();

        SpanReads.Read<string>(cache, "User:42", Ct).Should().Be("v");
    }

    [Fact]
    public async Task A_cache_with_a_strategy_that_declines_the_span_reads_by_span_what_it_wrote_by_key()
    {
        using var cache = InMemoryMultilayer.Cache(new InMemoryCacheOptions { CacheKeyStrategy = new DecliningStrategy() });
        (await cache.SetAsync<string>("User:42", "v", policy: null, Ct)).Should().BeTrue();

        SpanReads.Read<string>(cache, "User:42", Ct).Should().Be("v");
    }

    [Fact]
    public async Task A_typed_cache_with_a_prefix_strategy_reads_by_span_what_it_wrote_by_key()
    {
        using var cache = InMemoryMultilayer.Cache(new InMemoryCacheOptions { CacheKeyStrategy = new PrefixCacheKeyStrategy("tier") });
        var typed = new Cache<string>(cache, new PrefixCacheKeyStrategy("app"));
        (await typed.SetAsync(" User:42 ", "v", Ct)).Should().BeTrue();

        SpanReads.Read(typed, "User:42", Ct).Should().Be("v");
        (await cache.GetAsync<string>("app:user:42", policy: null, Ct)).Should().Be("v");
    }

    [Fact]
    public async Task A_typed_cache_with_a_custom_strategy_reads_by_span_what_it_wrote_by_key()
    {
        using var cache = InMemoryMultilayer.Cache();
        var typed = new Cache<string>(cache, new SuffixStrategy());
        (await typed.SetAsync("User:42", "v", Ct)).Should().BeTrue();

        SpanReads.Read(typed, "User:42", Ct).Should().Be("v");
    }

    [Fact]
    public async Task An_outside_implementation_reads_by_span_through_the_default_body()
    {
        var cache = new DictionaryCache();
        (await cache.SetAsync<string>("k", "v", policy: null, Ct)).Should().BeTrue();

        SpanReads.Read<string>(cache, " K ", Ct).Should().Be("v");
    }

    [Fact]
    public async Task A_hash_cache_reads_by_span_what_it_wrote_by_key()
    {
        using var cache = InMemoryMultilayer.HashCache();
        var values = new Dictionary<string, string?> { ["name"] = "n", ["Other"] = "o" };
        (await cache.SetAsync<string>(" User:42 ", values, policy: null, Ct)).Should().BeTrue();

        SpanReads.ReadItem<string>(cache, "User:42", "name", Ct).Should().Be("n");
        SpanReads.ReadItem<string>(cache, "User:42", "NAME", Ct).Should().Be(await cache.GetItemAsync<string>("User:42", "NAME", policy: null, Ct));
        SpanReads.ReadItem<string>(cache, "User:42", "missing", Ct).Should().BeNull();
        SpanReads.ReadItem<string>(cache, "User:43", "name", Ct).Should().BeNull();
        SpanReads.ReadAll<string>(cache, "User:42", Ct).Should().BeEquivalentTo(values);
        SpanReads.ReadAll<string>(cache, "User:43", Ct).Should().BeEmpty();
    }

    [Fact]
    public async Task A_typed_hash_cache_with_a_prefix_strategy_reads_by_span_what_it_wrote_by_key()
    {
        using var cache = InMemoryMultilayer.HashCache(new InMemoryCacheOptions { CacheKeyStrategy = new PrefixCacheKeyStrategy("tier") });
        var typed = new HashCache<string>(cache, new PrefixCacheKeyStrategy("app"));
        var values = new Dictionary<string, string?> { ["name"] = "n" };
        (await typed.SetAsync("User:42", values, Ct)).Should().BeTrue();

        SpanReads.ReadItem(typed, "User:42", "name", Ct).Should().Be("n");
        SpanReads.ReadAll(typed, "User:42", Ct).Should().BeEquivalentTo(values);
        (await cache.GetItemAsync<string>("app:user:42", "name", policy: null, Ct)).Should().Be("n");
    }

    [Fact]
    public async Task A_cancelled_span_read_throws_as_the_key_read_does_even_on_a_warm_hit()
    {
        using var cache = InMemoryMultilayer.Cache();
        using var hash = InMemoryMultilayer.HashCache();
        (await cache.SetAsync<string>("user:42", "v", policy: null, Ct)).Should().BeTrue();
        (await hash.SetAsync<string>("user:42", new Dictionary<string, string?> { ["name"] = "n" }, policy: null, Ct)).Should().BeTrue();
        var cancelled = new CancellationToken(canceled: true);

        var byKey = () => cache.GetAsync<string>("user:42", policy: null, cancelled);
        var bySpan = () => SpanReads.Read<string>(cache, "user:42", cancelled);
        var itemBySpan = () => SpanReads.ReadItem<string>(hash, "user:42", "name", cancelled);
        var allBySpan = () => SpanReads.ReadAll<string>(hash, "user:42", cancelled);

        byKey.Should().Throw<OperationCanceledException>();
        bySpan.Should().Throw<OperationCanceledException>();
        itemBySpan.Should().Throw<OperationCanceledException>();
        allBySpan.Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public void A_cancelled_span_read_is_cancelled_before_the_strategy_runs()
    {
        using var cache = InMemoryMultilayer.Cache(new InMemoryCacheOptions { CacheKeyStrategy = new ThrowingStrategy() });
        using var hash = InMemoryMultilayer.HashCache(new InMemoryCacheOptions { CacheKeyStrategy = new ThrowingStrategy() });
        var cancelled = new CancellationToken(canceled: true);

        var bySpan = () => SpanReads.Read<string>(cache, "user:42", cancelled);
        var itemBySpan = () => SpanReads.ReadItem<string>(hash, "user:42", "f", cancelled);
        var allBySpan = () => SpanReads.ReadAll<string>(hash, "user:42", cancelled);

        bySpan.Should().Throw<OperationCanceledException>();
        itemBySpan.Should().Throw<OperationCanceledException>();
        allBySpan.Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public void An_empty_span_read_with_a_cancelled_token_fails_as_the_key_read_fails()
    {
        using var cache = InMemoryMultilayer.Cache();
        using var hash = InMemoryMultilayer.HashCache();
        var cancelled = new CancellationToken(canceled: true);

        var byKey = Record.Exception(() => cache.GetAsync<string>("", policy: null, cancelled).AsTask().GetAwaiter().GetResult());
        var bySpan = Record.Exception(() => SpanReads.Read<string>(cache, "", cancelled));
        var itemByKey = Record.Exception(() => hash.GetItemAsync<string>("", "f", policy: null, cancelled).AsTask().GetAwaiter().GetResult());
        var itemBySpan = Record.Exception(() => SpanReads.ReadItem<string>(hash, "", "f", cancelled));

        byKey.Should().BeOfType<ArgumentNullException>();
        bySpan.Should().BeOfType(byKey!.GetType());
        itemBySpan.Should().BeOfType(itemByKey!.GetType());
    }

    [Fact]
    public void The_null_caches_answer_a_span_read_with_nothing()
    {
        SpanReads.Read<string>(NullCache.Instance, "k", Ct).Should().BeNull();
        SpanReads.ReadItem<string>(NullHashCache.Instance, "k", "f", Ct).Should().BeNull();
        SpanReads.ReadAll<string>(NullHashCache.Instance, "k", Ct).Should().BeEmpty();
    }

#if NET9_0_OR_GREATER
    [Fact]
    public async Task A_warm_span_read_that_hits_locally_allocates_nothing()
    {
        using var cache = InMemoryMultilayer.Cache();
        (await cache.SetAsync<string>("user:42", "v", policy: null, Ct)).Should().BeTrue();

        AllocatedBy(() => SpanReads.Read<string>(cache, "user:42")).Should().Be(0);
        AllocatedBy(() => SpanReads.Read<string>(cache, " User:42 ")).Should().Be(0, "normalizing on the stack keeps the read on the fast path");
    }

    [Fact]
    public async Task A_warm_span_read_through_prefix_strategies_allocates_nothing()
    {
        using var cache = InMemoryMultilayer.Cache(new InMemoryCacheOptions { CacheKeyStrategy = new PrefixCacheKeyStrategy("tier") });
        var typed = new Cache<string>(cache, new PrefixCacheKeyStrategy("app"));
        (await typed.SetAsync("user:42", "v", Ct)).Should().BeTrue();

        AllocatedBy(() => SpanReads.Read(typed, " User:42 ")).Should().Be(0);
    }

    [Fact]
    public async Task A_warm_span_read_through_custom_strategies_that_compose_by_span_allocates_nothing()
    {
        using var cache = InMemoryMultilayer.Cache(new InMemoryCacheOptions { CacheKeyStrategy = new SuffixStrategy() });
        var typed = new Cache<string>(cache, new SuffixStrategy());
        (await typed.SetAsync("user:42", "v", Ct)).Should().BeTrue();

        AllocatedBy(() => SpanReads.Read(typed, "user:42")).Should().Be(0, "the library lowercases the strategy's output as WithName does on the key path");
    }

    [Fact]
    public async Task A_warm_span_hash_read_that_hits_locally_allocates_nothing()
    {
        using var cache = InMemoryMultilayer.HashCache();
        (await cache.SetAsync<string>("user:42", new Dictionary<string, string?> { ["name"] = "n" }, policy: null, Ct)).Should().BeTrue();

        AllocatedBy(() => SpanReads.ReadItem<string>(cache, "user:42", "name")).Should().Be(0);
        AllocatedBy(() => SpanReads.ReadAll<string>(cache, "user:42")).Should().Be(0);
    }

    /// <summary>Bytes the current thread allocates across a thousand warm calls.</summary>
    private static long AllocatedBy(Action read)
    {
        for (var i = 0; i < 1_000; i++)
        {
            read();
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1_000; i++)
        {
            read();
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
#endif

    /// <summary>Appends an upper-case suffix; the key path lowercases it through <c>WithName</c>, the span path through the library.</summary>
    private sealed class SuffixStrategy : ICacheKeyStrategy
    {
        public CacheKey GetCacheKey<T>(CacheKey key) => key.WithName(key.Name + "#S");

        public bool TryGetCacheKey<T>(ReadOnlySpan<char> key, Span<char> destination, out int written)
        {
            written = key.Length + 2;
            if (written > destination.Length)
            {
                written = 0;
                return false;
            }

            key.CopyTo(destination);
            "#S".AsSpan().CopyTo(destination[key.Length..]);
            return true;
        }
    }

    private sealed class ThrowingStrategy : ICacheKeyStrategy
    {
        public CacheKey GetCacheKey<T>(CacheKey key) => throw new NotSupportedException("the strategy was reached");

        public bool TryGetCacheKey<T>(ReadOnlySpan<char> key, Span<char> destination, out int written) => throw new NotSupportedException("the strategy was reached");
    }

    private sealed class DecliningStrategy : ICacheKeyStrategy
    {
        public CacheKey GetCacheKey<T>(CacheKey key) => key.WithName(key.Name + "#d");

        public bool TryGetCacheKey<T>(ReadOnlySpan<char> key, Span<char> destination, out int written)
        {
            written = 0;
            return false;
        }
    }
}
