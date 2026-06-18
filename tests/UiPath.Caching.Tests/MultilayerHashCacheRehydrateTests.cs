using UiPath.Caching.Locking;

namespace UiPath.Caching.Tests;

public class MultilayerHashCacheRehydrateTests(ITestContextAccessor testContextAccessor) : IAsyncLifetime
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubstitute();

    private IHashCache _innerCache = default!;
    private global::Microsoft.Extensions.Caching.Memory.IMemoryCache _memoryCache = default!;
    private ICacheKeyStrategy _cacheKeyStrategy = default!;
    private ITopicKeyStrategy _topicKeyStrategy = default!;
    private ITopicFactory _topicFactory = default!;
    private MultilayerCachePerNameLockTests.ITopicProviderWithConnectionState _topicProvider = default!;
    private ITopic<ICacheEvent> _topic = default!;
    private global::UiPath.Caching.IMemoryCacheFactory _memoryCacheFactory = default!;
    private ICacheEventFactory _cacheEventFactory = default!;
    private ILocalLock _localLock = default!;
    private IDistributedLock _distributedLock = default!;
    private InMemoryRedisCacheOptions _options = default!;
    private CacheKey _cacheKey = default!;
    private TopicKey _topicKey = default!;
    private MultilayerHashCache? _sut;

    private MultilayerHashCache Sut => _sut ??= _fixture.Create<MultilayerHashCache>();

    private static readonly TimeSpan Duration = TimeSpan.FromMinutes(10);
    private static readonly IDictionary<string, string?> CachedDict = new Dictionary<string, string?> { ["f"] = "cached" };
    private static readonly IDictionary<string, string?> RefreshedDict = new Dictionary<string, string?> { ["f"] = "rehydrated" };

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
            Name = "test-hash-profile",
        },
    };

    [Fact]
    public async Task Hit_before_threshold_does_not_trigger_rehydrate()
    {
        var token = testContextAccessor.Current.CancellationToken;
        var fresh = new TestCacheEntry<IDictionary<string, string?>> { Value = CachedDict, Expiration = DateTimeOffset.UtcNow.Add(Duration) };
        _innerCache.GetCacheEntryAsync<string>(_cacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()).Returns(fresh);

        var generatorCalls = 0;
        Func<CancellationToken, Task<IDictionary<string, string?>>> generator = _ =>
        {
            Interlocked.Increment(ref generatorCalls);
            return Task.FromResult(RefreshedDict);
        };

        var result = await Sut.GetOrAddAsync(_cacheKey, generator, RehydratePolicy(), token);

        result.Should().BeEquivalentTo(CachedDict);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        generatorCalls.Should().Be(0);
        await _distributedLock.DidNotReceiveWithAnyArgs().TryAcquireAsync(default!, default, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Hit_past_threshold_acquires_distributed_lock_and_invokes_generator()
    {
        var token = testContextAccessor.Current.CancellationToken;
        var ttl = TimeSpan.FromMinutes(2);
        var aged = new TestCacheEntry<IDictionary<string, string?>> { Value = CachedDict, Expiration = DateTimeOffset.UtcNow.Add(ttl) };
        _innerCache.GetCacheEntryAsync<string>(_cacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()).Returns(aged);

        var generatorTcs = new TaskCompletionSource<IDictionary<string, string?>>(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<CancellationToken, Task<IDictionary<string, string?>>> generator = _ => generatorTcs.Task;

        var acquiredLock = Substitute.For<IAsyncDisposable>();
        _distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(acquiredLock);

        var result = await Sut.GetOrAddAsync(_cacheKey, generator, RehydratePolicy(), token);

        result.Should().BeEquivalentTo(CachedDict);

        await WaitForAsync(() => _distributedLock.ReceivedCalls().Any(), TimeSpan.FromSeconds(5), token);
        await _distributedLock.ReceivedWithAnyArgs(1).TryAcquireAsync(default!, default, Arg.Any<CancellationToken>());
        generatorTcs.TrySetResult(RefreshedDict);
    }

    [Fact]
    public async Task Rehydrate_writes_value_back_through_inner_cache_with_KeyReplace_on_success()
    {
        var token = testContextAccessor.Current.CancellationToken;
        var ttl = TimeSpan.FromMinutes(2);
        var aged = new TestCacheEntry<IDictionary<string, string?>> { Value = CachedDict, Expiration = DateTimeOffset.UtcNow.Add(ttl) };
        _innerCache.GetCacheEntryAsync<string>(_cacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()).Returns(aged);

        Func<CancellationToken, Task<IDictionary<string, string?>>> generator = _ => Task.FromResult(RefreshedDict);

        var acquiredLock = Substitute.For<IAsyncDisposable>();
        _distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(acquiredLock);
        _innerCache.SetAsync<string?>(_cacheKey, Arg.Any<IDictionary<string, string?>>(), Arg.Any<HashCacheEntryOptions>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        await Sut.GetOrAddAsync(_cacheKey, generator, RehydratePolicy(), token);

        await WaitForAsync(() => _innerCache.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(IHashCache.SetAsync)), TimeSpan.FromSeconds(5), token);
        await _innerCache.Received(1).SetAsync<string?>(
            _cacheKey,
            Arg.Is<IDictionary<string, string?>>(d => d["f"] == "rehydrated"),
            Arg.Is<HashCacheEntryOptions>(o => o.SetOption == HashCacheSetOption.KeyReplace),
            Arg.Any<CachePolicy?>(),
            Arg.Any<CancellationToken>());
        await acquiredLock.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task Rehydrate_skipped_when_distributed_lock_unavailable()
    {
        var token = testContextAccessor.Current.CancellationToken;
        var ttl = TimeSpan.FromMinutes(2);
        var aged = new TestCacheEntry<IDictionary<string, string?>> { Value = CachedDict, Expiration = DateTimeOffset.UtcNow.Add(ttl) };
        _innerCache.GetCacheEntryAsync<string>(_cacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()).Returns(aged);

        var generatorCalls = 0;
        Func<CancellationToken, Task<IDictionary<string, string?>>> generator = _ =>
        {
            Interlocked.Increment(ref generatorCalls);
            return Task.FromResult(RefreshedDict);
        };

        _distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(default(IAsyncDisposable));

        await Sut.GetOrAddAsync(_cacheKey, generator, RehydratePolicy(), token);

        await WaitForAsync(() => _distributedLock.ReceivedCalls().Any(), TimeSpan.FromSeconds(5), token);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        generatorCalls.Should().Be(0);
        await _innerCache.DidNotReceive().SetAsync<string?>(_cacheKey, Arg.Any<IDictionary<string, string?>>(), Arg.Any<HashCacheEntryOptions>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rehydrate_does_not_release_lock_on_generator_failure()
    {
        var token = testContextAccessor.Current.CancellationToken;
        var ttl = TimeSpan.FromMinutes(2);
        var aged = new TestCacheEntry<IDictionary<string, string?>> { Value = CachedDict, Expiration = DateTimeOffset.UtcNow.Add(ttl) };
        _innerCache.GetCacheEntryAsync<string>(_cacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()).Returns(aged);

        Func<CancellationToken, Task<IDictionary<string, string?>>> generator = _ => throw new InvalidOperationException("boom");

        var acquiredLock = Substitute.For<IAsyncDisposable>();
        _distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(acquiredLock);

        await Sut.GetOrAddAsync(_cacheKey, generator, RehydratePolicy(), token);

        await WaitForAsync(() => _distributedLock.ReceivedCalls().Any(), TimeSpan.FromSeconds(5), token);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        await acquiredLock.DidNotReceive().DisposeAsync();
        await _innerCache.DidNotReceive().SetAsync<string?>(_cacheKey, Arg.Any<IDictionary<string, string?>>(), Arg.Any<HashCacheEntryOptions>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rehydrate_disabled_when_policy_RehydrateEnabled_is_null_or_false()
    {
        var token = testContextAccessor.Current.CancellationToken;
        var ttl = TimeSpan.FromMinutes(2);
        var aged = new TestCacheEntry<IDictionary<string, string?>> { Value = CachedDict, Expiration = DateTimeOffset.UtcNow.Add(ttl) };
        _innerCache.GetCacheEntryAsync<string>(_cacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()).Returns(aged);

        var generatorCalls = 0;
        Func<CancellationToken, Task<IDictionary<string, string?>>> generator = _ =>
        {
            Interlocked.Increment(ref generatorCalls);
            return Task.FromResult(RefreshedDict);
        };

        var policy = new CachePolicy
        {
            DistributedExpiration = Duration,
            RehydrateEnabled = false,
            Rehydrate = new RehydrateOptions { Threshold = 0.75 },
        };

        await Sut.GetOrAddAsync(_cacheKey, generator, policy, token);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        generatorCalls.Should().Be(0);
        await _distributedLock.DidNotReceiveWithAnyArgs().TryAcquireAsync(default!, default, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetAsync_with_options_lacking_expiry_uses_policy_DistributedExpiration()
    {
        var token = testContextAccessor.Current.CancellationToken;
        var policyTtl = TimeSpan.FromMinutes(7);
        var policy = new CachePolicy { DistributedExpiration = policyTtl };
        var values = new Dictionary<string, string?> { ["k"] = "v" };
        // Options carries only metadata — neither ExpireTime nor TimeToLive set. The cache-wide
        // DefaultExpiration is 10 minutes (Duration); the resolved policy says 7 minutes. Inner
        // SetAsync must receive the policy's value, not the cache-wide fallback.
        var opts = new HashCacheEntryOptions(Metadata: new Dictionary<string, string?> { ["m"] = "1" });
        _innerCache.SetAsync<string?>(_cacheKey, Arg.Any<IDictionary<string, string?>>(), Arg.Any<HashCacheEntryOptions>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _topic.PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>())
            .Returns(_ => true);

        await Sut.SetAsync(_cacheKey, values, opts, policy: policy, token: token);

        await _innerCache.Received(1).SetAsync<string?>(
            _cacheKey,
            Arg.Any<IDictionary<string, string?>>(),
            Arg.Is<HashCacheEntryOptions>(o =>
                o.ExpireTime.HasValue
                && o.ExpireTime.Value - DateTimeOffset.UtcNow > policyTtl - TimeSpan.FromSeconds(5)
                && o.ExpireTime.Value - DateTimeOffset.UtcNow < policyTtl + TimeSpan.FromSeconds(5)),
            Arg.Any<CachePolicy?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rehydrate_publishes_broadcast_so_other_nodes_invalidate_their_L1()
    {
        // Without the broadcast, peer nodes keep serving the pre-rehydrate L1 value until natural
        // expiry. The rehydrate write must publish a CacheSet event the same way a normal SetAsync does.
        var token = testContextAccessor.Current.CancellationToken;
        var ttl = TimeSpan.FromMinutes(2);
        var aged = new TestCacheEntry<IDictionary<string, string?>> { Value = CachedDict, Expiration = DateTimeOffset.UtcNow.Add(ttl) };
        _innerCache.GetCacheEntryAsync<string>(_cacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()).Returns(aged);

        Func<CancellationToken, Task<IDictionary<string, string?>>> generator = _ => Task.FromResult(RefreshedDict);

        var acquiredLock = Substitute.For<IAsyncDisposable>();
        _distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(acquiredLock);
        _innerCache.SetAsync<string?>(_cacheKey, Arg.Any<IDictionary<string, string?>>(), Arg.Any<HashCacheEntryOptions>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        await Sut.GetOrAddAsync(_cacheKey, generator, RehydratePolicy(), token);

        await WaitForAsync(() => _topic.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(ITopic<ICacheEvent>.PublishAsync)), TimeSpan.FromSeconds(5), token);
        await _topic.ReceivedWithAnyArgs(1).PublishAsync(default!, token);
    }

    [Fact]
    public async Task Rehydrate_skipped_when_current_hash_is_cached_empty_and_CacheNullValues_true()
    {
        // Circuit-breaker A (hash variant): existing entry is the cached-empty marker
        // (empty dict + Found=true under CacheNullValues); skip the rehydrate stampede
        // because the factory would just produce the same empty dict.
        _options.CacheNullValues = true;
        var token = testContextAccessor.Current.CancellationToken;
        var ttl = TimeSpan.FromMinutes(2);
        var empty = new Dictionary<string, string?>();
        var aged = new TestCacheEntry<IDictionary<string, string?>> { Value = empty, Found = true, Expiration = DateTimeOffset.UtcNow.Add(ttl) };
        _innerCache.GetCacheEntryAsync<string>(_cacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()).Returns(aged);

        var generatorCalls = 0;
        Func<CancellationToken, Task<IDictionary<string, string?>>> generator = _ =>
        {
            Interlocked.Increment(ref generatorCalls);
            return Task.FromResult(RefreshedDict);
        };

        await Sut.GetOrAddAsync(_cacheKey, generator, RehydratePolicy(), token);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        generatorCalls.Should().Be(0);
        await _distributedLock.DidNotReceiveWithAnyArgs().TryAcquireAsync(default!, default, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rehydrate_with_factory_returning_empty_preserves_original_entry_expiration()
    {
        // Circuit-breaker B (hash variant): factory returns an empty dict and we cache nulls;
        // the rehydrate write must reuse the original deadline so a stale empty entry expires
        // at its natural time instead of being extended forever.
        _options.CacheNullValues = true;
        var token = testContextAccessor.Current.CancellationToken;
        var ttl = TimeSpan.FromMinutes(2);
        var originalDeadline = DateTimeOffset.UtcNow.Add(ttl);
        var aged = new TestCacheEntry<IDictionary<string, string?>> { Value = CachedDict, Expiration = originalDeadline };
        _innerCache.GetCacheEntryAsync<string>(_cacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()).Returns(aged);

        Func<CancellationToken, Task<IDictionary<string, string?>>> generator =
            _ => Task.FromResult<IDictionary<string, string?>>(new Dictionary<string, string?>());

        var acquiredLock = Substitute.For<IAsyncDisposable>();
        _distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(acquiredLock);
        _innerCache.SetAsync<string?>(_cacheKey, Arg.Any<IDictionary<string, string?>>(), Arg.Any<HashCacheEntryOptions>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        await Sut.GetOrAddAsync(_cacheKey, generator, RehydratePolicy(), token);

        await WaitForAsync(() => _innerCache.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(IHashCache.SetAsync)), TimeSpan.FromSeconds(5), token);
        await _innerCache.Received(1).SetAsync<string?>(
            _cacheKey,
            Arg.Any<IDictionary<string, string?>>(),
            Arg.Is<HashCacheEntryOptions>(o =>
                o.ExpireTime.HasValue
                && Math.Abs((o.ExpireTime.Value - originalDeadline).TotalSeconds) < 1),
            Arg.Any<CachePolicy?>(),
            Arg.Any<CancellationToken>());
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
        throw new TimeoutException($"WaitForAsync timed out after {timeout} — predicate never became true. Background rehydrate path likely never ran.");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public ValueTask InitializeAsync()
    {
        _cacheKey = _fixture.Create<string>();
        _topicKey = _fixture.Create<string>();
        _innerCache = _fixture.Freeze<IHashCache>();
        _memoryCache = _fixture.Freeze<global::Microsoft.Extensions.Caching.Memory.IMemoryCache>();
        _localLock = _fixture.Freeze<ILocalLock>();
        _distributedLock = _fixture.Freeze<IDistributedLock>();
        _options = new InMemoryRedisCacheOptions
        {
            DefaultExpiration = Duration,
            EntryFactory = new TestCacheEntryFactory(),
            LocalLockEnabled = false,
            DistributedLockEnabled = false,
        };
        _fixture.Inject<IMultilayerCacheOptions>(_options);
        _fixture.Inject<IMemoryCacheOptions>(_options);
        _cacheKeyStrategy = _fixture.Create<ICacheKeyStrategy>();
        _topicKeyStrategy = _fixture.Create<ITopicKeyStrategy>();
        _cacheKeyStrategy.GetCacheKey<string>(_cacheKey).Returns(_cacheKey);
        _topicKeyStrategy.GetTopicKey<string>().Returns(_topicKey);
        _topicFactory = _fixture.Freeze<ITopicFactory>();
        _topicProvider = _fixture.Freeze<MultilayerCachePerNameLockTests.ITopicProviderWithConnectionState>();
        _topic = _fixture.Freeze<ITopic<ICacheEvent>>();
        _topicFactory.Get(Arg.Any<string>()).Returns(_topicProvider);
        _topicProvider.Create(_topicKey).Returns(_topic);
        _topic.PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>()).Returns(_ => true);
        _memoryCacheFactory = _fixture.Freeze<global::UiPath.Caching.IMemoryCacheFactory>();
        _memoryCacheFactory.Get(Arg.Any<IMemoryCacheOptions>()).Returns(_memoryCache);
        _cacheEventFactory = _fixture.Freeze<ICacheEventFactory>();
        return ValueTask.CompletedTask;
    }
}
