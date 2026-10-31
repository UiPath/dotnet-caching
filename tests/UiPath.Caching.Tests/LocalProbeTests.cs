#pragma warning disable CA2012 // NSubstitute setups call the ValueTask-returning members without consuming the result.
using UiPath.Caching.Tests.Fakes;

namespace UiPath.Caching.Tests;

/// <summary>How many times a call reads the local tier: a miss that has just read it does not read it again to decide to go on.</summary>
public class LocalProbeTests
{
    private static readonly CachePolicy NoLocalLock = new() { Lock = new LockProfile { LocalSingleFlightEnabled = false } };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_miss_reads_the_local_tier_once_when_there_is_no_local_lock()
    {
        var memory = new CountingMemoryCacheFactory();
        using var cache = InMemoryMultilayer.Cache(memory: memory);

        (await cache.GetOrAddAsync<string>("k", Generate, NoLocalLock, Ct)).Should().Be("generated");

        memory.Lookups.Should().Be(1);
    }

    [Fact]
    public async Task A_miss_reads_the_local_tier_once_with_the_local_lock_and_no_distributed_lock()
    {
        var memory = new CountingMemoryCacheFactory();
        using var cache = InMemoryMultilayer.Cache(memory: memory);

        (await cache.GetOrAddAsync<string>("k", Generate, policy: null, Ct)).Should().Be("generated");

        memory.Lookups.Should().Be(1);
    }

    [Fact]
    public async Task A_miss_reads_the_local_tier_for_the_front_and_again_under_the_distributed_lock()
    {
        var memory = new CountingMemoryCacheFactory();
        using var cache = InMemoryMultilayer.Cache(memory: memory);
        var policy = new CachePolicy { Lock = new LockProfile { DistributedLockEnabled = true } };

        (await cache.GetOrAddAsync<string>("k", Generate, policy, Ct)).Should().Be("generated");

        memory.Lookups.Should().Be(2);
    }

    [Fact]
    public async Task A_miss_by_span_reads_the_local_tier_once_when_there_is_no_local_lock()
    {
        var memory = new CountingMemoryCacheFactory();
        using var cache = InMemoryMultilayer.Cache(memory: memory);

        (await GetOrAddBySpan(cache, "k", NoLocalLock)).Should().Be("generated");

        memory.Lookups.Should().Be(1);
    }

    [Fact]
    public async Task A_stateful_miss_reads_the_local_tier_once_when_there_is_no_local_lock()
    {
        var memory = new CountingMemoryCacheFactory();
        using var cache = InMemoryMultilayer.Cache(memory: memory);

        (await cache.GetOrAddAsync<string, string>("k", "state", (s, _) => Task.FromResult<string?>(s), NoLocalLock, Ct)).Should().Be("state");

        memory.Lookups.Should().Be(1);
    }

    [Fact]
    public async Task A_hit_reads_the_local_tier_once()
    {
        var memory = new CountingMemoryCacheFactory();
        using var cache = InMemoryMultilayer.Cache(memory: memory);
        await cache.GetOrAddAsync<string>("k", Generate, NoLocalLock, Ct);
        var before = memory.Lookups;

        (await cache.GetOrAddAsync<string>("k", Generate, NoLocalLock, Ct)).Should().Be("generated");

        (memory.Lookups - before).Should().Be(1);
    }

    [Fact]
    public async Task A_miss_that_the_front_could_not_judge_still_reads_the_local_tier_before_it_generates()
    {
        using var cache = InMemoryMultilayer.Cache(new InMemoryCacheOptions { CacheKeyStrategy = new PrefixCacheKeyStrategy("tier") });
        var generated = 0;
        Task<string?> Count(CancellationToken _) => Task.FromResult<string?>("v" + Interlocked.Increment(ref generated));

        (await cache.GetOrAddAsync<string>("k", Count, policy: null, Ct)).Should().Be("v1");
        (await cache.GetOrAddAsync<string>("k", Count, policy: null, Ct)).Should().Be("v1");

        generated.Should().Be(1);
    }

    [Fact]
    public async Task A_case_sensitive_key_is_found_after_it_was_generated()
    {
        using var cache = InMemoryMultilayer.Cache();
        var generated = 0;
        Task<string?> Count(CancellationToken _) => Task.FromResult<string?>("v" + Interlocked.Increment(ref generated));
        var key = new CacheKey("Mixed", CacheKeyCasing.Sensitive);

        (await cache.GetOrAddAsync<string>(key, Count, policy: null, Ct)).Should().Be("v1");
        (await cache.GetOrAddAsync<string>(key, Count, policy: null, Ct)).Should().Be("v1");

        generated.Should().Be(1);
    }

