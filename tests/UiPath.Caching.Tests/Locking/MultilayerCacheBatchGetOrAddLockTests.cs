using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;
using UiPath.Caching.Locking;
using UiPath.Caching.Tests.Broadcast;

namespace UiPath.Caching.Tests.Locking;

public class MultilayerCacheBatchGetOrAddLockTests(ITestContextAccessor testContextAccessor) : IAsyncLifetime
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubstitute();

    private static readonly long[] States1 = [1L];
    private static readonly long[] States2 = [2L];
    private static readonly long[] States1And2 = [1L, 2L];
    private static readonly string?[] V1AndV2 = ["v:1", "v:2"];
    private static readonly string?[] RefilledAAndGen2 = ["refilled:a", "gen:2"];

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
    private AsyncKeyedLocalLock _locker = default!;
    private InMemoryRedisCacheOptions _options = default!;
    private TopicKey _topicKey = default!;
    private MultilayerCache? _sut;

    private readonly object _sutLock = new();
    private MultilayerCache Sut
    {
        get
        {
            if (_sut is not null) return _sut;
            lock (_sutLock) { return _sut ??= _fixture.Create<MultilayerCache>(); }
        }
    }

    private readonly ConcurrentDictionary<CacheKey, string?> _stored = new();

    [Fact]
    public async Task Concurrent_identical_batches_invoke_the_generator_once()
    {
        var token = testContextAccessor.Current.CancellationToken;
        KeyValuePair<CacheKey, long>[] entries = [new((CacheKey)"a", 1L), new((CacheKey)"b", 2L)];
        var generatorCalls = 0;
        var concurrent = 0;
        var maxConcurrent = 0;
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<KeyValuePair<long, string?>[]> Generator(long[] requested, CancellationToken ct)
        {
            var inside = Interlocked.Increment(ref concurrent);
            int observed;
            do { observed = Volatile.Read(ref maxConcurrent); if (inside <= observed) break; }
            while (Interlocked.CompareExchange(ref maxConcurrent, inside, observed) != observed);
            firstEntered.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            Interlocked.Increment(ref generatorCalls);
            Interlocked.Decrement(ref concurrent);
            return requested.Select(id => new KeyValuePair<long, string?>(id, "v:" + id)).ToArray();
        }

        var tasks = Enumerable.Range(0, 16)
            .Select(_ => Task.Run(async () => await Sut.GetOrAddAsync<string, long>(entries, Generator, (CachePolicy?)null, token)))
            .ToArray();

        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
        release.TrySetResult();
        var results = await Task.WhenAll(tasks);

        maxConcurrent.Should().Be(1, "the composite lock must serialize generator invocations for the same miss set");
        generatorCalls.Should().Be(1, "the post-lock re-probe must find the first caller's values");
        results.Should().AllSatisfy(r => r.Select(p => p.Value).Should().Equal(V1AndV2));
    }

    [Fact]
    public async Task Disjoint_miss_sets_take_different_composite_locks()
    {
        var token = testContextAccessor.Current.CancellationToken;
        var observed = new List<long[]>();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<KeyValuePair<long, string?>[]> Blocking(long[] requested, CancellationToken ct)
        {
            lock (observed) { observed.Add(requested); }
            firstEntered.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            return requested.Select(id => new KeyValuePair<long, string?>(id, "v:" + id)).ToArray();
        }

        Task<KeyValuePair<long, string?>[]> Fast(long[] requested, CancellationToken _)
        {
            lock (observed) { observed.Add(requested); }
            return Task.FromResult(requested.Select(id => new KeyValuePair<long, string?>(id, "v:" + id)).ToArray());
        }

        var first = Task.Run(async () => await Sut.GetOrAddAsync<string, long>([new((CacheKey)"a", 1L)], Blocking, (CachePolicy?)null, token));
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
        var second = await Sut.GetOrAddAsync<string, long>([new((CacheKey)"a", 1L), new((CacheKey)"b", 2L)], Fast, (CachePolicy?)null, token);
        release.TrySetResult();
        await first;

        second.Select(p => p.Value).Should().Equal(V1AndV2);
        observed.Should().HaveCount(2, "disjoint miss sets take different locks — the documented limitation");
        observed[0].Should().Equal(States1, "the first caller's only miss is 'a'");
        observed[1].Should().Equal(States1And2,
            "the second caller took a different lock, so 'a' was still missing when its generator ran");
    }

    [Fact]
    public async Task Re_probe_under_the_lock_shrinks_the_generator_key_set()
    {
        var token = testContextAccessor.Current.CancellationToken;

        var probeRounds = 0;
        _innerCache.GetCacheEntriesAsync<string>(Arg.Any<CacheKey[]>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(c =>
            {
                var round = Interlocked.Increment(ref probeRounds);
                return c.Arg<CacheKey[]>()!
                    .Select(k => new KeyValuePair<CacheKey, ICacheEntry<string?>>(
                        k,
                        round >= 2 && k == (CacheKey)"a"
                            ? new TestCacheEntry<string?> { Value = "refilled:a", Expiration = DateTimeOffset.UtcNow.AddMinutes(10), Found = true }
                            : new TestCacheEntry<string?> { Value = null, Expiration = DateTimeOffset.MinValue }))
                    .ToArray();
            });

        var observed = new List<long[]>();

        var result = await Sut.GetOrAddAsync<string, long>(
            [new((CacheKey)"a", 1L), new((CacheKey)"b", 2L)],
            (requested, _) =>
            {
                observed.Add(requested);
                return Task.FromResult(requested.Select(id => new KeyValuePair<long, string?>(id, "gen:" + id)).ToArray());
            },
            (CachePolicy?)null,
            token);

        probeRounds.Should().Be(2, "one pre-lock probe and one re-probe under the lock");
        observed.Single().Should().Equal(States2,
            "the re-probe found 'a', so the generator must be narrowed to the states whose keys are still missing");
        result.Select(r => r.Key).Should().Equal(States1And2);
        result.Select(r => r.Value).Should().Equal(RefilledAAndGen2);
    }

    [Fact]
    public async Task Single_miss_batch_serializes_with_single_key_GetOrAdd()
    {
        var token = testContextAccessor.Current.CancellationToken;
        var concurrent = 0;
        var maxConcurrent = 0;
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;

        async Task Track(CancellationToken ct)
        {
            var inside = Interlocked.Increment(ref concurrent);
            int observed;
            do { observed = Volatile.Read(ref maxConcurrent); if (inside <= observed) break; }
            while (Interlocked.CompareExchange(ref maxConcurrent, inside, observed) != observed);
            if (Interlocked.Increment(ref started) == 2) { bothStarted.TrySetResult(); }
            await Task.WhenAny(bothStarted.Task, Task.Delay(TimeSpan.FromSeconds(2), ct)).ConfigureAwait(false);
            Interlocked.Decrement(ref concurrent);
        }

        var batch = Task.Run(async () => await Sut.GetOrAddAsync<string, long>(
            [new((CacheKey)"a", 1L)],
            async (ids, ct) => { await Track(ct); return ids.Select(id => new KeyValuePair<long, string?>(id, "v")).ToArray(); },
            (CachePolicy?)null, token));
        var single = Task.Run(async () => await Sut.GetOrAddAsync<string>(
            (CacheKey)"a",
            async ct => { await Track(ct); return "v"; },
            (CachePolicy?)null, token));

        await Task.WhenAll(batch, single);

        maxConcurrent.Should().Be(1, "a single-miss batch must lock on the key itself, not a composite");
    }

    public ValueTask InitializeAsync()
    {
        _topicKey = _fixture.Create<string>();
        _changeTokenFactory = _fixture.Freeze<IChangeTokenFactory>();
        _changeTokenFactory.Create(Arg.Any<string>(), Arg.Any<ITopic<ICacheEvent>>(), Arg.Any<string>(), Arg.Any<Type>())
            .Returns(_ => new TestChangeToken { ActiveChangeCallbacks = true, HasChanged = false });
        _memoryCache = new MemoryCache(Options.Create(new MemoryCacheOptions()));
        _innerCache = _fixture.Freeze<ICache>();
        _options = new InMemoryRedisCacheOptions
        {
            DefaultExpiration = TimeSpan.FromMinutes(10),
            EntryFactory = new TestCacheEntryFactory(),
            CacheNullValues = true,
            LocalLockTimeout = TimeSpan.FromMinutes(1),
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
        _memoryCacheFactory = _fixture.Freeze<IMemoryCacheFactory>();
        _memoryCacheFactory.Get(Arg.Any<IMemoryCacheOptions>()).Returns(_ => _memoryCache);
        _fixture.Inject<IMultilayerCacheOptions>(_options);
        _fixture.Inject<IMemoryCacheOptions>(_options);
        _fixture.Inject<ILocalLock>(_locker);
        _cacheEventFactory = _fixture.Freeze<ICacheEventFactory>();
        _cacheEventFactory.Create(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CacheEventData>(), Arg.Any<string?>())
            .Returns(c => new TestCacheEvent
            {
                Id = c.ArgAt<string?>(3),
                Data = c.Arg<CacheEventData>(),
                Type = c.ArgAt<string>(1),
            });

        _innerCache.GetCacheEntriesAsync<string>(Arg.Any<CacheKey[]>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(c => c.Arg<CacheKey[]>()!
                .Select(k => new KeyValuePair<CacheKey, ICacheEntry<string?>>(k, Entry(k)))
                .ToArray());

        _innerCache.GetCacheEntryAsync<string>(Arg.Any<CacheKey>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns<ICacheEntry<string?>>(c => Entry(c.Arg<CacheKey>()));

        _innerCache.SetAsync<string?>(Arg.Any<KeyValuePair<CacheKey, string?>[]>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(c =>
            {
                foreach (var pair in c.Arg<KeyValuePair<CacheKey, string?>[]>()!) { _stored[pair.Key] = pair.Value; }
                return true;
            });

        _innerCache.SetAsync<string?>(Arg.Any<CacheKey>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(c =>
            {
                _stored[c.Arg<CacheKey>()] = c.ArgAt<string?>(1);
                return true;
            });

        return ValueTask.CompletedTask;
    }

    private TestCacheEntry<string?> Entry(CacheKey key) =>
        _stored.TryGetValue(key, out var value)
            ? new TestCacheEntry<string?> { Value = value, Expiration = DateTimeOffset.UtcNow.AddMinutes(10), Found = true }
            : new TestCacheEntry<string?> { Value = null, Expiration = DateTimeOffset.MinValue };

    public ValueTask DisposeAsync()
    {
        _locker?.Dispose();
        _memoryCache?.Dispose();
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}
