using Moq;
using UiPath.Caching.Tests.Fakes;

namespace UiPath.Caching.Tests;

/// <summary><c>SetAsync</c> by <c>Span&lt;char&gt;</c> writes what the <see cref="CacheKey"/> forms write, and reaches a cache's span capability when it has one.</summary>
public class SpanKeyWriteTests
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);
    private static readonly DateTimeOffset Deadline = DateTimeOffset.UtcNow.AddMinutes(5);
    private static readonly IDictionary<string, string?> Fields = new Dictionary<string, string?> { ["f"] = "v" };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_bare_default_key_still_binds_to_the_CacheKey_overloads()
    {
        var cache = Substitute.For<ICache>();
        var typed = Substitute.For<ICache<string>>();
        var hash = Substitute.For<IHashCache>();
        var typedHash = Substitute.For<IHashCache<string>>();

        await cache.SetAsync<string>(default, "v", (CachePolicy?)null, Ct);
        await typed.SetAsync(default, "v", Ct);
        await hash.SetAsync<string>(default, Fields, (CachePolicy?)null, Ct);
        await typedHash.SetAsync(default, Fields, Ct);

        await cache.Received(1).SetAsync<string>(Arg.Any<CacheKey>(), "v", null, Ct);
        await typed.Received(1).SetAsync(Arg.Any<CacheKey>(), "v", Ct);
        await hash.Received(1).SetAsync<string>(Arg.Any<CacheKey>(), Fields, (CachePolicy?)null, Ct);
        await typedHash.Received(1).SetAsync(Arg.Any<CacheKey>(), Fields, Ct);
    }

    [Fact]
    public async Task A_bare_default_key_binds_to_the_CacheKey_overload_on_the_public_cache_classes()
    {
        var inner = Substitute.For<ICache>();
        var innerHash = Substitute.For<IHashCache>();

        await new Cache<string>(inner).SetAsync(default, "v", Ct);
        await new HashCache<string>(innerHash).SetAsync(default, Fields, Ct);
        await NullCache.Instance.SetAsync<string>(default, "v", (CachePolicy?)null, Ct);
        await NullHashCache.Instance.SetAsync<string>(default, Fields, (CachePolicy?)null, Ct);

        await inner.Received(1).SetAsync<string>(Arg.Any<CacheKey>(), "v", null, Ct);
        await innerHash.Received(1).SetAsync<string>(Arg.Any<CacheKey>(), Fields, null, Ct);
    }

    [Fact]
    public async Task A_cache_writes_by_span_what_it_reads_by_key()
    {
        using var cache = InMemoryMultilayer.Cache();

        (await cache.SetAsync<string>(Key(" User:1 "), "a", (CachePolicy?)null, Ct)).Should().BeTrue();
        (await cache.SetAsync<string>(Key("user:2"), "b", Ttl, null, Ct)).Should().BeTrue();
        (await cache.SetAsync<string>(Key("user:3"), "c", Deadline, null, Ct)).Should().BeTrue();

        (await cache.GetAsync<string>("user:1", policy: null, Ct)).Should().Be("a");
        (await cache.GetAsync<string>("user:2", policy: null, Ct)).Should().Be("b");
        (await cache.GetAsync<string>("user:3", policy: null, Ct)).Should().Be("c");
        (await cache.GetCacheEntryAsync<string>("user:3", policy: null, Ct)).Expiration.Should().BeCloseTo(Deadline, TimeSpan.FromSeconds(1));
        (await cache.GetCacheEntryAsync<string>("user:2", policy: null, Ct)).Expiration.Should().BeCloseTo(DateTimeOffset.UtcNow + Ttl, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_cache_rejects_an_empty_span_and_an_expiration_that_is_not_positive_as_the_key_forms_do()
    {
        using var cache = InMemoryMultilayer.Cache();

        var empty = async () => await cache.SetAsync<string>(Key("  "), "v", (CachePolicy?)null, Ct);
        var zero = async () => await cache.SetAsync<string>(Key("k"), "v", TimeSpan.Zero, null, Ct);
        var past = async () => await cache.SetAsync<string>(Key("k"), "v", DateTimeOffset.UtcNow.AddMinutes(-1), null, Ct);

        await empty.Should().ThrowAsync<ArgumentNullException>();
        await zero.Should().ThrowAsync<ArgumentOutOfRangeException>().WithParameterName("expiration");
        await past.Should().ThrowAsync<ArgumentOutOfRangeException>().WithParameterName("expiration");
    }

    [Fact]
    public async Task A_capable_cache_takes_the_span_write_itself()
    {
        var spy = new SpanWritingCache();
        ICache cache = spy;

        await cache.SetAsync<string>(Key("a"), "v", (CachePolicy?)null, Ct);
        await cache.SetAsync<string>(Key("b"), "v", Ttl, null, Ct);
        await cache.SetAsync<string>(Key("c"), "v", Deadline, null, Ct);

        spy.SpanWrites.Should().Equal("a", "b", "c");
        spy.SetCalls.Should().Be(0);
    }

    [Fact]
    public async Task A_cache_without_the_capability_gets_the_CacheKey_member_and_the_key_is_normalized()
    {
        var cache = new DictionaryCache();

        await ((ICache)cache).SetAsync<string>(Key(" User:42 "), "v", (CachePolicy?)null, Ct);
        await ((ICache)cache).SetAsync<string>(Key("User:43"), "v", Ttl, null, Ct);
        await ((ICache)cache).SetAsync<string>(Key("User:44"), "v", Deadline, null, Ct);

        cache.SetCalls.Should().Be(3);
        cache.Read<string>("user:42").Should().Be("v");
        cache.Read<string>("user:43").Should().Be("v");
        cache.Read<string>("user:44").Should().Be("v");
    }

    [Fact]
    public async Task A_proxied_cache_gets_the_span_write_through_its_CacheKey_member()
    {
        var substitute = Substitute.For<ICache>();
        var typed = Substitute.For<ICache<string>>();
        var moq = new Mock<ICache>(MockBehavior.Strict);
        moq.Setup(c => c.SetAsync<string>(new CacheKey("user:42"), "v", Deadline, null, It.IsAny<CancellationToken>())).Returns(new ValueTask<bool>(true));
        var moqTyped = new Mock<ICache<string>>(MockBehavior.Strict);
        moqTyped.Setup(c => c.SetAsync(new CacheKey("user:42"), "v", Ttl, It.IsAny<CancellationToken>())).Returns(new ValueTask<bool>(true));

        await substitute.SetAsync<string>(Key("user:42"), "v", Ttl, null, Ct);
        await typed.SetAsync(Key("user:42"), "v", Deadline, Ct);
        (await moq.Object.SetAsync<string>(Key("user:42"), "v", Deadline, null, Ct)).Should().BeTrue();
        (await moqTyped.Object.SetAsync(Key("user:42"), "v", Ttl, Ct)).Should().BeTrue();

        await substitute.Received(1).SetAsync<string>(new CacheKey("user:42"), "v", Ttl, null, Ct);
        await typed.Received(1).SetAsync(new CacheKey("user:42"), "v", Deadline, Ct);
        moq.Verify(c => c.SetAsync<string>(new CacheKey("user:42"), "v", Deadline, null, It.IsAny<CancellationToken>()), Times.Once);
        moqTyped.Verify(c => c.SetAsync(new CacheKey("user:42"), "v", Ttl, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_typed_cache_composes_its_key_and_hands_the_text_to_the_span_capability()
    {
        var spy = new SpanWritingCache();
        var typed = new Cache<string>(spy, new PrefixCacheKeyStrategy("app"));
        ICache<string> cache = typed;

        await cache.SetAsync(Key("user:1"), "v", Ct);
        await cache.SetAsync(Key("user:2"), "v", Ttl, Ct);
        await cache.SetAsync(Key("user:3"), "v", Deadline, Ct);

        spy.SpanWrites.Should().Equal("app:user:1", "app:user:2", "app:user:3");
        spy.SetCalls.Should().Be(0);
    }

    [Fact]
    public async Task A_typed_cache_whose_strategy_declines_the_span_writes_through_the_key_it_builds()
    {
        var spy = new SpanWritingCache();
        var typed = new Cache<string>(spy, new DecliningStrategy());
        ICache<string> cache = typed;

        await cache.SetAsync(Key("User:1"), "v", Ct);

        spy.SpanWrites.Should().BeEmpty();
        spy.SetCalls.Should().Be(1);
        spy.Read<string>("user:1#d").Should().Be("v");
    }

    [Fact]
    public async Task A_typed_cache_writes_by_span_what_it_reads_by_key()
    {
        using var cache = InMemoryMultilayer.Cache(new InMemoryCacheOptions { CacheKeyStrategy = new PrefixCacheKeyStrategy("tier") });
        var typed = new Cache<string>(cache, new PrefixCacheKeyStrategy("app"));

        (await typed.SetAsync(Key(" User:1 "), "a", Ct)).Should().BeTrue();
        (await typed.SetAsync(Key("user:2"), "b", Ttl, Ct)).Should().BeTrue();
        (await typed.SetAsync(Key("user:3"), "c", Deadline, Ct)).Should().BeTrue();

        (await typed.GetAsync("user:1", Ct)).Should().Be("a");
        (await typed.GetAsync("user:2", Ct)).Should().Be("b");
        (await typed.GetAsync("user:3", Ct)).Should().Be("c");
        (await cache.GetAsync<string>("app:user:1", policy: null, Ct)).Should().Be("a");
    }

    [Fact]
    public async Task A_hash_cache_writes_by_span_what_it_reads_by_key()
    {
        using var cache = InMemoryMultilayer.HashCache();

        (await cache.SetAsync<string>(Key(" User:1 "), Fields, (CachePolicy?)null, Ct)).Should().BeTrue();
        (await cache.SetAsync<string>(Key("user:2"), Fields, Ttl, null, Ct)).Should().BeTrue();
        (await cache.SetAsync<string>(Key("user:3"), Fields, Deadline, null, Ct)).Should().BeTrue();
        (await cache.SetAsync<string>(Key("user:4"), Fields, new HashCacheEntryOptions(TimeToLive: Ttl), null, Ct)).Should().BeTrue();

        foreach (var key in new[] { "user:1", "user:2", "user:3", "user:4" })
        {
            (await cache.GetItemAsync<string>(key, "f", policy: null, Ct)).Should().Be("v");
        }
    }

    [Fact]
    public async Task A_hash_cache_written_by_span_keeps_the_expiration_its_options_give()
    {
        using var cache = InMemoryMultilayer.HashCache();
        var deadline = DateTimeOffset.UtcNow.AddMinutes(3);

        (await cache.SetAsync<string>(Key("ttl"), Fields, new HashCacheEntryOptions(TimeToLive: TimeSpan.FromMinutes(3)), null, Ct)).Should().BeTrue();
        (await cache.SetAsync<string>(Key("at"), Fields, new HashCacheEntryOptions(ExpireTime: deadline), null, Ct)).Should().BeTrue();

        (await cache.GetCacheEntryAsync<string>("ttl", policy: null, Ct)).Expiration.Should().BeCloseTo(DateTimeOffset.UtcNow.AddMinutes(3), TimeSpan.FromSeconds(30));
        (await cache.GetCacheEntryAsync<string>("at", policy: null, Ct)).Expiration.Should().BeCloseTo(deadline, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task A_capable_hash_cache_takes_the_span_write_itself_and_any_other_gets_the_CacheKey_member()
    {
        var spy = new SpanWritingHashCache();
        IHashCache cache = spy;
        var proxy = Substitute.For<IHashCache>();

        await cache.SetAsync<string>(Key("a"), Fields, (CachePolicy?)null, Ct);
        await cache.SetAsync<string>(Key("b"), Fields, Ttl, null, Ct);
        await cache.SetAsync<string>(Key("c"), Fields, Deadline, null, Ct);
        await cache.SetAsync<string>(Key("d"), Fields, new HashCacheEntryOptions(TimeToLive: Ttl), null, Ct);
        await proxy.SetAsync<string>(Key("a"), Fields, Ttl, null, Ct);

        spy.SpanWrites.Should().Equal("a", "b", "c", "d");
        spy.SetCalls.Should().Be(0);
        await proxy.Received(1).SetAsync<string>(new CacheKey("a"), Fields, Ttl, null, Ct);
    }

    [Fact]
    public async Task A_typed_hash_cache_composes_its_key_and_writes_by_span()
    {
        var spy = new SpanWritingHashCache();
        IHashCache<string> typed = new HashCache<string>(spy, new PrefixCacheKeyStrategy("app"));
        var proxy = Substitute.For<IHashCache<string>>();

        await typed.SetAsync(Key("user:1"), Fields, Ct);
        await typed.SetAsync(Key("user:2"), Fields, Ttl, Ct);
        await typed.SetAsync(Key("user:3"), Fields, Deadline, Ct);
        await typed.SetAsync(Key("user:4"), Fields, new HashCacheEntryOptions(TimeToLive: Ttl), Ct);
        await proxy.SetAsync(Key("user:1"), Fields, Ttl, Ct);

        spy.SpanWrites.Should().Equal("app:user:1", "app:user:2", "app:user:3", "app:user:4");
        spy.SetCalls.Should().Be(0);
        await proxy.Received(1).SetAsync(new CacheKey("user:1"), Fields, Ttl, Ct);
    }

    [Fact]
    public async Task A_typed_hash_cache_writes_by_span_what_it_reads_by_key()
    {
        using var cache = InMemoryMultilayer.HashCache();
        var typed = new HashCache<string>(cache, new PrefixCacheKeyStrategy("app"));

        (await typed.SetAsync(Key(" User:1 "), Fields, Ct)).Should().BeTrue();

        (await typed.GetItemAsync("user:1", "f", Ct)).Should().Be("v");
    }

    [Fact]
    public async Task A_span_write_hands_the_capability_the_expiration_and_policy_it_was_given()
    {
        var policy = new CachePolicy();
        var options = new HashCacheEntryOptions(TimeToLive: Ttl);
        var spy = new SpanWritingCache();
        var hashSpy = new SpanWritingHashCache();
        ICache cache = spy;
        IHashCache hash = hashSpy;
        ICache<string> typed = new Cache<string>(spy, policy: policy);
        IHashCache<string> typedHash = new HashCache<string>(hashSpy, policy: policy);

        await cache.SetAsync<string>(Key("a"), "v", Ttl, policy, Ct);
        await cache.SetAsync<string>(Key("b"), "v", Deadline, policy, Ct);
        await typed.SetAsync(Key("c"), "v", Ttl, Ct);
        await typed.SetAsync(Key("d"), "v", Ct);
        await hash.SetAsync<string>(Key("a"), Fields, options, policy, Ct);
        await typedHash.SetAsync(Key("b"), Fields, Deadline, Ct);
        await typedHash.SetAsync(Key("c"), Fields, Ct);
        await typedHash.SetAsync(Key("d"), Fields, Ttl, Ct);
        await typedHash.SetAsync(Key("e"), Fields, options, Ct);

        spy.Calls.Should().Equal(("a", policy, Ttl), ("b", policy, Deadline), ("c", policy, Ttl), ("d", policy, null));
        hashSpy.Calls.Should().Equal(("a", policy, options), ("b", policy, Deadline), ("c", policy, null), ("d", policy, Ttl), ("e", policy, options));
    }

    [Fact]
    public async Task The_null_caches_accept_a_span_write_and_keep_nothing()
    {
        (await NullCache.Instance.SetAsync<string>(Key("k"), "v", (CachePolicy?)null, Ct)).Should().BeTrue();
        (await NullHashCache.Instance.SetAsync<string>(Key("k"), Fields, (CachePolicy?)null, Ct)).Should().BeTrue();
        (await ((ICache)NullCache.Instance).SetAsync<string>(Key("k"), "v", Ttl, null, Ct)).Should().BeTrue();
        (await ((IHashCache)NullHashCache.Instance).SetAsync<string>(Key("k"), Fields, Deadline, null, Ct)).Should().BeTrue();
    }

    [Fact]
    public async Task A_span_write_through_a_typed_cache_allocates_less_than_building_the_key_first()
    {
        using var cache = InMemoryMultilayer.Cache();
        var typed = new Cache<string>(cache, new PrefixCacheKeyStrategy("app"));
        await typed.SetAsync("user:42", "v", Ct);

        var bySpan = AllocatedBy(() => Complete(SetBySpan(typed)));
        var byKey = AllocatedBy(() => Complete(SetByBuiltKey(typed)));

        bySpan.Should().BeLessThan(byKey - 24);
    }

    private static Span<char> Key(string key) => key.ToCharArray();

    private static ValueTask<bool> SetBySpan(Cache<string> cache)
    {
        Span<char> key = stackalloc char[7];
        "user:42".CopyTo(key);
        return cache.SetAsync(key, "v", Ct);
    }

    private static ValueTask<bool> SetByBuiltKey(Cache<string> cache)
    {
        Span<char> key = stackalloc char[7];
        "user:42".CopyTo(key);
        return cache.SetAsync(new CacheKey(key), "v", Ct);
    }

    private static bool Complete(ValueTask<bool> write) => write.IsCompletedSuccessfully ? write.Result : write.AsTask().GetAwaiter().GetResult();

    /// <summary>The mean bytes the current thread allocates per warm call.</summary>
    private static long AllocatedBy(Action write)
    {
        for (var i = 0; i < 2_000; i++)
        {
            write();
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 2_000; i++)
        {
            write();
        }

        return (GC.GetAllocatedBytesForCurrentThread() - before) / 2_000;
    }

    /// <summary>A cache outside the library that opts into the span write.</summary>
    private sealed class SpanWritingCache : DictionaryCache, ISpanKeyCache
    {
        public List<(string Key, CachePolicy? Policy, object? Expiration)> Calls { get; } = [];

        public IEnumerable<string> SpanWrites => Calls.Select(c => c.Key);

        public ValueTask<bool> SetAsync<T>(Span<char> cacheKey, T? value, CachePolicy? policy, CancellationToken token = default) => Record(cacheKey, policy, null);

        public ValueTask<bool> SetAsync<T>(Span<char> cacheKey, T? value, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) => Record(cacheKey, policy, expiration);

        public ValueTask<bool> SetAsync<T>(Span<char> cacheKey, T? value, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) => Record(cacheKey, policy, expiration);

        public ValueTask<T?> GetAsync<T>(Span<char> cacheKey, CachePolicy? policy, CancellationToken token = default) => throw new NotSupportedException();

        public ValueTask<ICacheEntry<T?>> GetCacheEntryAsync<T>(Span<char> cacheKey, CachePolicy? policy, CancellationToken token = default) => throw new NotSupportedException();

        public ValueTask<T?> GetOrAddAsync<T>(Span<char> cacheKey, Func<CancellationToken, Task<T?>> generator, CachePolicy? policy, CancellationToken token = default) => throw new NotSupportedException();

        public ValueTask<T?> GetOrAddAsync<T>(Span<char> cacheKey, Func<CancellationToken, Task<T?>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) => throw new NotSupportedException();

        public ValueTask<T?> GetOrAddAsync<T>(Span<char> cacheKey, Func<CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) => throw new NotSupportedException();

        public ValueTask<T?> GetOrAddAsync<T, TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, CachePolicy? policy, CancellationToken token = default) => throw new NotSupportedException();

        public ValueTask<T?> GetOrAddAsync<T, TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) => throw new NotSupportedException();

        public ValueTask<T?> GetOrAddAsync<T, TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) => throw new NotSupportedException();

        public ValueTask<bool> ContainsAsync<T>(Span<char> cacheKey, CancellationToken token = default) => throw new NotSupportedException();

        public ValueTask<T?> GetOrAddWithExpirationAsync<T>(Span<char> cacheKey, Func<CancellationToken, Task<GeneratedValue<T>>> generator, CachePolicy? policy, CancellationToken token = default) => throw new NotSupportedException();

        public ValueTask<T?> GetOrAddWithExpirationAsync<T, TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<GeneratedValue<T>>> generator, CachePolicy? policy, CancellationToken token = default) => throw new NotSupportedException();

        private ValueTask<bool> Record(Span<char> cacheKey, CachePolicy? policy, object? expiration)
        {
            Calls.Add((cacheKey.ToString(), policy, expiration));
            return ValueTask.FromResult(true);
        }
    }

    private sealed class SpanWritingHashCache : IHashCache, ISpanKeyHashCache
    {
        public List<(string Key, CachePolicy? Policy, object? Expiration)> Calls { get; } = [];

        public IEnumerable<string> SpanWrites => Calls.Select(c => c.Key);

        public int SetCalls { get; private set; }

        public string Name => "spy";

        public ValueTask<bool> SetAsync<T>(Span<char> cacheKey, IDictionary<string, T?> values, CachePolicy? policy, CancellationToken token = default) => Record(cacheKey, policy, null);

        public ValueTask<bool> SetAsync<T>(Span<char> cacheKey, IDictionary<string, T?> values, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) => Record(cacheKey, policy, expiration);

        public ValueTask<bool> SetAsync<T>(Span<char> cacheKey, IDictionary<string, T?> values, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) => Record(cacheKey, policy, expiration);

        public ValueTask<bool> SetAsync<T>(Span<char> cacheKey, IDictionary<string, T?> values, HashCacheEntryOptions options, CachePolicy? policy, CancellationToken token = default) => Record(cacheKey, policy, options);

        public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, CachePolicy? policy, CancellationToken token = default) => Count();

        public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) => Count();

        public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) => Count();

        public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, HashCacheEntryOptions options, CachePolicy? policy, CancellationToken token = default) => Count();

        public ValueTask<T?> GetItemAsync<T>(Span<char> cacheKey, string field, CachePolicy? policy, CancellationToken token = default) => throw new NotSupportedException();

        public ValueTask<IDictionary<string, T?>> GetAsync<T>(Span<char> cacheKey, CachePolicy? policy, CancellationToken token = default) => throw new NotSupportedException();

        public ValueTask<ICacheEntry<IDictionary<string, T?>>> GetCacheEntryAsync<T>(Span<char> cacheKey, CachePolicy? policy, CancellationToken token = default) => throw new NotSupportedException();

        public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(Span<char> cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, CachePolicy? policy, CancellationToken token = default) => throw new NotSupportedException();

        public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(Span<char> cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) => throw new NotSupportedException();

        public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(Span<char> cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) => throw new NotSupportedException();

        public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(Span<char> cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, HashCacheSetOption? setOption, CachePolicy? policy, CancellationToken token = default) => throw new NotSupportedException();

        public ValueTask<bool> ContainsAsync<T>(Span<char> cacheKey, CancellationToken token = default) => throw new NotSupportedException();

        public ValueTask<T?> GetItemAsync<T>(CacheKey cacheKey, string field, CachePolicy? policy, CancellationToken token = default) => throw new NotSupportedException();

        public ValueTask<IDictionary<string, T?>> GetAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default) => throw new NotSupportedException();

        public ValueTask<IDictionary<string, T?>> GetAsync<T>(CacheKey cacheKey, string[] fields, CachePolicy? policy, CancellationToken token = default) => throw new NotSupportedException();

        public ValueTask<ICacheEntry<IDictionary<string, T?>>> GetCacheEntryAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default) => throw new NotSupportedException();

        public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, CachePolicy? policy, CancellationToken token = default) => throw new NotSupportedException();

        public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) => throw new NotSupportedException();

        public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) => throw new NotSupportedException();

        public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, HashCacheSetOption? setOption, CachePolicy? policy, CancellationToken token = default) => throw new NotSupportedException();

        public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default) => throw new NotSupportedException();

        public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) => throw new NotSupportedException();

        public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) => throw new NotSupportedException();

        public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, HashCacheEntryOptions options, CachePolicy? policy, CancellationToken token = default) => throw new NotSupportedException();

        public ValueTask<bool> RemoveAsync<T>(CacheKey cacheKey, CancellationToken token = default) => throw new NotSupportedException();

        public ValueTask<bool> ContainsAsync<T>(CacheKey cacheKey, CancellationToken token = default) => throw new NotSupportedException();

        public ValueTask<TimeSpan?> TimeToLiveAsync<T>(CacheKey cacheKey, CancellationToken token = default) => throw new NotSupportedException();

        public ValueTask<DateTimeOffset?> ExpireTimeAsync<T>(CacheKey cacheKey, CancellationToken token = default) => throw new NotSupportedException();

        public ValueTask<IDictionary<string, string?>?> GetMetadataAsync<T>(CacheKey cacheKey, CancellationToken token = default) => throw new NotSupportedException();

        public ValueTask<bool> SetMetadataAsync<T>(CacheKey cacheKey, IDictionary<string, string?> metadata, CancellationToken token = default) => throw new NotSupportedException();

        public void Dispose()
        {
        }

        private ValueTask<bool> Record(Span<char> cacheKey, CachePolicy? policy, object? expiration)
        {
            Calls.Add((cacheKey.ToString(), policy, expiration));
            return ValueTask.FromResult(true);
        }

        private ValueTask<bool> Count()
        {
            SetCalls++;
            return ValueTask.FromResult(true);
        }
    }

    private sealed class DecliningStrategy : ICacheKeyStrategy, ISpanCacheKeyStrategy
    {
        public CacheKey GetCacheKey<T>(CacheKey key) => key.WithName(key.Name + "#d");

        public bool TryGetCacheKey<T>(ReadOnlySpan<char> key, Span<char> destination, out int written)
        {
            written = 0;
            return false;
        }
    }
}
