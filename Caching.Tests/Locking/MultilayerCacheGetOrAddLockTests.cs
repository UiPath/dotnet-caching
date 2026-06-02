using Microsoft.Extensions.Caching.Memory;
using UiPath.Platform.Caching.Locking;
using UiPath.Platform.Caching.Tests.Broadcast;

namespace UiPath.Platform.Caching.Tests.Locking;

public class MultilayerCacheGetOrAddLockTests(ITestContextAccessor testContextAccessor) : IAsyncLifetime
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubstitute();

    private ICache _innerCache = default!;
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

    [Fact]
    public async Task GetOrAddAsync_serializes_concurrent_generator_invocations_for_the_same_key()
    {
        _options.LocalLockTimeout = TimeSpan.FromMinutes(1);
        _sut = null;

        var token = testContextAccessor.Current.CancellationToken;
        var cacheKey = _fixture.Create<string>();
        _cacheKeyStrategy.GetCacheKey<string>(cacheKey).Returns((CacheKey)cacheKey);

        string? storedValue = null;
        _innerCache.GetCacheEntryAsync<string>((CacheKey)cacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(_ => new TestCacheEntry<string?> { Value = storedValue });
        _innerCache.SetAsync<string?>((CacheKey)cacheKey, Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(c => { storedValue = c.Arg<string?>(); return true; });

        var generatorCalls = 0;
        var concurrentInGenerator = 0;
        var maxConcurrent = 0;
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var producedValue = "the-value";

        async Task<string?> Generator(CancellationToken ct)
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

        results.Should().AllSatisfy(r => r.Should().Be(producedValue));
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
            .Returns(new TestCacheEntry<string?> { Value = null });
        _innerCache.GetCacheEntryAsync<string>((CacheKey)keyB, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(new TestCacheEntry<string?> { Value = null });

        var concurrentInGenerator = 0;
        var maxConcurrent = 0;
        var startedCount = 0;
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<string?> Generator(CancellationToken ct)
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
            return "v";
        }

        var taskA = Task.Run(async () => await Sut.GetOrAddAsync(keyA, Generator, (CachePolicy?)null, token));
        var taskB = Task.Run(async () => await Sut.GetOrAddAsync(keyB, Generator, (CachePolicy?)null, token));
        await Task.WhenAll(taskA, taskB);

        maxConcurrent.Should().BeGreaterThan(1, "different keys should be able to run their generators concurrently");
    }

    [Fact]
    public async Task GetOrAddAsync_skips_distributed_lock_when_post_local_lock_re_read_hits()
    {
        var distributedLock = Substitute.For<IDistributedLock>();
        _fixture.Inject(distributedLock);
        _options.DistributedLockEnabled = true;
        _sut = null;

        var token = testContextAccessor.Current.CancellationToken;
        var cacheKey = _fixture.Create<string>();
        _cacheKeyStrategy.GetCacheKey<string>(cacheKey).Returns((CacheKey)cacheKey);

        var callCount = 0;
        _innerCache.GetCacheEntryAsync<string>((CacheKey)cacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Interlocked.Increment(ref callCount) == 1
                ? new TestCacheEntry<string?> { Value = null }
                : new TestCacheEntry<string?> { Value = "populated" });

        var generatorCalls = 0;
        Task<string?> Generator(CancellationToken ct)
        {
            Interlocked.Increment(ref generatorCalls);
            return Task.FromResult<string?>("generated");
        }

        var result = await Sut.GetOrAddAsync(cacheKey, Generator, (CachePolicy?)null, token);

        result.Should().Be("populated");
        generatorCalls.Should().Be(0, "post-local-lock re-read hits — the generator must not run");
        await distributedLock.DidNotReceive().AcquireAsync(
            Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetOrAddAsync_re_reads_cache_after_local_lock_timeout()
    {
        _options.LocalLockTimeout = TimeSpan.FromMilliseconds(200);
        _sut = null;

        var token = testContextAccessor.Current.CancellationToken;
        var cacheKey = _fixture.Create<string>();
        _cacheKeyStrategy.GetCacheKey<string>(cacheKey).Returns((CacheKey)cacheKey);
        _innerCache.GetCacheEntryAsync<string>((CacheKey)cacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(new TestCacheEntry<string?> { Value = null });

        var holderAcquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holderRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<string?> Holder(CancellationToken ct)
        {
            holderAcquired.TrySetResult();
            await holderRelease.Task.WaitAsync(ct);
            return "holder";
        }
        async Task<string?> Second(CancellationToken ct)
        {
            await Task.Delay(10, ct);
            return "second";
        }

        var holderTask = Task.Run(async () => await Sut.GetOrAddAsync(cacheKey, Holder, (CachePolicy?)null, token));
        await holderAcquired.Task.WaitAsync(TimeSpan.FromSeconds(2), token);

        await Sut.GetOrAddAsync(cacheKey, Second, (CachePolicy?)null, token);

        var calls = _innerCache.ReceivedCalls()
            .Count(c => c.GetMethodInfo().Name == nameof(ICache.GetCacheEntryAsync));
        calls.Should().BeGreaterThanOrEqualTo(4,
            "second caller should do an initial read + a post-timeout re-read; without the re-read on timeout the generator runs without checking whether another node populated L2 during the wait");

        holderRelease.TrySetResult();
        await holderTask;
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
            .Returns(new TestCacheEntry<string?> { Value = null });

        const int concurrentCallers = 8;
        var concurrentInGenerator = 0;
        var maxConcurrent = 0;
        var startedCount = 0;
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<string?> Generator(CancellationToken ct)
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
            return "v";
        }

        var tasks = Enumerable.Range(0, concurrentCallers)
            .Select(_ => Task.Run(async () => await Sut.GetOrAddAsync(cacheKey, Generator, (CachePolicy?)null, token)))
            .ToArray();
        await Task.WhenAll(tasks);

        maxConcurrent.Should().BeGreaterThan(1, "with single-flight off, generators should run concurrently");
    }

    [Fact]
    public void Ctor_throws_when_DistributedLockExpiry_is_zero_or_negative()
    {
        _options.DistributedLockExpiry = TimeSpan.Zero;
        _sut = null;
        var act = () => _ = Sut;
        act.Should().Throw<Exception>()
            .Which.GetBaseException()
            .Should().BeOfType<ArgumentOutOfRangeException>()
            .Which.Message.Should().Contain(nameof(IMultilayerCacheOptions.DistributedLockExpiry));
    }

    [Fact]
    public void Ctor_throws_when_DistributedLockTimeout_is_negative()
    {
        _options.DistributedLockTimeout = TimeSpan.FromMilliseconds(-1);
        _sut = null;
        var act = () => _ = Sut;
        act.Should().Throw<Exception>()
            .Which.GetBaseException()
            .Should().BeOfType<ArgumentOutOfRangeException>()
            .Which.Message.Should().Contain(nameof(IMultilayerCacheOptions.DistributedLockTimeout));
    }

    [Fact]
    public void Ctor_throws_when_LocalLockTimeout_is_zero_or_negative()
    {
        _options.LocalLockTimeout = TimeSpan.Zero;
        _sut = null;
        var act = () => _ = Sut;
        act.Should().Throw<Exception>()
            .Which.GetBaseException()
            .Should().BeOfType<ArgumentOutOfRangeException>()
            .Which.Message.Should().Contain(nameof(IMultilayerCacheOptions.LocalLockTimeout));
    }

    [Fact]
    public void Ctor_throws_when_LocalLockEnabled_false_with_DistributedLockEnabled_true()
    {
        _options.LocalLockEnabled = false;
        _options.DistributedLockEnabled = true;
        _sut = null;
        var act = () => _ = Sut;
        act.Should().Throw<Exception>()
            .Which.GetBaseException()
            .Should().BeOfType<ArgumentException>()
            .Which.Message.Should().Contain(nameof(IMultilayerCacheOptions.LocalLockEnabled));
    }

    public ValueTask InitializeAsync()
    {
        _topicKey = _fixture.Create<string>();
        _changeTokenFactory = _fixture.Freeze<IChangeTokenFactory>();
        _memoryCache = new MemoryCache(Options.Create(new MemoryCacheOptions()));
        _innerCache = _fixture.Freeze<ICache>();
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