    [Fact]
    public async Task A_cached_null_is_served_from_the_local_tier_and_does_not_generate_again()
    {
        var inner = Substitute.For<ICache>();
        inner.GetCacheEntryAsync<string>(Arg.Any<CacheKey>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ICacheEntry<string?>>(new TestCacheEntry<string?> { Expiration = DateTimeOffset.MinValue }));
        inner.SetAsync<string>(Arg.Any<CacheKey>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()).Returns(true);
        using var cache = InMemoryMultilayer.Cache(new InMemoryCacheOptions { CacheNullValues = true }, inner);
        var generated = 0;
        Task<string?> Null(CancellationToken _)
        {
            Interlocked.Increment(ref generated);
            return Task.FromResult<string?>(null);
        }

        (await cache.GetOrAddAsync<string>("k", Null, policy: null, Ct)).Should().BeNull();
        var readsAfterFirst = inner.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(ICache.GetCacheEntryAsync));
        (await cache.GetOrAddAsync<string>("k", Null, policy: null, Ct)).Should().BeNull();

        generated.Should().Be(1);
        inner.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(ICache.GetCacheEntryAsync)).Should().Be(readsAfterFirst);
    }

    [Fact]
    public async Task A_miss_with_an_inner_tier_still_reads_it_once()
    {
        var inner = Substitute.For<ICache>();
        inner.GetCacheEntryAsync<string>(Arg.Any<CacheKey>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ICacheEntry<string?>>(new TestCacheEntry<string?> { Expiration = DateTimeOffset.MinValue }));
        inner.SetAsync<string>(Arg.Any<CacheKey>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()).Returns(true);
        var memory = new CountingMemoryCacheFactory();
        using var cache = InMemoryMultilayer.Cache(inner: inner, memory: memory);

        (await cache.GetOrAddAsync<string>("k", Generate, NoLocalLock, Ct)).Should().Be("generated");

        memory.Lookups.Should().Be(1);
        inner.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(ICache.GetCacheEntryAsync)).Should().Be(1);
    }

    [Fact]
    public async Task A_hash_cache_miss_reads_the_local_tier_once_when_there_is_no_local_lock()
    {
        var memory = new CountingMemoryCacheFactory();
        using var cache = InMemoryMultilayer.HashCache(memory: memory);

        var values = await cache.GetOrAddAsync<string>("k", _ => Task.FromResult<IDictionary<string, string?>>(new Dictionary<string, string?> { ["f"] = "v" }), NoLocalLock, Ct);

        values["f"].Should().Be("v");
        memory.Lookups.Should().Be(1);
    }

    [Fact]
    public async Task A_typed_cache_hit_by_span_finds_entries_written_by_string_and_back()
    {
        using var inner = InMemoryMultilayer.Cache();
        var typed = new Cache<string>(inner);
        await typed.SetAsync("  Mixed:CASE:Key  ", "by-string", Ct);

        (await GetBySpan(typed, "mixed:case:key")).Should().Be("by-string");
        (await GetBySpan(typed, "MIXED:case:KEY ")).Should().Be("by-string");

        await typed.SetAsync("tenant:7F3A", "second", Ct);
        (await typed.GetAsync("TENANT:7f3a", Ct)).Should().Be("second");
        (await GetBySpan(typed, "Tenant:7f3a")).Should().Be("second");
    }

    private static Task<string?> Generate(CancellationToken token) => Task.FromResult<string?>("generated");

    private static ValueTask<string?> GetOrAddBySpan(MultilayerCache cache, string key, CachePolicy policy)
    {
        Span<char> buffer = stackalloc char[64];
        key.AsSpan().CopyTo(buffer);
        return cache.GetOrAddAsync<string>(buffer[..key.Length], Generate, policy, Ct);
    }

    private static ValueTask<string?> GetBySpan(Cache<string> cache, string key)
    {
        Span<char> buffer = stackalloc char[64];
        key.AsSpan().CopyTo(buffer);
        return ((ISpanKeyCache<string>)cache).GetAsync(buffer[..key.Length], Ct);
    }
}
