using UiPath.Platform.Caching.Config;
using UiPath.Platform.Caching.Locking;

namespace UiPath.Platform.Caching.Tests;

public class MultilayerHashCachePerNameJitterTests(ITestContextAccessor testContextAccessor) : IAsyncLifetime
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubstitute();

    private IHashCache _innerCache = default!;
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
    private MultilayerHashCache? _sut;

    private MultilayerHashCache Sut => _sut ??= _fixture.Create<MultilayerHashCache>();

    [Fact]
    public async Task SetAsync_jitters_policy_derived_expiration_within_max()
    {
        var token = testContextAccessor.Current.CancellationToken;
        var baseTtl = TimeSpan.FromMinutes(5);
        var maxJitter = TimeSpan.FromSeconds(30);
        var defaultPolicy = new CachePolicy { DistributedExpiration = baseTtl, JitterMaxDuration = maxJitter };
        var policyFactory = new DefaultCachePolicyFactory(Array.Empty<KeyValuePair<string, CachePolicy>>(), defaultPolicy);
        _fixture.Inject<ICachePolicyFactory>(policyFactory);
        _sut = null;

        _innerCache.SetAsync<string?>(_cacheKey, Arg.Any<IDictionary<string, string?>>(), Arg.Any<HashCacheEntryOptions>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _topic.PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>())
            .Returns(_ => true);

        var values = new Dictionary<string, string?> { ["f"] = "v" };
        await Sut.SetAsync(_cacheKey, values, (CachePolicy?)null, token);

        await _innerCache.Received(1).SetAsync<string?>(
            _cacheKey,
            Arg.Any<IDictionary<string, string?>>(),
            Arg.Is<HashCacheEntryOptions>(o => o.ExpireTime.HasValue
                && o.ExpireTime.Value - DateTimeOffset.UtcNow >= baseTtl - TimeSpan.FromSeconds(5)
                && o.ExpireTime.Value - DateTimeOffset.UtcNow <= baseTtl + maxJitter + TimeSpan.FromSeconds(5)),
            Arg.Any<CachePolicy?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetAsync_honors_caller_explicit_expiration_without_jitter()
    {
        var token = testContextAccessor.Current.CancellationToken;
        var maxJitter = TimeSpan.FromMinutes(1);
        var defaultPolicy = new CachePolicy { JitterMaxDuration = maxJitter };
        var policyFactory = new DefaultCachePolicyFactory(Array.Empty<KeyValuePair<string, CachePolicy>>(), defaultPolicy);
        _fixture.Inject<ICachePolicyFactory>(policyFactory);
        _sut = null;

        var callerTtl = TimeSpan.FromMinutes(2);

        _innerCache.SetAsync<string?>(_cacheKey, Arg.Any<IDictionary<string, string?>>(), Arg.Any<HashCacheEntryOptions>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _topic.PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>())
            .Returns(_ => true);

        var values = new Dictionary<string, string?> { ["f"] = "v" };
        await Sut.SetAsync(_cacheKey, values, callerTtl, policy: null, token);

        await _innerCache.Received(1).SetAsync<string?>(
            _cacheKey,
            Arg.Any<IDictionary<string, string?>>(),
            Arg.Is<HashCacheEntryOptions>(o => o.ExpireTime.HasValue
                && o.ExpireTime.Value - DateTimeOffset.UtcNow > callerTtl - TimeSpan.FromSeconds(5)
                && o.ExpireTime.Value - DateTimeOffset.UtcNow < callerTtl + TimeSpan.FromSeconds(5)),
            Arg.Any<CachePolicy?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetAsync_jitters_options_DefaultExpiration_when_policy_DistributedExpiration_is_null()
    {
        var token = testContextAccessor.Current.CancellationToken;
        var optionsTtl = _options.DefaultExpiration!.Value;
        var maxJitter = TimeSpan.FromSeconds(30);
        var defaultPolicy = new CachePolicy { JitterMaxDuration = maxJitter }; // no DistributedExpiration
        var policyFactory = new DefaultCachePolicyFactory(Array.Empty<KeyValuePair<string, CachePolicy>>(), defaultPolicy);
        _fixture.Inject<ICachePolicyFactory>(policyFactory);
        _sut = null;

        _innerCache.SetAsync<string?>(_cacheKey, Arg.Any<IDictionary<string, string?>>(), Arg.Any<HashCacheEntryOptions>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _topic.PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>())
            .Returns(_ => true);

        var values = new Dictionary<string, string?> { ["f"] = "v" };
        await Sut.SetAsync(_cacheKey, values, (TimeSpan?)null, policy: null, token);

        await _innerCache.Received(1).SetAsync<string?>(
            _cacheKey,
            Arg.Any<IDictionary<string, string?>>(),
            Arg.Is<HashCacheEntryOptions>(o => o.ExpireTime.HasValue
                && o.ExpireTime.Value - DateTimeOffset.UtcNow >= optionsTtl - TimeSpan.FromSeconds(5)
                && o.ExpireTime.Value - DateTimeOffset.UtcNow <= optionsTtl + maxJitter + TimeSpan.FromSeconds(5)),
            Arg.Any<CachePolicy?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RefreshAsync_TimeSpan_overload_jitters_policy_derived_expiration()
    {
        var token = testContextAccessor.Current.CancellationToken;
        var baseTtl = TimeSpan.FromMinutes(5);
        var maxJitter = TimeSpan.FromSeconds(30);
        var defaultPolicy = new CachePolicy { DistributedExpiration = baseTtl, JitterMaxDuration = maxJitter };
        var policyFactory = new DefaultCachePolicyFactory(Array.Empty<KeyValuePair<string, CachePolicy>>(), defaultPolicy);
        _fixture.Inject<ICachePolicyFactory>(policyFactory);
        _sut = null;

        _innerCache.RefreshAsync<string>(_cacheKey, Arg.Any<HashCacheEntryOptions>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _topic.PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>())
            .Returns(_ => true);

        await Sut.RefreshAsync<string>(_cacheKey, (TimeSpan?)null, policy: null, token);

        await _innerCache.Received(1).RefreshAsync<string>(
            _cacheKey,
            Arg.Is<HashCacheEntryOptions>(o => o.ExpireTime.HasValue
                && o.ExpireTime.Value - DateTimeOffset.UtcNow >= baseTtl - TimeSpan.FromSeconds(5)
                && o.ExpireTime.Value - DateTimeOffset.UtcNow <= baseTtl + maxJitter + TimeSpan.FromSeconds(5)),
            Arg.Any<CachePolicy?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RefreshAsync_HashCacheEntryOptions_honors_ExpireTime_without_jitter()
    {
        var token = testContextAccessor.Current.CancellationToken;
        var maxJitter = TimeSpan.FromMinutes(1);
        var defaultPolicy = new CachePolicy { JitterMaxDuration = maxJitter };
        var policyFactory = new DefaultCachePolicyFactory(Array.Empty<KeyValuePair<string, CachePolicy>>(), defaultPolicy);
        _fixture.Inject<ICachePolicyFactory>(policyFactory);
        _sut = null;

        var callerInstant = DateTimeOffset.UtcNow.AddMinutes(2);
        var options = new HashCacheEntryOptions(ExpireTime: callerInstant);

        _innerCache.RefreshAsync<string>(_cacheKey, Arg.Any<HashCacheEntryOptions>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _topic.PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>())
            .Returns(_ => true);

        await Sut.RefreshAsync<string>(_cacheKey, options, policy: null, token);

        await _innerCache.Received(1).RefreshAsync<string>(
            _cacheKey,
            Arg.Is<HashCacheEntryOptions>(o => o.ExpireTime.HasValue
                && o.ExpireTime.Value - callerInstant > TimeSpan.FromSeconds(-1)
                && o.ExpireTime.Value - callerInstant < TimeSpan.FromSeconds(1)),
            Arg.Any<CachePolicy?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RefreshAsync_HashCacheEntryOptions_honors_TimeToLive_without_jitter()
    {
        var token = testContextAccessor.Current.CancellationToken;
        var callerTtl = TimeSpan.FromMinutes(2);
        var metadata = new Dictionary<string, string?> { ["m"] = "1" };
        var defaultPolicy = new CachePolicy
        {
            DistributedExpiration = TimeSpan.FromDays(30),
            JitterMaxDuration = TimeSpan.FromDays(30),
        };
        var policyFactory = new DefaultCachePolicyFactory(Array.Empty<KeyValuePair<string, CachePolicy>>(), defaultPolicy);
        _fixture.Inject<ICachePolicyFactory>(policyFactory);
        _sut = null;

        var options = new HashCacheEntryOptions(
            TimeToLive: callerTtl,
            Metadata: metadata,
            SetOption: HashCacheSetOption.HashReplace);

        _innerCache.RefreshAsync<string>(_cacheKey, Arg.Any<HashCacheEntryOptions>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _topic.PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>())
            .Returns(_ => true);

        await Sut.RefreshAsync<string>(_cacheKey, options, policy: null, token);

        await _innerCache.Received(1).RefreshAsync<string>(
            _cacheKey,
            Arg.Is<HashCacheEntryOptions>(o => o.ExpireTime.HasValue
                && !o.TimeToLive.HasValue
                && o.ExpireTime.Value - DateTimeOffset.UtcNow > callerTtl - TimeSpan.FromSeconds(5)
                && o.ExpireTime.Value - DateTimeOffset.UtcNow < callerTtl + TimeSpan.FromSeconds(5)
                && ReferenceEquals(o.Metadata, metadata)
                && o.SetOption == HashCacheSetOption.HashReplace),
            Arg.Any<CachePolicy?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RefreshAsync_HashCacheEntryOptions_jitters_when_neither_ExpireTime_nor_TimeToLive_set()
    {
        var token = testContextAccessor.Current.CancellationToken;
        var baseTtl = TimeSpan.FromMinutes(5);
        var maxJitter = TimeSpan.FromSeconds(30);
        var defaultPolicy = new CachePolicy { DistributedExpiration = baseTtl, JitterMaxDuration = maxJitter };
        var policyFactory = new DefaultCachePolicyFactory(Array.Empty<KeyValuePair<string, CachePolicy>>(), defaultPolicy);
        _fixture.Inject<ICachePolicyFactory>(policyFactory);
        _sut = null;

        var metadata = new Dictionary<string, string?> { ["m"] = "1" };
        var options = new HashCacheEntryOptions(Metadata: metadata, SetOption: HashCacheSetOption.HashReplace);

        _innerCache.RefreshAsync<string>(_cacheKey, Arg.Any<HashCacheEntryOptions>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _topic.PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>())
            .Returns(_ => true);

        await Sut.RefreshAsync<string>(_cacheKey, options, policy: null, token);

        await _innerCache.Received(1).RefreshAsync<string>(
            _cacheKey,
            Arg.Is<HashCacheEntryOptions>(o => o.ExpireTime.HasValue
                && o.ExpireTime.Value - DateTimeOffset.UtcNow >= baseTtl - TimeSpan.FromSeconds(5)
                && o.ExpireTime.Value - DateTimeOffset.UtcNow <= baseTtl + maxJitter + TimeSpan.FromSeconds(5)
                && ReferenceEquals(o.Metadata, metadata)
                && o.SetOption == HashCacheSetOption.HashReplace),
            Arg.Any<CachePolicy?>(),
            Arg.Any<CancellationToken>());
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
            DefaultExpiration = TimeSpan.FromMinutes(10),
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
        _memoryCacheFactory = _fixture.Freeze<global::UiPath.Platform.Caching.IMemoryCacheFactory>();
        _memoryCacheFactory.Get(Arg.Any<IMemoryCacheOptions>()).Returns(_memoryCache);
        _cacheEventFactory = _fixture.Freeze<ICacheEventFactory>();
        return ValueTask.CompletedTask;
    }
}
