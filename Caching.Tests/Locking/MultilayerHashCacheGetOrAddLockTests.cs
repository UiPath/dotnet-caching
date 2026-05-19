using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Logging;
using UiPath.Platform.Caching.Locking;
using UiPath.Platform.Caching.Tests.Broadcast;

namespace UiPath.Platform.Caching.Tests.Locking;

public class MultilayerHashCacheGetOrAddLockTests(ITestContextAccessor testContextAccessor) : IAsyncLifetime
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubstitute();

    private IHashCache _innerCache = default!;
    private IChangeTokenFactory _changeTokenFactory = default!;
    private ITopicFactory _topicFactory = default!;
    private MultilayerCacheTests.ITopicProviderWithConnectionState _topicProvider = default!;
    private ITopic<ICacheEvent> _topic = default!;
    private ICacheKeyStrategy _cacheKeyStrategy = default!;
    private ITopicKeyStrategy _topicKeyStrategy = default!;
    private IMemoryCache _memoryCache = default!;
    private ICacheEventFactory _cacheEventFactory = default!;
    private IMemoryCacheFactory _memoryCacheFactory = default!;
    private InMemoryRedisCacheOptions _options = default!;
    private TopicKey _topicKey = default!;
    private AsyncKeyedLocalLock _locker = default!;

    private MultilayerHashCache? _sut;
    private readonly object _sutLock = new();
    private MultilayerHashCache Sut
    {
        get
        {
            if (_sut is not null) return _sut;
            lock (_sutLock) { return _sut ??= _fixture.Create<MultilayerHashCache>(); }
        }
    }

    [Fact]
    public async Task GetOrAddAsync_runs_generator_exactly_once_under_concurrent_callers_for_the_same_key()
    {
        _options.LocalLockTimeout = TimeSpan.FromMinutes(1);
        _sut = null;

        var token = testContextAccessor.Current.CancellationToken;
        var cacheKey = _fixture.Create<string>();
        _cacheKeyStrategy.GetCacheKey<string>(cacheKey).Returns((CacheKey)cacheKey);

        IDictionary<string, string?>? storedValue = null;
        _innerCache.GetCacheEntryAsync<string>((CacheKey)cacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(_ => new TestCacheEntry<IDictionary<string, string?>> { Value = storedValue });
        _innerCache.SetAsync<string?>(
                (CacheKey)cacheKey,
                Arg.Any<IDictionary<string, string?>>(),
                Arg.Any<HashCacheEntryOptions>(),
                Arg.Any<CachePolicy?>(),
                Arg.Any<CancellationToken>())
            .Returns(c => { storedValue = c.Arg<IDictionary<string, string?>>(); return true; });

        var generatorCalls = 0;
        var concurrentInGenerator = 0;
        var maxConcurrent = 0;
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var producedValue = new Dictionary<string, string?> { ["k"] = "v" };

        async Task<IDictionary<string, string?>> Generator(CancellationToken ct)
        {
            var inside = Interlocked.Increment(ref concurrentInGenerator);
            int observed;
            do { observed = Volatile.Read(ref maxConcurrent); if (inside <= observed) break; }
            while (Interlocked.CompareExchange(ref maxConcurrent, inside, observed) != observed);
            firstEntered.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            Interlocked.Increment(ref generatorCalls);
            Interlocked.Decrement(ref concurrentInGenerator);
            return producedValue;
        }

        var tasks = Enumerable.Range(0, 32)
            .Select(_ => Task.Run(async () => await Sut.GetOrAddAsync(cacheKey, Generator, (CachePolicy?)null, token)))
            .ToArray();

        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
        release.TrySetResult();

        var results = await Task.WhenAll(tasks);

        results.Should().AllSatisfy(r => r.Should().BeEquivalentTo(producedValue));
        maxConcurrent.Should().Be(1, "the local locker must serialize generator invocations for the same key");
        generatorCalls.Should().Be(1, "single-flight should run the generator exactly once when the first call populates the inner cache");
    }

    [Fact]
    public async Task GetOrAddAsync_does_not_block_concurrent_callers_on_different_keys()
    {
        var token = testContextAccessor.Current.CancellationToken;
        var keyA = _fixture.Create<string>();
        var keyB = _fixture.Create<string>();
        _cacheKeyStrategy.GetCacheKey<string>(keyA).Returns((CacheKey)keyA);
        _cacheKeyStrategy.GetCacheKey<string>(keyB).Returns((CacheKey)keyB);

        _innerCache.GetCacheEntryAsync<string>((CacheKey)keyA, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(new TestCacheEntry<IDictionary<string, string?>> { Value = null });
        _innerCache.GetCacheEntryAsync<string>((CacheKey)keyB, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(new TestCacheEntry<IDictionary<string, string?>> { Value = null });

        var concurrentInGenerator = 0;
        var maxConcurrent = 0;
        var startedCount = 0;
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<IDictionary<string, string?>> Generator(CancellationToken ct)
        {
            var inside = Interlocked.Increment(ref concurrentInGenerator);
            int observed;
            do { observed = Volatile.Read(ref maxConcurrent); if (inside <= observed) break; }
            while (Interlocked.CompareExchange(ref maxConcurrent, inside, observed) != observed);

            if (Interlocked.Increment(ref startedCount) == 2)
            {
                bothStarted.TrySetResult();
            }
            await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);

            Interlocked.Decrement(ref concurrentInGenerator);
            return new Dictionary<string, string?> { ["k"] = "v" };
        }

        var taskA = Task.Run(async () => await Sut.GetOrAddAsync(keyA, Generator, (CachePolicy?)null, token));
        var taskB = Task.Run(async () => await Sut.GetOrAddAsync(keyB, Generator, (CachePolicy?)null, token));
        await Task.WhenAll(taskA, taskB);

        maxConcurrent.Should().BeGreaterThan(1, "different keys should be able to run their generators concurrently");
    }

    [Fact]
    public async Task GetOrAddAsync_falls_through_to_generator_when_local_lock_is_disabled()
    {
        _fixture.Inject<ILocalLock>(NullLocalLock.Instance);
        _options.LocalLockEnabled = false;
        _sut = null;

        var token = testContextAccessor.Current.CancellationToken;
        var cacheKey = _fixture.Create<string>();
        _cacheKeyStrategy.GetCacheKey<string>(cacheKey).Returns((CacheKey)cacheKey);
        _innerCache.GetCacheEntryAsync<string>((CacheKey)cacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(new TestCacheEntry<IDictionary<string, string?>> { Value = null });

        const int concurrentCallers = 8;
        var concurrentInGenerator = 0;
        var maxConcurrent = 0;
        var startedCount = 0;
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<IDictionary<string, string?>> Generator(CancellationToken ct)
        {
            var inside = Interlocked.Increment(ref concurrentInGenerator);
            int observed;
            do { observed = Volatile.Read(ref maxConcurrent); if (inside <= observed) break; }
            while (Interlocked.CompareExchange(ref maxConcurrent, inside, observed) != observed);

            if (Interlocked.Increment(ref startedCount) == concurrentCallers)
            {
                allStarted.TrySetResult();
            }
            await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);

            Interlocked.Decrement(ref concurrentInGenerator);
            return new Dictionary<string, string?> { ["k"] = "v" };
        }

        var tasks = Enumerable.Range(0, concurrentCallers)
            .Select(_ => Task.Run(async () => await Sut.GetOrAddAsync(cacheKey, Generator, (CachePolicy?)null, token)))
            .ToArray();
        await Task.WhenAll(tasks);

        maxConcurrent.Should().BeGreaterThan(1, "with single-flight off, generators should run concurrently");
    }

    public ValueTask InitializeAsync()
    {
        _topicKey = _fixture.Create<string>();
        _changeTokenFactory = _fixture.Freeze<IChangeTokenFactory>();
        _memoryCache = new MemoryCache(Options.Create(new MemoryCacheOptions()));
        _innerCache = _fixture.Freeze<IHashCache>();
        _options = new InMemoryRedisCacheOptions
        {
            DefaultExpiration = TimeSpan.FromMinutes(10),
            EntryFactory = new TestCacheEntryFactory(),
        };
        _locker = new AsyncKeyedLocalLock(Options.Create(new CacheOptions()));

        _cacheKeyStrategy = _fixture.Create<ICacheKeyStrategy>();
        _topicKeyStrategy = _fixture.Create<ITopicKeyStrategy>();
        _topicKeyStrategy.GetTopicKey<string>().Returns(_topicKey);
        _topicFactory = _fixture.Freeze<ITopicFactory>();
        _topicProvider = _fixture.Freeze<MultilayerCacheTests.ITopicProviderWithConnectionState>();
        _topic = _fixture.Freeze<ITopic<ICacheEvent>>();
        _topicFactory.Get(Arg.Any<string>()).Returns(_topicProvider);
        _topicProvider.Create(_topicKey).Returns(_topic);
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
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _locker?.Dispose();
        _memoryCache?.Dispose();
        return ValueTask.CompletedTask;
    }
}
