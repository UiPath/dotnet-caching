using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using UiPath.Caching.Config;
using UiPath.Caching.Locking;
using UiPath.Caching.Tests.Fakes;
using UiPath.Caching.Tests.Telemetry;
using MemoryEntry = Microsoft.Extensions.Caching.Memory.ICacheEntry;

namespace UiPath.Caching.Tests;

/// <summary>Fail-safe under a size limit: the kept values live in the local tier, at no size while their entry is live and at its size, and at low priority, once it has expired.</summary>
public class MultilayerCacheFailSafeSizeLimitTests
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Long = TimeSpan.FromHours(1);
    private static readonly CachePolicy FailSafe = new() { FailSafeEnabled = true, FailSafeMaxStaleAge = TimeSpan.FromHours(2), FailSafeCooldown = TimeSpan.Zero };
    private static readonly CachePolicy Plain = new();

    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly HeldMemory _memory;

    public MultilayerCacheFailSafeSizeLimitTests() => _memory = new HeldMemory(_clock);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Live_entries_and_kept_values_stay_within_the_one_limit()
    {
        using var cache = Build(sizeLimit: 20);
        var kept = await FillAsync(cache, "kept", 10, FailSafe, Lifetime);
        _clock.Advance(Lifetime * 2);
        (await FallbacksAsync(cache, kept)).Should().Be(10);
        await CachePeers.Until(() => _memory.Left == 10);
        _memory.SizeHeld.Should().Be(10, "each kept value took the size its expired entry gave back");
        var live = await FillAsync(cache, "live", 10, FailSafe);
        _memory.SizeHeld.Should().Be(20);

        // Full: the tier refuses the next entry, as it does without fail-safe, and compacts by a twentieth of its limit.
        await WriteAsync(cache, "more", Plain, Long);
        (await HitAsync(cache, "more")).Should().BeNull();
        await CachePeers.Until(() => _memory.SizeHeld == 19);
        await WriteAsync(cache, "more", Plain, Long);

        (await HitAsync(cache, "more")).Should().Be("more");
        (await HitsAsync(cache, live)).Should().Be(10);
        (await FallbacksAsync(cache, kept)).Should().Be(9, "twenty payloads in all");
        _memory.SizeHeld.Should().Be(20);
    }

    [Fact]
    public async Task When_the_tier_compacts_it_takes_kept_values_before_live_entries()
    {
        using var cache = Build(sizeLimit: 20);
        var before = await FillAsync(cache, "before", 10, Plain);
        var kept = await FillAsync(cache, "kept", 5, FailSafe, Lifetime);
        _clock.Advance(Lifetime * 2);
        (await FallbacksAsync(cache, kept)).Should().Be(5);
        await CachePeers.Until(() => _memory.SizeHeld == 15);
        var live = await FillAsync(cache, "live", 5, FailSafe);

        _memory.OfferTooLarge();
        await CachePeers.Until(() => _memory.SizeHeld == 19);

        (await HitsAsync(cache, before)).Should().Be(10, "the oldest entries are not the first to go");
        (await HitsAsync(cache, live)).Should().Be(5);
        (await FallbacksAsync(cache, kept)).Should().Be(4);
        _clock.Advance(Long * 2);
        (await FallbacksAsync(cache, live)).Should().Be(5, "a compaction leaves the values of live entries, which take no size");
    }

    [Fact]
    public async Task A_kept_value_costs_nothing_while_its_local_entry_is_live()
    {
        using var cache = Build(sizeLimit: 2);

        await WriteAsync(cache, "a", FailSafe);
        await WriteAsync(cache, "b", FailSafe);

        (await HitAsync(cache, "a")).Should().Be("a");
        (await HitAsync(cache, "b")).Should().Be("b", "two entries fill the limit; their kept values are the same two payloads");
        _memory.SizeHeld.Should().Be(2);
        _clock.Advance(Lifetime * 2);
        (await FallbacksAsync(cache, ["a", "b"])).Should().Be(2);
        await CachePeers.Until(() => _memory.Left == 2);
        _memory.SizeHeld.Should().Be(2);
        (await FallbacksAsync(cache, ["a", "b"])).Should().Be(2);
    }

    [Fact]
    public async Task A_kept_value_the_full_tier_refuses_is_not_kept()
    {
        using var cache = Build(sizeLimit: 20);
        await WriteAsync(cache, "a", FailSafe);
        await FillAsync(cache, "live", 19, Plain);
        _clock.Advance(Lifetime * 2);

        // The read takes the expired entry of "a" out of the tier; what follows its eviction waits until a live entry has taken the room.
        _memory.Hold();
        (await FallbackAsync(cache, "a")).Should().Be("a", "the value is there before the tier has told that its entry expired");
        await WriteAsync(cache, "c", Plain, Long);
        _memory.Release();
        await CachePeers.Until(() => _memory.Left == 1);

        (await FallbackAsync(cache, "a")).Should().BeNull();
        (await HitAsync(cache, "c")).Should().Be("c");
        _memory.SizeHeld.Should().BeLessThanOrEqualTo(20);
    }

    [Theory]
    [InlineData("removal")]
    [InlineData("plain write")]
    [InlineData("fail-safe write")]
    public async Task What_lands_between_an_entrys_expiry_and_the_charge_of_its_kept_value_wins(string landing)
    {
        using var cache = Build(sizeLimit: 20);
        var key = new CacheKey("a");
        await WriteAsync(cache, "a", FailSafe);
        _clock.Advance(Lifetime * 2);
        _memory.Hold();
        (await FallbackAsync(cache, "a")).Should().Be("a");

        if (landing == "removal")
        {
            await cache.RemoveAsync<string>(key, Ct);
        }
        else
        {
            (await cache.SetAsync(key, "newer", Long, landing == "plain write" ? Plain : FailSafe, Ct)).Should().BeTrue();
        }

        _memory.Release();
        await CachePeers.Until(() => _memory.Left == 1);

        _memory.SizeHeld.Should().Be(landing == "removal" ? 0 : 1, "the expired entry's value is not kept");
        (await HitAsync(cache, "a")).Should().Be(landing == "removal" ? null : "newer");
        _clock.Advance(Long * 2);
        (await FallbackAsync(cache, "a")).Should().Be(landing == "fail-safe write" ? "newer" : null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_kept_value_takes_the_size_its_entry_was_charged_whatever_the_size_provider_answers_later(bool throwsLater)
    {
        using var cache = Build(sizeLimit: 1, new FirstSizes(throwsLater));
        await WriteAsync(cache, "a", FailSafe);
        _clock.Advance(Lifetime * 2);
        (await FallbackAsync(cache, "a")).Should().Be("a");
        await CachePeers.Until(() => _memory.Left == 1);

        _memory.SizeHeld.Should().Be(1, "the kept value took over the size of its expired entry");
        await WriteAsync(cache, "b", Plain, Long);

        (await FallbackAsync(cache, "a")).Should().Be("a");
        (await HitAsync(cache, "b")).Should().BeNull("the limit is one in all");
        _memory.SizeHeld.Should().Be(1);
    }

    [Fact]
    public async Task A_write_without_fail_safe_that_lands_while_a_kept_value_is_stored_again_still_drops_it()
    {
        using var cache = Build(sizeLimit: 20);
        var key = new CacheKey("a");
        await WriteAsync(cache, "a", FailSafe);
        _clock.Advance(Lifetime * 2);
        Task<bool>? newer = null;

        // The write comes from another thread while the tier holds neither the value it replaces nor the one it stores.
        _memory.Charging = () =>
        {
            newer = Task.Run(() => cache.SetAsync(key, "newer", Long, Plain, Ct).AsTask(), Ct);
            newer.Wait(TimeSpan.FromMilliseconds(300), Ct);
        };

        // A read that looks for no kept value takes the expired entry out of the tier.
        (await HitAsync(cache, "a")).Should().BeNull();
        await CachePeers.Until(() => _memory.Left == 1);

        (await newer!).Should().BeTrue();
        (await HitAsync(cache, "a")).Should().Be("newer");
        _clock.Advance(Long + Lifetime);
        (await FallbackAsync(cache, "a")).Should().BeNull("the write replaced the kept value, though it found none at first");
    }

    [Fact]
    public async Task Without_fail_safe_a_size_limited_cache_is_left_as_it_was()
    {
        using var cache = Build(sizeLimit: 2);

        await WriteAsync(cache, "a", Plain);
        await WriteAsync(cache, "b", Plain);

        _memory.Committed.Should().HaveCount(2).And.OnlyContain(entry => entry.Key is string && entry.Callbacks == 0 && entry.Size == 1 && entry.Priority == CacheItemPriority.Normal);
    }

    [Fact]
    public async Task A_write_without_fail_safe_is_told_nothing_once_fail_safe_is_in_use()
    {
        using var cache = Build(sizeLimit: 2);

        await WriteAsync(cache, "a", FailSafe);
        await WriteAsync(cache, "b", Plain);

        _memory.Committed.Should().HaveCount(3);
        _memory.Committed.Should().ContainSingle(entry => Equals(entry.Key, "a")).Which.Callbacks.Should().Be(1);
        _memory.Committed.Should().ContainSingle(entry => Equals(entry.Key, "b")).Which.Callbacks.Should().Be(0);
        var kept = _memory.Committed.Should().ContainSingle(entry => !(entry.Key is string)).Which;
        kept.Size.Should().Be(0);
        kept.Priority.Should().Be(CacheItemPriority.NeverRemove, "a compaction that took it would free nothing");
    }

    [Fact]
    public async Task Without_a_size_limit_a_kept_value_is_stored_once_and_nothing_is_told_when_its_entry_leaves()
    {
        using var cache = Build(sizeLimit: null);
        await WriteAsync(cache, "a", FailSafe);
        _clock.Advance(Lifetime * 2);

        (await FallbackAsync(cache, "a")).Should().Be("a");

        _memory.Committed.Should().HaveCount(2);
        _memory.Committed.Should().ContainSingle(entry => entry.Key is string).Which.Callbacks.Should().Be(0);
    }

    private static async Task WriteAsync(MultilayerCache cache, string key, CachePolicy policy, TimeSpan? lifetime = null) =>
        (await cache.SetAsync(new CacheKey(key), key, lifetime ?? Lifetime, policy, Ct)).Should().BeTrue();

    private static ValueTask<string?> HitAsync(MultilayerCache cache, string key) => cache.GetAsync<string>(new CacheKey(key), Plain, Ct);

    /// <summary>Writes <paramref name="count"/> long-lived entries, unless a lifetime is given, and returns their keys.</summary>
    private static async Task<string[]> FillAsync(MultilayerCache cache, string prefix, int count, CachePolicy policy, TimeSpan? lifetime = null)
    {
        var keys = Enumerable.Range(1, count).Select(i => $"{prefix}{i}").ToArray();
        foreach (var key in keys)
        {
            await WriteAsync(cache, key, policy, lifetime ?? Long);
        }

        return keys;
    }

    private static async Task<int> HitsAsync(MultilayerCache cache, string[] keys)
    {
        var hits = 0;
        foreach (var key in keys)
        {
            if (await HitAsync(cache, key) == key)
            {
                hits++;
            }
        }

        return hits;
    }

    /// <summary>What a miss with a failing generator is answered with: the kept value, or null when the failure reaches the caller.</summary>
    private static async Task<string?> FallbackAsync(MultilayerCache cache, string key)
    {
        try
        {
            return await cache.GetOrAddAsync(new CacheKey(key), static _ => Task.FromException<string?>(new InvalidOperationException("down")), Lifetime, FailSafe, Ct);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static async Task<int> FallbacksAsync(MultilayerCache cache, string[] keys)
    {
        var served = 0;
        foreach (var key in keys)
        {
            if (await FallbackAsync(cache, key) == key)
            {
                served++;
            }
        }

        return served;
    }

    private MultilayerCache Build(long? sizeLimit, ICacheEntrySizeProvider? sizes = null)
    {
        var options = new InMemoryCacheOptions { SizeLimit = sizeLimit, SizeProvider = sizes };
        return RunTables.Track(new MultilayerCache(
            KnownCacheProviderNames.InMemory,
            NullCache.Instance,
            _memory,
            NullChangeTokenFactory.Instance,
            NullTopicFactory.Instance,
            NullCacheEventFactory.Instance,
            new RecordingTelemetryProvider(),
            options,
            options,
            new CacheOptions { AppShortName = "test" },
            distributedLock: NullDistributedLock.Instance,
            policyFactory: NullCachePolicyFactory.Instance,
            clock: _clock,
            logger: NullLogger.Instance));
    }

    /// <summary>Sizes an entry as one the first time, and answers zero or throws when it is asked about that entry again.</summary>
    private sealed class FirstSizes(bool throwsLater) : ICacheEntrySizeProvider
    {
        private readonly HashSet<object> _sized = new(ReferenceEqualityComparer.Instance);

        public long GetSize(UiPath.Caching.ICacheEntry entry)
        {
            lock (_sized)
            {
                if (_sized.Add(entry))
                {
                    return 1;
                }
            }

            return throwsLater ? throw new InvalidOperationException("sized again") : 0;
        }
    }

    /// <summary>The local tier, which records what is committed to it and can hold back what follows an eviction.</summary>
    private sealed class HeldMemory(TimeProvider clock) : IMemoryCacheFactory, IMemoryCache
    {
        private readonly MemoryCacheFactory _factory = new(clock, NullLoggerFactory.Instance);
        private readonly ManualResetEventSlim _released = new(true);
        private readonly List<(object Key, int Callbacks, long? Size, CacheItemPriority Priority)> _committed = [];
        private IMemoryCache _inner = null!;
        private int _left;

        public IReadOnlyList<(object Key, int Callbacks, long? Size, CacheItemPriority Priority)> Committed
        {
            get
            {
                lock (_committed)
                {
                    return [.. _committed];
                }
            }
        }

        /// <summary>Entries that left the tier and whose eviction callbacks have run.</summary>
        public int Left => Volatile.Read(ref _left);

        public long SizeHeld => _inner.GetCurrentStatistics()!.CurrentEstimatedSize ?? 0;

        /// <summary>Runs once, as the next kept value is stored again at its size, with the one it replaces taken out as the tier's scan for expired entries may.</summary>
        public Action? Charging { get; set; }

        public void Hold() => _released.Reset();

        /// <summary>Offers the tier an entry larger than any limit a test sets, which it refuses, and then compacts.</summary>
        public void OfferTooLarge()
        {
            using var entry = _inner.CreateEntry(new object());
            entry.Size = 1_000_000;
            entry.Value = string.Empty;
        }

        public void Release() => _released.Set();

        public IMemoryCache Get(IMemoryCacheOptions memoryOptions)
        {
            _inner = _factory.Get(memoryOptions);
            return this;
        }

        public MemoryEntry CreateEntry(object key) => new HeldEntry(this, _inner.CreateEntry(key));

        public bool TryGetValue(object key, out object? value) => _inner.TryGetValue(key, out value);

        public void Remove(object key) => _inner.Remove(key);

        public MemoryCacheStatistics? GetCurrentStatistics() => _inner.GetCurrentStatistics();

        public void Dispose()
        {
            _released.Set();
            _inner.Dispose();
        }

        private sealed class HeldEntry(HeldMemory owner, MemoryEntry inner) : MemoryEntry
        {
            public object Key => inner.Key;

            public object? Value
            {
                get => inner.Value;
                set => inner.Value = value;
            }

            public DateTimeOffset? AbsoluteExpiration
            {
                get => inner.AbsoluteExpiration;
                set => inner.AbsoluteExpiration = value;
            }

            public TimeSpan? AbsoluteExpirationRelativeToNow
            {
                get => inner.AbsoluteExpirationRelativeToNow;
                set => inner.AbsoluteExpirationRelativeToNow = value;
            }

            public TimeSpan? SlidingExpiration
            {
                get => inner.SlidingExpiration;
                set => inner.SlidingExpiration = value;
            }

            public IList<Microsoft.Extensions.Primitives.IChangeToken> ExpirationTokens => inner.ExpirationTokens;

            public IList<PostEvictionCallbackRegistration> PostEvictionCallbacks => inner.PostEvictionCallbacks;

            public CacheItemPriority Priority
            {
                get => inner.Priority;
                set => inner.Priority = value;
            }

            public long? Size
            {
                get => inner.Size;
                set => inner.Size = value;
            }

            public void Dispose()
            {
                lock (owner._committed)
                {
                    owner._committed.Add((inner.Key, inner.PostEvictionCallbacks.Count, inner.Size, inner.Priority));
                }

                foreach (var registration in inner.PostEvictionCallbacks)
                {
                    var callback = registration.EvictionCallback;
                    registration.EvictionCallback = (key, value, reason, state) =>
                    {
                        owner._released.Wait();
                        try
                        {
                            callback?.Invoke(key, value, reason, state);
                        }
                        finally
                        {
                            Interlocked.Increment(ref owner._left);
                        }
                    };
                }

                if (inner.Key is not string && inner.Size > 0 && owner.Charging is { } charging)
                {
                    owner.Charging = null;
                    owner._inner.Remove(inner.Key);
                    charging();
                }

                inner.Dispose();
            }
        }
    }
}
