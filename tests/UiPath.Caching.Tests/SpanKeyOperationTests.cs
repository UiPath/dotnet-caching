using UiPath.Caching.Tests.Fakes;

namespace UiPath.Caching.Tests;

/// <summary>Span <c>ContainsAsync</c>, <c>GetCacheEntryAsync</c> and <c>GetOrAddAsync</c> answer as the <see cref="CacheKey"/> forms do, and string keys read the local tier by their text.</summary>
public class SpanKeyOperationTests
{
    private static readonly Func<CancellationToken, Task<string?>> Unexpected = static _ => throw new InvalidOperationException("the generator ran on a hit");
    private static readonly Func<CancellationToken, Task<IDictionary<string, string?>>> UnexpectedHash = static _ => throw new InvalidOperationException("the generator ran on a hit");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_bare_default_key_still_binds_to_the_CacheKey_overloads()
    {
        var cache = Substitute.For<ICache>();
        var typed = Substitute.For<ICache<string>>();
        var hash = Substitute.For<IHashCache>();
        var typedHash = Substitute.For<IHashCache<string>>();

        await cache.ContainsAsync<string>(default, Ct);
        await cache.GetCacheEntryAsync<string>(default, null, Ct);
        await typed.ContainsAsync(default, Ct);
        await hash.ContainsAsync<string>(default, Ct);
        await hash.GetCacheEntryAsync<string>(default, null, Ct);
        await typedHash.ContainsAsync(default, Ct);
        await typedHash.GetCacheEntryAsync(default, Ct);

        await cache.Received(1).ContainsAsync<string>(Arg.Any<CacheKey>(), Ct);
        await cache.Received(1).GetCacheEntryAsync<string>(Arg.Any<CacheKey>(), null, Ct);
        await typed.Received(1).ContainsAsync(Arg.Any<CacheKey>(), Ct);
        await hash.Received(1).ContainsAsync<string>(Arg.Any<CacheKey>(), Ct);
        await hash.Received(1).GetCacheEntryAsync<string>(Arg.Any<CacheKey>(), null, Ct);
        await typedHash.Received(1).ContainsAsync(Arg.Any<CacheKey>(), Ct);
        await typedHash.Received(1).GetCacheEntryAsync(Arg.Any<CacheKey>(), Ct);
    }

    [Fact]
    public async Task A_cache_answers_span_contains_entry_and_get_or_add_as_the_key_forms_do()
    {
        using var cache = InMemoryMultilayer.Cache();
        (await cache.SetAsync<string>(" User:42 ", "v", policy: null, Ct)).Should().BeTrue();
        var expiration = (await cache.GetCacheEntryAsync<string>("user:42", policy: null, Ct)).Expiration;

        SpanReads.Contains<string>(cache, " User:42", Ct).Should().BeTrue();
        SpanReads.Contains<string>(cache, "user:43", Ct).Should().BeFalse();
        var entry = SpanReads.Entry<string>(cache, "User:42", Ct);
        entry.Value.Should().Be("v");
        entry.Expiration.Should().Be(expiration);
        SpanReads.Entry<string>(cache, "user:43", Ct).Found.Should().BeFalse();
        SpanReads.GetOrAdd(cache, "User:42", Unexpected, token: Ct).Should().Be("v");
    }

    [Fact]
    public async Task A_span_get_or_add_miss_runs_the_generator_once_and_stores_under_the_key()
    {
        using var cache = InMemoryMultilayer.Cache();
        var calls = 0;

        SpanReads.GetOrAdd(cache, " User:42 ", _ => { calls++; return Task.FromResult<string?>("fresh"); }, token: Ct).Should().Be("fresh");
        SpanReads.GetOrAdd(cache, "user:42", Unexpected, token: Ct).Should().Be("fresh");

        calls.Should().Be(1);
        (await cache.GetAsync<string>("user:42", policy: null, Ct)).Should().Be("fresh");
    }

