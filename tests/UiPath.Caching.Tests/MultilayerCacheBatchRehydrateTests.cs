using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;
using UiPath.Caching.Locking;
using UiPath.Caching.Telemetry;
using UiPath.Caching.Tests.Telemetry;

namespace UiPath.Caching.Tests;

public class MultilayerCacheBatchRehydrateTests(ITestContextAccessor testContextAccessor) : IAsyncLifetime
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubstitute();

    private static readonly long[] States1 = [1L];
    private static readonly long[] States2 = [2L];
    private static readonly long[] States1And2 = [1L, 2L];
    private static readonly string?[] AAndB = ["A", "B"];

    private ICache _innerCache = default!;
    private MemoryCache _memoryCache = default!;
    private ICacheKeyStrategy _cacheKeyStrategy = default!;
    private ITopicKeyStrategy _topicKeyStrategy = default!;
    private ITopicFactory _topicFactory = default!;
    private MultilayerCacheTests.ITopicProviderWithConnectionState _topicProvider = default!;
    private ITopic<ICacheEvent> _topic = default!;
    private IChangeTokenFactory _changeTokenFactory = default!;
    private IMemoryCacheFactory _memoryCacheFactory = default!;
    private ICacheEventFactory _cacheEventFactory = default!;
    private IDistributedLock _distributedLock = default!;
    private AsyncKeyedLocalLock _locker = default!;
    private InMemoryRedisCacheOptions _options = default!;
    private TopicKey _topicKey = default!;
    private RecordingTelemetryProvider _telemetry = default!;
    private MultilayerCache? _sut;

    private MultilayerCache Sut => _sut ??= _fixture.Create<MultilayerCache>();

    private static readonly TimeSpan Duration = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<CacheKey, string?> _stored = new();
    private readonly ConcurrentQueue<CacheKey[]> _innerSetCalls = new();
    private readonly ConcurrentQueue<DateTimeOffset?> _innerSetExpirations = new();
    private readonly HashSet<CacheKey> _agedKeys = [];

    private static CachePolicy RehydratePolicy(double threshold = 0.75) => new()
    {
        DistributedExpiration = Duration,
        RehydrateEnabled = true,
        Rehydrate = new RehydrateOptions
        {
            Threshold = threshold,
            BaseCooldown = TimeSpan.FromSeconds(1),
            MaxCooldown = TimeSpan.FromMinutes(5),
            TimeoutFraction = 0.5,
            Name = "test-profile",
        },
    };

    /// <summary>Seeds a hit past the rehydrate threshold.</summary>
    private void SeedAged(CacheKey key, string? value)
    {
        _agedKeys.Add(key);
        _stored[key] = value;
    }

    [Fact]
    public async Task Hits_past_threshold_are_rehydrated_in_one_background_call()
    {
        var token = testContextAccessor.Current.CancellationToken;
        SeedAged("a", "A");
        SeedAged("b", "B");
        var rehydrateCalls = new List<long[]>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var result = await Sut.GetOrAddAsync<string, long>(
            [new((CacheKey)"a", 1L), new((CacheKey)"b", 2L)],
            (ids, _) =>
            {
                lock (rehydrateCalls) { rehydrateCalls.Add(ids); }
                done.TrySetResult();
                return Task.FromResult(ids.Select(id => new KeyValuePair<long, string?>(id, "fresh:" + id)).ToArray());
            },
            RehydratePolicy(),
            token);

        result.Select(r => r.Value).Should().Equal(AAndB, "the cached values are returned; rehydration is in the background");
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
        await Task.Delay(200, token);
        var calls = Snapshot(rehydrateCalls);
        calls.Should().HaveCount(1, "all states past threshold must coalesce into one generator call");
        calls[0].Should().BeEquivalentTo(States1And2);
        await WaitForAsync(() => _stored.TryGetValue((CacheKey)"b", out var v) && v == "fresh:2", TimeSpan.FromSeconds(10), token);
        _innerSetCalls.ToArray().Single().Should().BeEquivalentTo(
            new[] { (CacheKey)"a", (CacheKey)"b" },
            "both refreshed keys share one expiration, so they must land in a single L2 write");
    }

    [Fact]
    public async Task Only_keys_past_threshold_are_included()
    {
        var token = testContextAccessor.Current.CancellationToken;
        SeedAged("a", "A");
        _stored[(CacheKey)"b"] = "B";   // fresh: full 10-minute TTL, below threshold
        var rehydrateCalls = new List<long[]>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await Sut.GetOrAddAsync<string, long>(
            [new((CacheKey)"a", 1L), new((CacheKey)"b", 2L)],
            (ids, _) =>
            {
                lock (rehydrateCalls) { rehydrateCalls.Add(ids); }
                done.TrySetResult();
                return Task.FromResult(ids.Select(id => new KeyValuePair<long, string?>(id, "fresh")).ToArray());
            },
            RehydratePolicy(),
            token);

        await done.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
        Snapshot(rehydrateCalls).Single().Should().Equal(States1, "b is nowhere near its rehydrate threshold");
    }

    [Fact]
    public async Task Rehydrate_generator_receives_states_for_the_reserved_keys_only()
    {
        var token = testContextAccessor.Current.CancellationToken;
        SeedAged("user:1", "A");          // aged: past threshold
        _stored[(CacheKey)"user:2"] = "B"; // fresh: below threshold
        var calls = new List<long[]>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await Sut.GetOrAddAsync<string, long>(
            [new((CacheKey)"user:1", 1L), new((CacheKey)"user:2", 2L)],
            (ids, _) => { lock (calls) { calls.Add(ids); } done.TrySetResult(); return Task.FromResult(ids.Select(id => new KeyValuePair<long, string?>(id, "fresh")).ToArray()); },
            RehydratePolicy(),
            token);

        await done.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
        Snapshot(calls).Single().Should().Equal(States1, "only the aged entry's state is rehydrated, translated from the reserved key");
    }

    [Fact]
    public async Task Cached_null_is_not_rehydrated_when_CacheNullValues_is_on()
    {
        var token = testContextAccessor.Current.CancellationToken;
        SeedAged("a", null);            // cached-null marker: re-running the factory would just churn it
        SeedAged("b", "B");
        var rehydrateCalls = new List<long[]>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await Sut.GetOrAddAsync<string, long>(
            [new((CacheKey)"a", 1L), new((CacheKey)"b", 2L)],
            (ids, _) =>
            {
                lock (rehydrateCalls) { rehydrateCalls.Add(ids); }
                done.TrySetResult();
                return Task.FromResult(ids.Select(id => new KeyValuePair<long, string?>(id, "fresh")).ToArray());
            },
            RehydratePolicy(),
            token);

        await done.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
        Snapshot(rehydrateCalls).Single().Should().Equal(States2, "the cached-null guard is applied per key, and must not suppress the rest of the set");
    }

    [Fact]
    public async Task Rehydrate_is_not_triggered_when_the_policy_disables_it()
    {
        var token = testContextAccessor.Current.CancellationToken;
        SeedAged("a", "A");
        var rehydrateCalls = 0;

        await Sut.GetOrAddAsync<string, long>(
            [new((CacheKey)"a", 1L)],
            (ids, _) => { Interlocked.Increment(ref rehydrateCalls); return Task.FromResult(ids.Select(id => new KeyValuePair<long, string?>(id, "fresh")).ToArray()); },
            (CachePolicy?)null,
            token);

        await Task.Delay(200, token);
        Volatile.Read(ref rehydrateCalls).Should().Be(0);
        await _distributedLock.DidNotReceiveWithAnyArgs().TryAcquireAsync(default!, default, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Caller_explicit_TimeSpan_drives_the_batch_rehydrate_soft_TTL_not_the_policy()
    {
        var token = testContextAccessor.Current.CancellationToken;
        SeedAged("a", "A");
        var rehydrateCalls = 0;

        await Sut.GetOrAddAsync<string, long>(
            [new((CacheKey)"a", 1L)],
            (ids, _) => { Interlocked.Increment(ref rehydrateCalls); return Task.FromResult(ids.Select(id => new KeyValuePair<long, string?>(id, "fresh")).ToArray()); },
            TimeSpan.FromMinutes(2),
            RehydratePolicy(),
            token);

        await Task.Delay(200, token);
        Volatile.Read(ref rehydrateCalls).Should().Be(0, "soft-TTL math must use the caller's explicit TimeSpan, not the policy's DistributedExpiration");
        await _distributedLock.DidNotReceiveWithAnyArgs().TryAcquireAsync(default!, default, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_key_already_in_flight_is_not_rehydrated_twice()
    {
        var token = testContextAccessor.Current.CancellationToken;
        SeedAged("a", "A");
        SeedAged("b", "B");
        var rehydrateCalls = new List<long[]>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<KeyValuePair<long, string?>[]> Generator(long[] ids, CancellationToken ct)
        {
            lock (rehydrateCalls) { rehydrateCalls.Add(ids); }
            entered.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            return ids.Select(id => new KeyValuePair<long, string?>(id, "fresh")).ToArray();
        }

        await Sut.GetOrAddAsync<string, long>([new((CacheKey)"a", 1L)], Generator, RehydratePolicy(), token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
        await Sut.GetOrAddAsync<string, long>([new((CacheKey)"a", 1L), new((CacheKey)"b", 2L)], Generator, RehydratePolicy(), token);
        await WaitForAsync(() => Snapshot(rehydrateCalls).Count == 2, TimeSpan.FromSeconds(10), token);
        release.TrySetResult();

        var calls = Snapshot(rehydrateCalls);
        calls.Should().HaveCount(2);
        calls[0].Should().Equal(States1, "the first call rehydrates a on its own");
        calls[1].Should().Equal(States2, "a is still in flight, so only b is rehydrated by the second call");
    }

    [Fact]
    public async Task Rehydrated_values_are_written_with_a_refreshed_expiration()
    {
        var token = testContextAccessor.Current.CancellationToken;
        SeedAged("a", "A");
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await Sut.GetOrAddAsync<string, long>(
            [new((CacheKey)"a", 1L)],
            (ids, _) =>
            {
                var pairs = ids.Select(id => new KeyValuePair<long, string?>(id, "fresh")).ToArray();
                done.TrySetResult();
                return Task.FromResult(pairs);
            },
            RehydratePolicy(),
            token);

        await done.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
        await WaitForAsync(() => _stored.TryGetValue((CacheKey)"a", out var v) && v == "fresh", TimeSpan.FromSeconds(10), token);
        _innerSetCalls.ToArray().Single().Should().Equal(new[] { (CacheKey)"a" }, "the only inner write in this test comes from the rehydrate");
        var expirations = _innerSetExpirations.ToArray();
        expirations.Should().HaveCount(1);
        expirations[0].Should().NotBeNull();
        expirations[0]!.Value.Should().BeCloseTo(
            DateTimeOffset.UtcNow.Add(Duration),
            TimeSpan.FromMinutes(1),
            "a rehydrated non-null value gets a full fresh TTL window, not the aged entry's remaining 2 minutes");
    }

    [Fact]
    public async Task Batch_rehydrate_locks_on_the_caller_key_not_the_strategy_mapped_key()
    {
        var token = testContextAccessor.Current.CancellationToken;
        _cacheKeyStrategy.GetCacheKey<string>(Arg.Any<CacheKey>())
            .Returns(c => (CacheKey)("pfx:" + c.Arg<CacheKey>().Name));
        var lockKeyStrategy = new PrefixingLockKeyStrategy();
        _options.LockKeyStrategy = lockKeyStrategy;
        SeedAged("pfx:a", "A");     // L1/L2 address the entry by its mapped key
        var lockKeys = new ConcurrentQueue<string>();
        _distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(c =>
            {
                lockKeys.Enqueue(c.ArgAt<string>(0));
                return Substitute.For<IAsyncDisposable>();
            });
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var result = await Sut.GetOrAddAsync<string, long>(
            [new((CacheKey)"a", 1L)],
            (ids, _) =>
            {
                var pairs = ids.Select(id => new KeyValuePair<long, string?>(id, "fresh")).ToArray();
                done.TrySetResult();
                return Task.FromResult(pairs);
            },
            RehydratePolicy(),
            token);

        result.Select(r => r.Key).Should().Equal(States1, "the caller gets its own state back");
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
        await WaitForAsync(() => !lockKeys.IsEmpty, TimeSpan.FromSeconds(10), token);
        lockKeys.ToArray().Should().Equal(
            new[] { "rehydrate:" + lockKeyStrategy.GetLockKey((CacheKey)"a") },
            "a single-candidate batch rehydrate must dedupe against single-key rehydration of that key, which locks on the caller key");
    }

    [Fact]
    public async Task Batch_rehydrate_locks_on_the_reserved_keys_not_every_candidate()
    {
        var token = testContextAccessor.Current.CancellationToken;
        _cacheKeyStrategy.GetCacheKey<string>(Arg.Any<CacheKey>())
            .Returns(c => (CacheKey)("pfx:" + c.Arg<CacheKey>().Name));
        var lockKeyStrategy = new PrefixingLockKeyStrategy();
        _options.LockKeyStrategy = lockKeyStrategy;
        SeedAged("pfx:a", "A");             // aged: past threshold, so it is reserved
        _stored[(CacheKey)"pfx:b"] = "B";   // fresh: a candidate, but never reserved
        var lockKeys = new ConcurrentQueue<string>();
        _distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(c =>
            {
                lockKeys.Enqueue(c.ArgAt<string>(0));
                return Substitute.For<IAsyncDisposable>();
            });
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await Sut.GetOrAddAsync<string, long>(
            [new((CacheKey)"a", 1L), new((CacheKey)"b", 2L)],
            (ids, _) =>
            {
                var pairs = ids.Select(id => new KeyValuePair<long, string?>(id, "fresh")).ToArray();
                done.TrySetResult();
                return Task.FromResult(pairs);
            },
            RehydratePolicy(),
            token);

        await done.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
        await WaitForAsync(() => !lockKeys.IsEmpty, TimeSpan.FromSeconds(10), token);
        lockKeys.ToArray().Should().Equal(
            new[] { "rehydrate:" + lockKeyStrategy.GetLockKey((CacheKey)"a") },
            "the group key must be derived from the RESERVED set, so a batch that refreshes only `a` takes the same lock single-key rehydration of `a` takes");
    }

    private sealed class PrefixingLockKeyStrategy : IDistributedLockKeyStrategy
    {
        public string GetLockKey(CacheKey cacheKey) => "lck:" + cacheKey.Name;
    }

    [Fact]
    public async Task Batch_rehydrate_tags_telemetry_with_the_group_size()
    {
        var token = testContextAccessor.Current.CancellationToken;
        SeedAged("a", "A");
        SeedAged("b", "B");
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await Sut.GetOrAddAsync<string, long>(
            [new((CacheKey)"a", 1L), new((CacheKey)"b", 2L)],
            (ids, _) =>
            {
                var pairs = ids.Select(id => new KeyValuePair<long, string?>(id, "fresh")).ToArray();
                done.TrySetResult();
                return Task.FromResult(pairs);
            },
            RehydratePolicy(),
            token);

        await done.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
        await WaitForAsync(
            () => _telemetry.Events.Any(e => e.Name == "cache.rehydrate.succeeded"),
            TimeSpan.FromSeconds(10),
            token);
        var sizes = _telemetry.Events
            .Where(e => e.Properties is not null && e.Properties.ContainsKey("batch.size"))
            .Select(e => e.Properties!["batch.size"])
            .ToArray();
        sizes.Should().NotBeEmpty("a multi-key rehydrate must tag its telemetry with the group size");
        sizes.Should().AllBe("2", "the coalesced set had exactly two keys");
    }

    private static List<long[]> Snapshot(List<long[]> calls)
    {
        lock (calls) { return [.. calls]; }
    }

    private static async Task WaitForAsync(Func<bool> predicate, TimeSpan timeout, CancellationToken token)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (predicate())
            {
                return;
            }
            await Task.Delay(10, token);
        }
        throw new TimeoutException($"WaitForAsync timed out after {timeout} — predicate never became true. Background batch rehydrate likely never ran.");
    }

    public ValueTask InitializeAsync()
    {
        _topicKey = _fixture.Create<string>();
        _changeTokenFactory = _fixture.Freeze<IChangeTokenFactory>();
        _changeTokenFactory.Create(Arg.Any<string>(), Arg.Any<ITopic<ICacheEvent>>(), Arg.Any<string>(), Arg.Any<Type>())
            .Returns(_ => new TestChangeToken { ActiveChangeCallbacks = true, HasChanged = false });
        _memoryCache = new MemoryCache(Options.Create(new MemoryCacheOptions()));
        _innerCache = _fixture.Freeze<ICache>();
        _telemetry = new RecordingTelemetryProvider();
        _fixture.Inject<ICachingTelemetryProvider>(_telemetry);
        _options = new InMemoryRedisCacheOptions
        {
            DefaultExpiration = Duration,
            EntryFactory = new TestCacheEntryFactory(),
            CacheNullValues = true,
        };
        _locker = new AsyncKeyedLocalLock(Options.Create(new CacheOptions()));

        _cacheKeyStrategy = _fixture.Create<ICacheKeyStrategy>();
        _cacheKeyStrategy.GetCacheKey<string>(Arg.Any<CacheKey>()).Returns(c => c.Arg<CacheKey>());
        _options.CacheKeyStrategy = _cacheKeyStrategy;
        _topicKeyStrategy = _fixture.Create<ITopicKeyStrategy>();
        _topicKeyStrategy.GetTopicKey<string>().Returns(_topicKey);
        _options.TopicKeyStrategy = _topicKeyStrategy;
        _topicFactory = _fixture.Freeze<ITopicFactory>();
        _topicProvider = _fixture.Freeze<MultilayerCacheTests.ITopicProviderWithConnectionState>();
        _topic = _fixture.Freeze<ITopic<ICacheEvent>>();
        _topicFactory.Get(Arg.Any<string>()).Returns(_topicProvider);
        _topicProvider.Create(_topicKey).Returns(_topic);
        _topicProvider.IsConnected.Returns(true);
        _topic.PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>()).Returns(_ => true);
        _memoryCacheFactory = _fixture.Freeze<IMemoryCacheFactory>();
        _memoryCacheFactory.Get(Arg.Any<IMemoryCacheOptions>()).Returns(_ => _memoryCache);
        _distributedLock = _fixture.Freeze<IDistributedLock>();
        _distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(_ => Substitute.For<IAsyncDisposable>());
        _fixture.Inject<IMultilayerCacheOptions>(_options);
        _fixture.Inject<IMemoryCacheOptions>(_options);
        _fixture.Inject<ILocalLock>(_locker);
        _cacheEventFactory = _fixture.Freeze<ICacheEventFactory>();
        _cacheEventFactory.Create(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CacheEventData>(), Arg.Any<string?>())
            .Returns(c => new Broadcast.TestCacheEvent
            {
                Id = c.ArgAt<string?>(3),
                Data = c.Arg<CacheEventData>(),
                Type = c.ArgAt<string>(1),
            });

        _innerCache.GetCacheEntriesAsync<string>(Arg.Any<CacheKey[]>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(c => c.Arg<CacheKey[]>()!
                .Select(k => new KeyValuePair<CacheKey, ICacheEntry<string?>>(
                    k,
                    _stored.TryGetValue(k, out var v)
                        ? new TestCacheEntry<string?>
                        {
                            Value = v,
                            Expiration = DateTimeOffset.UtcNow.AddMinutes(_agedKeys.Contains(k) ? 2 : 10),
                            Found = true,
                        }
                        : new TestCacheEntry<string?> { Value = null, Expiration = DateTimeOffset.MinValue }))
                .ToArray());

        _innerCache.SetAsync<string?>(Arg.Any<KeyValuePair<CacheKey, string?>[]>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(c =>
            {
                var pairs = c.Arg<KeyValuePair<CacheKey, string?>[]>()!;
                _innerSetCalls.Enqueue(pairs.Select(p => p.Key).ToArray());
                _innerSetExpirations.Enqueue(c.ArgAt<DateTimeOffset?>(1));
                foreach (var pair in pairs) { _stored[pair.Key] = pair.Value; }
                return true;
            });

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _locker?.Dispose();
        _memoryCache?.Dispose();
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}
