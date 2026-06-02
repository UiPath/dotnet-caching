using UiPath.Platform.Caching.Locking;

namespace UiPath.Platform.Caching.Tests;

public class MultilayerCachePerNameLockTests(ITestContextAccessor testContextAccessor) : IAsyncLifetime
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubstitute();

    private ICache _innerCache = default!;
    private global::Microsoft.Extensions.Caching.Memory.IMemoryCache _memoryCache = default!;
    private ICacheKeyStrategy _cacheKeyStrategy = default!;
    private ITopicKeyStrategy _topicKeyStrategy = default!;
    private ITopicFactory _topicFactory = default!;
    private ITopicProviderWithConnectionState _topicProvider = default!;
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

    [Fact]
    public async Task GetOrAdd_with_null_policy_uses_cache_wide_lock_settings()
    {
        _options.LocalLockEnabled = true;
        _options.DistributedLockEnabled = false;
        Func<CancellationToken, Task<string?>> generator = _ => Task.FromResult<string?>("v");

        await Sut.GetOrAddAsync(_cacheKey, generator, policy: null, testContextAccessor.Current.CancellationToken);

        await _localLock.ReceivedWithAnyArgs().AcquireAsync(default!, Arg.Any<CancellationToken>());
        await _distributedLock.DidNotReceiveWithAnyArgs().AcquireAsync(default!, default, default, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetOrAdd_with_policy_LocalLockEnabled_false_skips_local_lock()
    {
        var policy = new CachePolicy { Lock = new LockProfile { LocalLockEnabled = false, DistributedLockEnabled = false } };
        Func<CancellationToken, Task<string?>> generator = _ => Task.FromResult<string?>("v");

        await Sut.GetOrAddAsync(_cacheKey, generator, policy, testContextAccessor.Current.CancellationToken);

        await _localLock.DidNotReceiveWithAnyArgs().AcquireAsync(default!, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetOrAdd_with_policy_uses_per_name_distributed_lock_timeout_and_expiry()
    {
        var expiry = TimeSpan.FromSeconds(30);
        var timeout = TimeSpan.FromMilliseconds(123);
        var policy = new CachePolicy
        {
            Lock = new LockProfile
            {
                LocalLockEnabled = false,
                DistributedLockEnabled = true,
                DistributedLockExpiry = expiry,
                DistributedLockTimeout = timeout,
            },
        };
        Func<CancellationToken, Task<string?>> generator = _ => Task.FromResult<string?>("v");

        await Sut.GetOrAddAsync(_cacheKey, generator, policy, testContextAccessor.Current.CancellationToken);

        await _distributedLock.Received(1).AcquireAsync(Arg.Any<string>(), expiry, timeout, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetOrAdd_with_policy_non_positive_distributed_lock_values_falls_back_to_cache_wide()
    {
        // Per-call LockProfile bypasses the IValidateOptions pipeline, so non-positive
        // DistributedLockExpiry / DistributedLockTimeout would otherwise surface as Redis
        // lock-API exceptions. RunUnderLocksAsync must treat <=0 as "inherit default".
        var cacheWideExpiry = TimeSpan.FromSeconds(45);
        var cacheWideTimeout = TimeSpan.FromMilliseconds(250);
        _options.DistributedLockEnabled = true;
        _options.DistributedLockExpiry = cacheWideExpiry;
        _options.DistributedLockTimeout = cacheWideTimeout;
        var policy = new CachePolicy
        {
            Lock = new LockProfile
            {
                LocalLockEnabled = false,
                DistributedLockEnabled = true,
                DistributedLockExpiry = TimeSpan.Zero,
                DistributedLockTimeout = TimeSpan.FromSeconds(-5),
            },
        };
        Func<CancellationToken, Task<string?>> generator = _ => Task.FromResult<string?>("v");

        await Sut.GetOrAddAsync(_cacheKey, generator, policy, testContextAccessor.Current.CancellationToken);

        await _distributedLock.Received(1).AcquireAsync(Arg.Any<string>(), cacheWideExpiry, cacheWideTimeout, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetOrAdd_policy_with_null_Lock_falls_back_to_cache_wide()
    {
        _options.LocalLockEnabled = false;
        _options.DistributedLockEnabled = false;
        var policy = new CachePolicy { LocalExpiration = TimeSpan.FromMinutes(1) };
        Func<CancellationToken, Task<string?>> generator = _ => Task.FromResult<string?>("v");

        await Sut.GetOrAddAsync(_cacheKey, generator, policy, testContextAccessor.Current.CancellationToken);

        await _localLock.DidNotReceiveWithAnyArgs().AcquireAsync(default!, Arg.Any<CancellationToken>());
        await _distributedLock.DidNotReceiveWithAnyArgs().AcquireAsync(default!, default, default, Arg.Any<CancellationToken>());
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
            DefaultExpiration = TimeSpan.FromMinutes(10),
            EntryFactory = new TestCacheEntryFactory(),
        };
        _fixture.Inject<IMultilayerCacheOptions>(_options);
        _fixture.Inject<IMemoryCacheOptions>(_options);
        _cacheKeyStrategy = _fixture.Create<ICacheKeyStrategy>();
        _topicKeyStrategy = _fixture.Create<ITopicKeyStrategy>();
        _cacheKeyStrategy.GetCacheKey<string>(_cacheKey).Returns(_cacheKey);
        _topicKeyStrategy.GetTopicKey<string>().Returns(_topicKey);
        _topicFactory = _fixture.Freeze<ITopicFactory>();
        _topicProvider = _fixture.Freeze<ITopicProviderWithConnectionState>();
        _topic = _fixture.Freeze<ITopic<ICacheEvent>>();
        _topicFactory.Get(Arg.Any<string>()).Returns(_topicProvider);
        _topicProvider.Create(_topicKey).Returns(_topic);
        _memoryCacheFactory = _fixture.Freeze<global::UiPath.Platform.Caching.IMemoryCacheFactory>();
        _memoryCacheFactory.Get(Arg.Any<IMemoryCacheOptions>()).Returns(_memoryCache);
        _cacheEventFactory = _fixture.Freeze<ICacheEventFactory>();
        return ValueTask.CompletedTask;
    }

    public interface ITopicProviderWithConnectionState : ITopicProvider, IConnectionState
    {
    }
}