    [Fact]
    public async Task A_span_get_or_add_with_an_expiration_stores_with_it()
    {
        using var cache = InMemoryMultilayer.Cache();
        var deadline = DateTimeOffset.UtcNow.AddMinutes(7);

        (await GetOrAddBySpan(cache, "user:42", TimeSpan.FromMinutes(3))).Should().Be("v");
        (await GetOrAddBySpan(cache, "user:43", deadline)).Should().Be("v");

        (await cache.TimeToLiveAsync<string>("user:42", Ct)).Should().BeCloseTo(TimeSpan.FromMinutes(3), TimeSpan.FromSeconds(30));
        (await cache.ExpireTimeAsync<string>("user:43", Ct)).Should().BeCloseTo(deadline, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task A_typed_cache_with_a_prefix_strategy_answers_span_operations_as_the_key_forms_do()
    {
        using var cache = InMemoryMultilayer.Cache(new InMemoryCacheOptions { CacheKeyStrategy = new PrefixCacheKeyStrategy("tier") });
        var typed = new Cache<string>(cache, new PrefixCacheKeyStrategy("app"));
        (await typed.SetAsync(" User:42 ", "v", Ct)).Should().BeTrue();

        SpanReads.Contains(typed, "User:42", Ct).Should().BeTrue();
        SpanReads.Contains(typed, "user:43", Ct).Should().BeFalse();
        SpanReads.GetOrAdd(typed, "User:42", Unexpected, Ct).Should().Be("v");
        SpanReads.GetOrAdd(typed, "user:43", static _ => Task.FromResult<string?>("fresh"), Ct).Should().Be("fresh");
        (await cache.GetAsync<string>("app:user:43", policy: null, Ct)).Should().Be("fresh");
    }

    [Fact]
    public async Task A_hash_cache_answers_span_operations_as_the_key_forms_do()
    {
        using var cache = InMemoryMultilayer.HashCache(new InMemoryCacheOptions { CacheKeyStrategy = new PrefixCacheKeyStrategy("tier") });
        var typed = new HashCache<string>(cache, new PrefixCacheKeyStrategy("app"));
        (await typed.SetAsync(" User:42 ", new Dictionary<string, string?> { ["f"] = "v" }, Ct)).Should().BeTrue();
        (await cache.SetAsync<string>("plain:1", new Dictionary<string, string?> { ["f"] = "p" }, policy: null, Ct)).Should().BeTrue();

        SpanReads.Contains(typed, "User:42", Ct).Should().BeTrue();
        SpanReads.Contains(typed, "user:43", Ct).Should().BeFalse();
        SpanReads.Contains<string>(cache, "Plain:1", Ct).Should().BeTrue();
        SpanReads.Entry(typed, "User:42", Ct).Value.Should().ContainKey("f").WhoseValue.Should().Be("v");
        SpanReads.Entry<string>(cache, "Plain:1", Ct).Value.Should().ContainKey("f").WhoseValue.Should().Be("p");
        SpanReads.GetOrAdd(typed, "User:42", UnexpectedHash, Ct).Should().ContainKey("f");
        SpanReads.GetOrAdd<string>(cache, "Plain:1", UnexpectedHash, token: Ct).Should().ContainKey("f");
        SpanReads.GetOrAdd(typed, "user:43", static _ => Task.FromResult<IDictionary<string, string?>>(new Dictionary<string, string?> { ["g"] = "fresh" }), Ct)
            .Should().ContainKey("g");
        (await cache.GetItemAsync<string>("app:user:43", "g", policy: null, Ct)).Should().Be("fresh");
    }

    [Fact]
    public async Task A_proxied_cache_serves_span_operations_through_its_CacheKey_overloads()
    {
        var cache = Substitute.For<ICache>();
        var typed = Substitute.For<ICache<string>>();
        var hash = Substitute.For<IHashCache>();
        cache.ContainsAsync<string>(new CacheKey("user:42"), Ct).Returns(true);
        cache.GetOrAddAsync(new CacheKey("user:42"), Unexpected, null, Ct).Returns("v");
        typed.ContainsAsync(new CacheKey("user:42"), Ct).Returns(true);
        typed.GetOrAddAsync(new CacheKey("user:42"), Unexpected, TimeSpan.FromMinutes(1), Ct).Returns("v");
        hash.ContainsAsync<string>(new CacheKey("user:42"), Ct).Returns(true);

        SpanReads.Contains<string>(cache, " User:42 ", Ct).Should().BeTrue();
        SpanReads.GetOrAdd(cache, "User:42", Unexpected, token: Ct).Should().Be("v");
        SpanReads.Contains(typed, "User:42", Ct).Should().BeTrue();
        Span<char> key = stackalloc char[7];
        "User:42".CopyTo(key);
        (await typed.GetOrAddAsync(key, Unexpected, TimeSpan.FromMinutes(1), Ct)).Should().Be("v");
        SpanReads.Contains<string>(hash, "User:42", Ct).Should().BeTrue();
    }

    [Fact]
    public async Task The_null_caches_answer_span_operations_with_nothing_and_run_the_generator()
    {
        SpanReads.Contains<string>(NullCache.Instance, "k", Ct).Should().BeFalse();
        SpanReads.Entry<string>(NullCache.Instance, "k", Ct).Found.Should().BeFalse();
        SpanReads.GetOrAdd(NullCache.Instance, "k", static _ => Task.FromResult<string?>("g"), token: Ct).Should().Be("g");
        SpanReads.Contains<string>(NullHashCache.Instance, "k", Ct).Should().BeFalse();
        SpanReads.Entry<string>(NullHashCache.Instance, "k", Ct).Found.Should().BeFalse();
        (await NullHashCache.Instance.GetOrAddAsync<string>("k", static _ => Task.FromResult<IDictionary<string, string?>>(new Dictionary<string, string?> { ["f"] = "g" }), policy: null, Ct))
            .Should().ContainKey("f");
    }

    [Fact]
    public async Task A_case_sensitive_key_reads_its_own_entry_beside_a_case_insensitive_one()
    {
        using var cache = InMemoryMultilayer.Cache();
        var sensitive = new CacheKey("User:42", CacheKeyCasing.Sensitive);
        (await cache.SetAsync<string>("user:42", "lower", policy: null, Ct)).Should().BeTrue();
        (await cache.SetAsync<string>(sensitive, "upper", policy: null, Ct)).Should().BeTrue();

        (await cache.GetAsync<string>(sensitive, policy: null, Ct)).Should().Be("upper", "the span path normalizes with the default casing, so a key built otherwise must stay on the key path");
        (await cache.GetOrAddAsync(sensitive, Unexpected, policy: null, Ct)).Should().Be("upper");
        (await cache.GetCacheEntryAsync<string>(sensitive, policy: null, Ct)).Value.Should().Be("upper");
    }

    [Fact]
    public async Task A_hash_cache_reads_a_case_sensitive_key_by_its_own_entry()
    {
        using var cache = InMemoryMultilayer.HashCache();
        var typed = new HashCache<string>(cache, new PrefixCacheKeyStrategy("app"));
        var sensitive = new CacheKey("User:42", CacheKeyCasing.Sensitive);
        var sensitiveName = new CacheKey("app:User:42", CacheKeyCasing.Sensitive);
        (await typed.SetAsync("user:42", new Dictionary<string, string?> { ["f"] = "lower" }, Ct)).Should().BeTrue();
        (await typed.SetAsync(sensitive, new Dictionary<string, string?> { ["f"] = "upper" }, Ct)).Should().BeTrue();

        (await typed.GetItemAsync(sensitive, "f", Ct)).Should().Be("upper");
        (await typed.GetAsync(sensitive, Ct)).Should().ContainKey("f").WhoseValue.Should().Be("upper");
        (await cache.GetItemAsync<string>(sensitiveName, "f", policy: null, Ct)).Should().Be("upper");
        (await cache.GetOrAddAsync(sensitiveName, UnexpectedHash, policy: null, Ct)).Should().ContainKey("f").WhoseValue.Should().Be("upper");
    }

    [Fact]
    public async Task A_typed_cache_reads_a_case_sensitive_key_by_its_own_entry()
    {
        using var cache = InMemoryMultilayer.Cache();
        var typed = new Cache<string>(cache, new PrefixCacheKeyStrategy("app"));
        var sensitive = new CacheKey("User:42", CacheKeyCasing.Sensitive);
        (await typed.SetAsync("user:42", "lower", Ct)).Should().BeTrue();
        (await typed.SetAsync(sensitive, "upper", Ct)).Should().BeTrue();

        (await typed.GetAsync(sensitive, Ct)).Should().Be("upper");
        (await typed.GetOrAddAsync(sensitive, Unexpected, Ct)).Should().Be("upper");
    }

    [Fact]
    public async Task A_typed_cache_with_a_strategy_that_declines_reads_by_key_what_it_wrote()
    {
        using var cache = InMemoryMultilayer.Cache();
        var typed = new Cache<string>(cache, new DecliningStrategy());
        (await typed.SetAsync("User:42", "v", Ct)).Should().BeTrue();

        (await typed.GetAsync("User:42", Ct)).Should().Be("v");
        (await typed.ContainsAsync("User:42", Ct)).Should().BeTrue();
        (await typed.GetOrAddAsync("User:42", Unexpected, Ct)).Should().Be("v");
        SpanReads.GetOrAdd(typed, "User:42", Unexpected, Ct).Should().Be("v");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_local_hit_past_the_rehydrate_threshold_still_rehydrates(bool bySpan)
    {
        using var cache = InMemoryMultilayer.Cache();
        var policy = new CachePolicy
        {
            DistributedExpiration = TimeSpan.FromMinutes(10),
            RehydrateEnabled = true,
            Rehydrate = new RehydrateOptions { Threshold = 0.5, BaseCooldown = TimeSpan.Zero },
        };
        (await cache.SetAsync<string>("user:42", "aged", TimeSpan.FromMinutes(1), policy: null, Ct)).Should().BeTrue();
        var rehydrated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<CancellationToken, Task<string?>> generator = _ =>
        {
            rehydrated.TrySetResult();
            return Task.FromResult<string?>("fresh");
        };

        var value = bySpan
            ? SpanReads.GetOrAdd(cache, "user:42", generator, policy, Ct)
            : await cache.GetOrAddAsync("user:42", generator, policy, Ct);

        value.Should().Be("aged");
        await rehydrated.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_hash_local_hit_past_the_rehydrate_threshold_still_rehydrates(bool bySpan)
    {
        using var cache = InMemoryMultilayer.HashCache();
        var policy = new CachePolicy
        {
            DistributedExpiration = TimeSpan.FromMinutes(10),
            RehydrateEnabled = true,
            Rehydrate = new RehydrateOptions { Threshold = 0.5, BaseCooldown = TimeSpan.Zero },
        };
        (await cache.SetAsync<string>("user:42", new Dictionary<string, string?> { ["f"] = "aged" }, TimeSpan.FromMinutes(1), policy: null, Ct)).Should().BeTrue();
        var rehydrated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<CancellationToken, Task<IDictionary<string, string?>>> generator = _ =>
        {
            rehydrated.TrySetResult();
            return Task.FromResult<IDictionary<string, string?>>(new Dictionary<string, string?> { ["f"] = "fresh" });
        };

        var values = bySpan
            ? SpanReads.GetOrAdd(cache, "user:42", generator, policy, Ct)
            : await cache.GetOrAddAsync("user:42", generator, policy, Ct);

        values.Should().ContainKey("f").WhoseValue.Should().Be("aged");
        await rehydrated.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
    }

    [Fact]
    public async Task A_cancelled_key_read_throws_even_on_a_warm_local_hit()
    {
        using var cache = InMemoryMultilayer.Cache();
        var typed = new Cache<string>(cache, new PrefixCacheKeyStrategy("app"));
        (await typed.SetAsync("user:42", "v", Ct)).Should().BeTrue();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var get = async () => await typed.GetAsync("user:42", cancelled.Token);
        var getOrAdd = async () => await typed.GetOrAddAsync("user:42", Unexpected, cancelled.Token);

        await get.Should().ThrowAsync<OperationCanceledException>();
        await getOrAdd.Should().ThrowAsync<OperationCanceledException>();
    }

#if NET9_0_OR_GREATER
    [Fact]
    public async Task Warm_string_key_reads_through_a_typed_cache_allocate_nothing()
    {
        using var cache = InMemoryMultilayer.Cache(new InMemoryCacheOptions { CacheKeyStrategy = new PrefixCacheKeyStrategy("tier") });
        var typed = new Cache<string>(cache, new PrefixCacheKeyStrategy("app"));
        (await typed.SetAsync("user:42", "v", Ct)).Should().BeTrue();

        AllocatedBy(() => typed.GetAsync("user:42").GetAwaiter().GetResult()).Should().Be(0);
        AllocatedBy(() => typed.ContainsAsync("user:42").GetAwaiter().GetResult()).Should().Be(0);
        AllocatedBy(() => typed.GetOrAddAsync("user:42", Unexpected).GetAwaiter().GetResult()).Should().Be(0);
    }

    [Fact]
    public async Task Warm_string_key_reads_on_a_cache_allocate_nothing()
    {
        using var cache = InMemoryMultilayer.Cache(new InMemoryCacheOptions { CacheKeyStrategy = new PrefixCacheKeyStrategy("tier") });
        (await cache.SetAsync<string>("user:42", "v", policy: null, Ct)).Should().BeTrue();

        AllocatedBy(() => cache.GetAsync<string>("user:42", policy: null).GetAwaiter().GetResult()).Should().Be(0);
        AllocatedBy(() => cache.GetCacheEntryAsync<string>("user:42", policy: null).GetAwaiter().GetResult()).Should().Be(0);
        AllocatedBy(() => cache.ContainsAsync<string>("user:42").GetAwaiter().GetResult()).Should().Be(0);
        AllocatedBy(() => cache.GetOrAddAsync("user:42", Unexpected, policy: null).GetAwaiter().GetResult()).Should().Be(0);
    }

    [Fact]
    public async Task Warm_string_key_reads_on_a_hash_cache_allocate_nothing()
    {
        using var cache = InMemoryMultilayer.HashCache(new InMemoryCacheOptions { CacheKeyStrategy = new PrefixCacheKeyStrategy("tier") });
        var typed = new HashCache<string>(cache, new PrefixCacheKeyStrategy("app"));
        (await typed.SetAsync("user:42", new Dictionary<string, string?> { ["f"] = "v" }, Ct)).Should().BeTrue();

        AllocatedBy(() => typed.GetItemAsync("user:42", "f").GetAwaiter().GetResult()).Should().Be(0);
        AllocatedBy(() => typed.GetAsync("user:42").GetAwaiter().GetResult()).Should().Be(0);
        AllocatedBy(() => typed.GetCacheEntryAsync("user:42").GetAwaiter().GetResult()).Should().Be(0);
        AllocatedBy(() => typed.ContainsAsync("user:42").GetAwaiter().GetResult()).Should().Be(0);
        AllocatedBy(() => typed.GetOrAddAsync("user:42", UnexpectedHash).GetAwaiter().GetResult()).Should().Be(0);
        AllocatedBy(() => cache.GetItemAsync<string>("app:user:42", "f", policy: null).GetAwaiter().GetResult()).Should().Be(0);
        AllocatedBy(() => cache.GetOrAddAsync("app:user:42", UnexpectedHash, policy: null).GetAwaiter().GetResult()).Should().Be(0);
    }

    [Fact]
    public async Task Warm_span_contains_entry_and_get_or_add_allocate_nothing()
    {
        using var cache = InMemoryMultilayer.Cache(new InMemoryCacheOptions { CacheKeyStrategy = new PrefixCacheKeyStrategy("tier") });
        var typed = new Cache<string>(cache, new PrefixCacheKeyStrategy("app"));
        using var hash = InMemoryMultilayer.HashCache();
        var typedHash = new HashCache<string>(hash, new PrefixCacheKeyStrategy("app"));
        (await typed.SetAsync("user:42", "v", Ct)).Should().BeTrue();
        (await cache.SetAsync<string>("plain:1", "p", policy: null, Ct)).Should().BeTrue();
        (await typedHash.SetAsync("user:42", new Dictionary<string, string?> { ["f"] = "v" }, Ct)).Should().BeTrue();

        AllocatedBy(() => SpanReads.Contains(typed, " User:42 ")).Should().Be(0);
        AllocatedBy(() => SpanReads.GetOrAdd(typed, "user:42", Unexpected)).Should().Be(0);
        AllocatedBy(() => SpanReads.Contains<string>(cache, "plain:1")).Should().Be(0);
        AllocatedBy(() => SpanReads.Entry<string>(cache, "plain:1")).Should().Be(0);
        AllocatedBy(() => SpanReads.GetOrAdd(cache, "plain:1", Unexpected)).Should().Be(0);
        AllocatedBy(() => SpanReads.Contains(typedHash, "user:42")).Should().Be(0);
        AllocatedBy(() => SpanReads.Entry(typedHash, "user:42")).Should().Be(0);
        AllocatedBy(() => SpanReads.GetOrAdd(typedHash, "user:42", UnexpectedHash)).Should().Be(0);
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

    private static ValueTask<string?> GetOrAddBySpan(ICache cache, string key, TimeSpan expiration)
    {
        Span<char> buffer = stackalloc char[key.Length];
        key.CopyTo(buffer);
        return cache.GetOrAddAsync(buffer, static _ => Task.FromResult<string?>("v"), expiration, policy: null, Ct);
    }

    private static ValueTask<string?> GetOrAddBySpan(ICache cache, string key, DateTimeOffset expiration)
    {
        Span<char> buffer = stackalloc char[key.Length];
        key.CopyTo(buffer);
        return cache.GetOrAddAsync(buffer, static _ => Task.FromResult<string?>("v"), expiration, policy: null, Ct);
    }

    /// <summary>Declines every span composition, so the typed cache must fall back to the key it builds.</summary>
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
