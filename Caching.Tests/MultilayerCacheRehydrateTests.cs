using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Logging;
using UiPath.Platform.Caching.Config;
using UiPath.Platform.Caching.Locking;

namespace UiPath.Platform.Caching.Tests;

public class MultilayerCacheRehydrateTests(ITestContextAccessor testContextAccessor) : IAsyncLifetime
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubstitute();

    private ICache _innerCache = default!;
    private global::Microsoft.Extensions.Caching.Memory.IMemoryCache _memoryCache = default!;
    private ICacheKeyStrategy _cacheKeyStrategy = default!;
    private ITopicKeyStrategy _topicKeyStrategy = default!;
    private ITopicFactory _topicFactory = default!;
    private MultilayerCachePerNameLockTests.ITopicProviderWithConnectionState _topicProvider = default!;
    private ITopic<ICacheEvent> _topic = default!;
    private global::UiPath.Platform.Caching.IMemoryCacheFactory _memoryCacheFactory = default!;
    private ICacheEventFactory _cacheEventFactory = default!;
    private ILocalLock _localLock = default!;
    private IDistributedLock _distributedLock = default!;
    private InMemoryRedisCacheOptions _options = default!;
    private CacheKey _cacheKey = default!;
    private TopicKey _topicKey = default!;
    private MultilayerCache? _sut;

    private MultilayerCache Sut => _sut ??= _fixture.Create<MultilayerCache>();

    private static readonly TimeSpan Duration = TimeSpan.FromMinutes(10);

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

    [Fact]
    public async Task Hit_before_threshold_does_not_trigger_rehydrate()
    {
        var token = testContextAccessor.Current.CancellationToken;
        var fresh = new TestCacheEntry<string?> { Value = "cached", Expiration = DateTimeOffset.UtcNow.Add(Duration) };
        _innerCache.GetCacheEntryAsync<string>(_cacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()).Returns(fresh);

        var generatorCalls = 0;
        Func<CancellationToken, Task<string?>> generator = _ =>
        {
            Interlocked.Increment(ref generatorCalls);
            return Task.FromResult<string?>("rehydrated");
        };

        var result = await Sut.GetOrAddAsync(_cacheKey, generator, RehydratePolicy(), token);

        result.Should().Be("cached");
        await Task.Delay(50, TestContext.Current.CancellationToken);
        generatorCalls.Should().Be(0);
        await _distributedLock.DidNotReceiveWithAnyArgs().TryAcquireAsync(default!, default, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Hit_past_threshold_acquires_distributed_lock_and_invokes_generator()
    {
        var token = testContextAccessor.Current.CancellationToken;
        var ttl = TimeSpan.FromMinutes(2);
        var aged = new TestCacheEntry<string?> { Value = "cached", Expiration = DateTimeOffset.UtcNow.Add(ttl) };
        _innerCache.GetCacheEntryAsync<string>(_cacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()).Returns(aged);

        var generatorTcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<CancellationToken, Task<string?>> generator = _ => generatorTcs.Task;

        var acquiredLock = Substitute.For<IAsyncDisposable>();
        _distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(acquiredLock);

        var result = await Sut.GetOrAddAsync(_cacheKey, generator, RehydratePolicy(), token);

        result.Should().Be("cached");

        await WaitForAsync(() => _distributedLock.ReceivedCalls().Any(), TimeSpan.FromSeconds(5), token);
        await _distributedLock.ReceivedWithAnyArgs(1).TryAcquireAsync(default!, default, Arg.Any<CancellationToken>());
        generatorTcs.TrySetResult("rehydrated");
    }

    [Fact]
    public async Task Rehydrate_writes_value_back_through_inner_cache_on_success()
    {
        var token = testContextAccessor.Current.CancellationToken;
        var ttl = TimeSpan.FromMinutes(2);
        var aged = new TestCacheEntry<string?> { Value = "cached", Expiration = DateTimeOffset.UtcNow.Add(ttl) };
        _innerCache.GetCacheEntryAsync<string>(_cacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()).Returns(aged);

        Func<CancellationToken, Task<string?>> generator = _ => Task.FromResult<string?>("rehydrated");

        var acquiredLock = Substitute.For<IAsyncDisposable>();
        _distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(acquiredLock);
        _innerCache.SetAsync<string?>(_cacheKey, Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        await Sut.GetOrAddAsync(_cacheKey, generator, RehydratePolicy(), token);

        await WaitForAsync(() => _innerCache.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(ICache.SetAsync)), TimeSpan.FromSeconds(5), token);
        await _innerCache.Received(1).SetAsync<string?>(_cacheKey, "rehydrated", Arg.Any<DateTimeOffset?>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
        await acquiredLock.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task Rehydrate_skipped_when_distributed_lock_unavailable()
    {
        var token = testContextAccessor.Current.CancellationToken;
        var ttl = TimeSpan.FromMinutes(2);
        var aged = new TestCacheEntry<string?> { Value = "cached", Expiration = DateTimeOffset.UtcNow.Add(ttl) };
        _innerCache.GetCacheEntryAsync<string>(_cacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()).Returns(aged);

        var generatorCalls = 0;
        Func<CancellationToken, Task<string?>> generator = _ =>
        {
            Interlocked.Increment(ref generatorCalls);
            return Task.FromResult<string?>("rehydrated");
        };

        _distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(default(IAsyncDisposable));

        await Sut.GetOrAddAsync(_cacheKey, generator, RehydratePolicy(), token);

        await WaitForAsync(() => _distributedLock.ReceivedCalls().Any(), TimeSpan.FromSeconds(5), token);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        generatorCalls.Should().Be(0);
        await _innerCache.DidNotReceive().SetAsync<string?>(_cacheKey, Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rehydrate_does_not_release_lock_on_generator_failure()
    {
        var token = testContextAccessor.Current.CancellationToken;
        var ttl = TimeSpan.FromMinutes(2);
        var aged = new TestCacheEntry<string?> { Value = "cached", Expiration = DateTimeOffset.UtcNow.Add(ttl) };
        _innerCache.GetCacheEntryAsync<string>(_cacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()).Returns(aged);

        Func<CancellationToken, Task<string?>> generator = _ => throw new InvalidOperationException("boom");

        var acquiredLock = Substitute.For<IAsyncDisposable>();
        _distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(acquiredLock);

        await Sut.GetOrAddAsync(_cacheKey, generator, RehydratePolicy(), token);

        await WaitForAsync(() => _distributedLock.ReceivedCalls().Any(), TimeSpan.FromSeconds(5), token);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        await acquiredLock.DidNotReceive().DisposeAsync();
        await _innerCache.DidNotReceive().SetAsync<string?>(_cacheKey, Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Caller_explicit_TimeSpan_drives_rehydrate_soft_TTL_not_policy()
    {
        // Entry has 2min remaining; caller passes 2min on this call. Elapsed fraction must be
        // computed against caller's TimeSpan (= 0, below threshold) rather than the policy's
        // 10min duration (which would yield 0.8, above the 0.75 threshold).
        var token = testContextAccessor.Current.CancellationToken;
        var ttl = TimeSpan.FromMinutes(2);
        var aged = new TestCacheEntry<string?> { Value = "cached", Expiration = DateTimeOffset.UtcNow.Add(ttl) };
        _innerCache.GetCacheEntryAsync<string>(_cacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()).Returns(aged);

        var generatorCalls = 0;
        Func<CancellationToken, Task<string?>> generator = _ =>
        {
            Interlocked.Increment(ref generatorCalls);
            return Task.FromResult<string?>("rehydrated");
        };

        await Sut.GetOrAddAsync(_cacheKey, generator, ttl, RehydratePolicy(), token);

        await Task.Delay(100, TestContext.Current.CancellationToken);
        generatorCalls.Should().Be(0, "soft-TTL math must use caller's explicit TimeSpan, not the policy's DistributedExpiration");
        await _distributedLock.DidNotReceiveWithAnyArgs().TryAcquireAsync(default!, default, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rehydrate_holds_lock_when_inner_write_returns_false()
    {
        var token = testContextAccessor.Current.CancellationToken;
        var ttl = TimeSpan.FromMinutes(2);
        var aged = new TestCacheEntry<string?> { Value = "cached", Expiration = DateTimeOffset.UtcNow.Add(ttl) };
        _innerCache.GetCacheEntryAsync<string>(_cacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()).Returns(aged);

        Func<CancellationToken, Task<string?>> generator = _ => Task.FromResult<string?>("rehydrated");

        var acquiredLock = Substitute.For<IAsyncDisposable>();
        _distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(acquiredLock);
        // Inner cache returns false from SetAsync — rehydrate must NOT report success.
        _innerCache.SetAsync<string?>(_cacheKey, Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(false);

        await Sut.GetOrAddAsync(_cacheKey, generator, RehydratePolicy(), token);

        await WaitForAsync(() => _innerCache.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(ICache.SetAsync)), TimeSpan.FromSeconds(5), token);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        // Lock stays held on failure so cooldown is enforced cluster-wide via the lock TTL.
        await acquiredLock.DidNotReceive().DisposeAsync();
    }

    [Fact]
    public async Task Rehydrate_disabled_when_policy_RehydrateEnabled_is_null_or_false()
    {
        var token = testContextAccessor.Current.CancellationToken;
        var ttl = TimeSpan.FromMinutes(2);
        var aged = new TestCacheEntry<string?> { Value = "cached", Expiration = DateTimeOffset.UtcNow.Add(ttl) };
        _innerCache.GetCacheEntryAsync<string>(_cacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()).Returns(aged);

        var generatorCalls = 0;
        Func<CancellationToken, Task<string?>> generator = _ =>
        {
            Interlocked.Increment(ref generatorCalls);
            return Task.FromResult<string?>("rehydrated");
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
    public async Task Rehydrate_publishes_broadcast_so_other_nodes_invalidate_their_L1()
    {
        // Without the broadcast, peer nodes keep serving the pre-rehydrate L1 value until natural
        // expiry. The rehydrate write must publish a CacheSet event the same way a normal SetAsync does.
        var token = testContextAccessor.Current.CancellationToken;
        var ttl = TimeSpan.FromMinutes(2);
        var aged = new TestCacheEntry<string?> { Value = "cached", Expiration = DateTimeOffset.UtcNow.Add(ttl) };
        _innerCache.GetCacheEntryAsync<string>(_cacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()).Returns(aged);

        Func<CancellationToken, Task<string?>> generator = _ => Task.FromResult<string?>("rehydrated");

        var acquiredLock = Substitute.For<IAsyncDisposable>();
        _distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(acquiredLock);
        _innerCache.SetAsync<string?>(_cacheKey, Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        await Sut.GetOrAddAsync(_cacheKey, generator, RehydratePolicy(), token);

        await WaitForAsync(() => _topic.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(ITopic<ICacheEvent>.PublishAsync)), TimeSpan.FromSeconds(5), token);
        await _topic.ReceivedWithAnyArgs(1).PublishAsync(default!, token);
    }

    [Fact]
    public async Task Rehydrate_skipped_when_current_entry_is_cached_null_and_CacheNullValues_true()
    {
        // Circuit-breaker A: the existing entry is a cached-null marker (Value is null but Found=true);
        // re-running the factory would just churn the same null, so the rehydrate must short-circuit
        // before taking the distributed lock.
        _options.CacheNullValues = true;
        var token = testContextAccessor.Current.CancellationToken;
        var ttl = TimeSpan.FromMinutes(2);
        var aged = new TestCacheEntry<string?> { Value = null, Found = true, Expiration = DateTimeOffset.UtcNow.Add(ttl) };
        _innerCache.GetCacheEntryAsync<string>(_cacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()).Returns(aged);

        var generatorCalls = 0;
        Func<CancellationToken, Task<string?>> generator = _ =>
        {
            Interlocked.Increment(ref generatorCalls);
            return Task.FromResult<string?>("rehydrated");
        };

        await Sut.GetOrAddAsync(_cacheKey, generator, RehydratePolicy(), token);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        generatorCalls.Should().Be(0);
        await _distributedLock.DidNotReceiveWithAnyArgs().TryAcquireAsync(default!, default, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rehydrate_with_factory_returning_null_preserves_original_entry_expiration()
    {
        // Circuit-breaker B: when the factory transitions to null (upstream delete) and we cache nulls,
        // the rehydrate must write the null with the ORIGINAL entry deadline rather than extending
        // by a fresh TTL window — otherwise a stale null would survive forever.
        _options.CacheNullValues = true;
        var token = testContextAccessor.Current.CancellationToken;
        var ttl = TimeSpan.FromMinutes(2);
        var originalDeadline = DateTimeOffset.UtcNow.Add(ttl);
        var aged = new TestCacheEntry<string?> { Value = "cached", Expiration = originalDeadline };
        _innerCache.GetCacheEntryAsync<string>(_cacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()).Returns(aged);

        Func<CancellationToken, Task<string?>> generator = _ => Task.FromResult<string?>(null);

        var acquiredLock = Substitute.For<IAsyncDisposable>();
        _distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(acquiredLock);
        _innerCache.SetAsync<string?>(_cacheKey, Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        await Sut.GetOrAddAsync(_cacheKey, generator, RehydratePolicy(), token);

        await WaitForAsync(() => _innerCache.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(ICache.SetAsync)), TimeSpan.FromSeconds(5), token);
        await _innerCache.Received(1).SetAsync<string?>(
            _cacheKey,
            null,
            Arg.Is<DateTimeOffset?>(d => d.HasValue && Math.Abs((d.Value - originalDeadline).TotalSeconds) < 1),
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
        _innerCache = _fixture.Freeze<ICache>();
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
        _memoryCacheFactory = _fixture.Freeze<global::UiPath.Platform.Caching.IMemoryCacheFactory>();
        _memoryCacheFactory.Get(Arg.Any<IMemoryCacheOptions>()).Returns(_memoryCache);
        _cacheEventFactory = _fixture.Freeze<ICacheEventFactory>();
        return ValueTask.CompletedTask;
    }
}
