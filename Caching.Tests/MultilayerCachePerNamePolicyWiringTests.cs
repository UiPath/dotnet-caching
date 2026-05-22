using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Logging;
using UiPath.Platform.Caching.Config;
using UiPath.Platform.Caching.Locking;

namespace UiPath.Platform.Caching.Tests;

public class MultilayerCachePerNamePolicyWiringTests : IAsyncLifetime
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

    [Fact]
    public async Task GetOrAdd_uses_policy_DistributedExpiration_when_caller_omits_expiration()
    {
        var token = TestContext.Current.CancellationToken;
        var policyTtl = TimeSpan.FromMinutes(7);
        var policy = new CachePolicy { DistributedExpiration = policyTtl };
        Func<CancellationToken, Task<string?>> generator = _ => Task.FromResult<string?>("v");

        _innerCache.GetCacheEntryAsync<string>(_cacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(new TestCacheEntry<string?> { Value = null });

        await Sut.GetOrAddAsync(_cacheKey, generator, policy, token);

        await _innerCache.Received(1).SetAsync<string?>(
            _cacheKey,
            "v",
            Arg.Is<DateTimeOffset?>(d => d.HasValue && d.Value - DateTimeOffset.UtcNow > policyTtl - TimeSpan.FromSeconds(5) && d.Value - DateTimeOffset.UtcNow < policyTtl + TimeSpan.FromSeconds(5)),
            Arg.Any<CachePolicy?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetOrAdd_caller_expiration_beats_policy_DistributedExpiration()
    {
        var token = TestContext.Current.CancellationToken;
        var callerTtl = TimeSpan.FromMinutes(2);
        var policy = new CachePolicy { DistributedExpiration = TimeSpan.FromMinutes(7) };
        Func<CancellationToken, Task<string?>> generator = _ => Task.FromResult<string?>("v");

        _innerCache.GetCacheEntryAsync<string>(_cacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(new TestCacheEntry<string?> { Value = null });

        await Sut.GetOrAddAsync(_cacheKey, generator, callerTtl, policy, token);

        await _innerCache.Received(1).SetAsync<string?>(
            _cacheKey,
            "v",
            Arg.Is<DateTimeOffset?>(d => d.HasValue && d.Value - DateTimeOffset.UtcNow > callerTtl - TimeSpan.FromSeconds(5) && d.Value - DateTimeOffset.UtcNow < callerTtl + TimeSpan.FromSeconds(5)),
            Arg.Any<CachePolicy?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetOrAdd_picks_up_DefaultCachePolicy_when_caller_passes_null_policy()
    {
        var token = TestContext.Current.CancellationToken;
        var defaultTtl = TimeSpan.FromMinutes(9);
        var defaultPolicy = new CachePolicy { DistributedExpiration = defaultTtl };
        var policyFactory = new DefaultCachePolicyFactory(
            Array.Empty<KeyValuePair<string, CachePolicy>>(),
            defaultPolicy);
        _fixture.Inject<ICachePolicyFactory>(policyFactory);
        _sut = null;

        Func<CancellationToken, Task<string?>> generator = _ => Task.FromResult<string?>("v");

        _innerCache.GetCacheEntryAsync<string>(_cacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(new TestCacheEntry<string?> { Value = null });

        await Sut.GetOrAddAsync(_cacheKey, generator, policy: null, token: token);

        await _innerCache.Received(1).SetAsync<string?>(
            _cacheKey,
            "v",
            Arg.Is<DateTimeOffset?>(d => d.HasValue && d.Value - DateTimeOffset.UtcNow > defaultTtl - TimeSpan.FromSeconds(5) && d.Value - DateTimeOffset.UtcNow < defaultTtl + TimeSpan.FromSeconds(5)),
            Arg.Any<CachePolicy?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetOrAdd_falls_back_to_cache_options_DefaultExpiration_when_policy_omits_TTL()
    {
        var token = TestContext.Current.CancellationToken;
        var defaultPolicy = new CachePolicy { Lock = new LockProfile { LocalLockEnabled = false } };
        var policyFactory = new DefaultCachePolicyFactory(
            Array.Empty<KeyValuePair<string, CachePolicy>>(),
            defaultPolicy);
        _fixture.Inject<ICachePolicyFactory>(policyFactory);
        _sut = null;

        Func<CancellationToken, Task<string?>> generator = _ => Task.FromResult<string?>("v");

        _innerCache.GetCacheEntryAsync<string>(_cacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(new TestCacheEntry<string?> { Value = null });

        await Sut.GetOrAddAsync(_cacheKey, generator, policy: null, token: token);

        var optionsTtl = _options.DefaultExpiration!.Value;
        await _innerCache.Received(1).SetAsync<string?>(
            _cacheKey,
            "v",
            Arg.Is<DateTimeOffset?>(d => d.HasValue && d.Value - DateTimeOffset.UtcNow > optionsTtl - TimeSpan.FromSeconds(5) && d.Value - DateTimeOffset.UtcNow < optionsTtl + TimeSpan.FromSeconds(5)),
            Arg.Any<CachePolicy?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetAsync_uses_DefaultCachePolicy_DistributedExpiration_when_caller_omits_expiration()
    {
        var defaultTtl = TimeSpan.FromMinutes(9);
        var defaultPolicy = new CachePolicy { DistributedExpiration = defaultTtl };
        var policyFactory = new DefaultCachePolicyFactory(
            Array.Empty<KeyValuePair<string, CachePolicy>>(),
            defaultPolicy);
        _fixture.Inject<ICachePolicyFactory>(policyFactory);
        _sut = null;

        _innerCache.SetAsync<string?>(_cacheKey, Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _topic.PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>())
            .Returns(_ => true);

        await Sut.SetAsync(_cacheKey, "v", (CachePolicy?)null, TestContext.Current.CancellationToken);

        await _innerCache.Received(1).SetAsync<string?>(
            _cacheKey,
            "v",
            Arg.Is<DateTimeOffset?>(d => d.HasValue && d.Value - DateTimeOffset.UtcNow > defaultTtl - TimeSpan.FromSeconds(5) && d.Value - DateTimeOffset.UtcNow < defaultTtl + TimeSpan.FromSeconds(5)),
            Arg.Any<CachePolicy?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetAsync_falls_back_to_cache_options_DefaultExpiration_when_policy_omits_TTL()
    {
        // Default policy with no DistributedExpiration → SetAsync uses _multiLayerCacheOptions.DefaultExpiration.
        _innerCache.SetAsync<string?>(_cacheKey, Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _topic.PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>())
            .Returns(_ => true);

        await Sut.SetAsync(_cacheKey, "v", (CachePolicy?)null, TestContext.Current.CancellationToken);

        var optionsTtl = _options.DefaultExpiration!.Value;
        await _innerCache.Received(1).SetAsync<string?>(
            _cacheKey,
            "v",
            Arg.Is<DateTimeOffset?>(d => d.HasValue && d.Value - DateTimeOffset.UtcNow > optionsTtl - TimeSpan.FromSeconds(5) && d.Value - DateTimeOffset.UtcNow < optionsTtl + TimeSpan.FromSeconds(5)),
            Arg.Any<CachePolicy?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetCacheEntry_L2_hit_populates_L1_with_DefaultCachePolicy_LocalExpiration()
    {
        // DefaultCachePolicy contributes a 1-min L1 cap; provider LocalMaxExpiration is 10 min.
        // The read-from-L2 populate must apply the policy's smaller cap, not the provider's, so
        // that a user-set DefaultCachePolicy.LocalExpiration governs read-path L1 hydration
        // the same way it governs write-path L1.
        var policyCap = TimeSpan.FromMinutes(1);
        _options.LocalMaxExpiration = TimeSpan.FromMinutes(10);
        var defaultPolicy = new CachePolicy { LocalExpiration = policyCap };
        var policyFactory = new DefaultCachePolicyFactory(
            Array.Empty<KeyValuePair<string, CachePolicy>>(),
            defaultPolicy);
        _fixture.Inject<ICachePolicyFactory>(policyFactory);
        _sut = null;

        var cacheEntry = _fixture.Freeze<Microsoft.Extensions.Caching.Memory.ICacheEntry>();
        _memoryCache.CreateEntry(Arg.Any<object>()).Returns(cacheEntry);
        _innerCache.GetCacheEntryAsync<string>(_cacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(new TestCacheEntry<string?> { Value = "v", Found = true, Expiration = DateTimeOffset.UtcNow.AddHours(1) });

        _ = await Sut.GetCacheEntryAsync<string>(_cacheKey, policy: null, token: TestContext.Current.CancellationToken);

        var nowPlusPolicyCap = DateTimeOffset.UtcNow.Add(policyCap);
        cacheEntry.AbsoluteExpiration.Should().BeCloseTo(nowPlusPolicyCap, TimeSpan.FromSeconds(5),
            "the L1 entry's absolute expiration must come from policy.LocalExpiration, not from LocalMaxExpiration or the L2 entry's 1-hour TTL");
    }

    [Fact]
    public async Task GetCacheEntry_L2_hit_populates_L1_with_caller_supplied_policy_LocalExpiration()
    {
        // Caller passes an explicit policy with LocalExpiration = 2 min. Provider LocalMaxExpiration is 10 min.
        // The L1 populate must apply the CALLER-supplied policy's cap, not the cache-wide one.
        var callerPolicyCap = TimeSpan.FromMinutes(2);
        var callerPolicy = new CachePolicy { LocalExpiration = callerPolicyCap };
        _options.LocalMaxExpiration = TimeSpan.FromMinutes(10);

        var cacheEntry = _fixture.Freeze<Microsoft.Extensions.Caching.Memory.ICacheEntry>();
        _memoryCache.CreateEntry(Arg.Any<object>()).Returns(cacheEntry);
        _innerCache.GetCacheEntryAsync<string>(_cacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(new TestCacheEntry<string?> { Value = "v", Found = true, Expiration = DateTimeOffset.UtcNow.AddHours(1) });

        _ = await Sut.GetCacheEntryAsync<string>(_cacheKey, callerPolicy, TestContext.Current.CancellationToken);

        var nowPlusCallerCap = DateTimeOffset.UtcNow.Add(callerPolicyCap);
        cacheEntry.AbsoluteExpiration.Should().BeCloseTo(nowPlusCallerCap, TimeSpan.FromSeconds(5),
            "the L1 entry's absolute expiration must come from the CALLER-supplied policy.LocalExpiration, not from LocalMaxExpiration or the L2 entry's 1-hour TTL");
    }

    [Fact]
    public async Task RefreshAsync_uses_DefaultCachePolicy_DistributedExpiration_when_caller_omits_expiration()
    {
        // DefaultCachePolicy says 7 min; provider options.DefaultExpiration is 10 min.
        // RefreshAsync(key, policy: null, token: token) without explicit expiration must extend to 7 min, not 10.
        var policyTtl = TimeSpan.FromMinutes(7);
        var defaultPolicy = new CachePolicy { DistributedExpiration = policyTtl };
        var policyFactory = new DefaultCachePolicyFactory(
            Array.Empty<KeyValuePair<string, CachePolicy>>(),
            defaultPolicy);
        _fixture.Inject<ICachePolicyFactory>(policyFactory);
        _sut = null;

        _innerCache.RefreshAsync<string>(_cacheKey, Arg.Any<DateTimeOffset?>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _topic.PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>())
            .Returns(_ => true);

        await Sut.RefreshAsync<string>(_cacheKey, policy: null, token: TestContext.Current.CancellationToken);

        await _innerCache.Received(1).RefreshAsync<string>(
            _cacheKey,
            Arg.Is<DateTimeOffset?>(d => d.HasValue
                && d.Value - DateTimeOffset.UtcNow > policyTtl - TimeSpan.FromSeconds(5)
                && d.Value - DateTimeOffset.UtcNow < policyTtl + TimeSpan.FromSeconds(5)), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetAsync_jitters_policy_derived_expiration_within_max()
    {
        var baseTtl = TimeSpan.FromMinutes(5);
        var maxJitter = TimeSpan.FromSeconds(30);
        var defaultPolicy = new CachePolicy { DistributedExpiration = baseTtl, JitterMaxDuration = maxJitter };
        var policyFactory = new DefaultCachePolicyFactory(Array.Empty<KeyValuePair<string, CachePolicy>>(), defaultPolicy);
        _fixture.Inject<ICachePolicyFactory>(policyFactory);
        _sut = null;

        _innerCache.SetAsync<string?>(_cacheKey, Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _topic.PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>())
            .Returns(_ => true);

        await Sut.SetAsync(_cacheKey, "v", (CachePolicy?)null, TestContext.Current.CancellationToken);

        await _innerCache.Received(1).SetAsync<string?>(
            _cacheKey,
            "v",
            Arg.Is<DateTimeOffset?>(d => d.HasValue
                && d.Value - DateTimeOffset.UtcNow >= baseTtl - TimeSpan.FromSeconds(5)
                && d.Value - DateTimeOffset.UtcNow <= baseTtl + maxJitter + TimeSpan.FromSeconds(5)),
            Arg.Any<CachePolicy?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetAsync_honors_caller_explicit_expiration_without_jitter()
    {
        var maxJitter = TimeSpan.FromMinutes(1);
        var defaultPolicy = new CachePolicy { JitterMaxDuration = maxJitter };
        var policyFactory = new DefaultCachePolicyFactory(Array.Empty<KeyValuePair<string, CachePolicy>>(), defaultPolicy);
        _fixture.Inject<ICachePolicyFactory>(policyFactory);
        _sut = null;

        var callerTtl = TimeSpan.FromMinutes(2);

        _innerCache.SetAsync<string?>(_cacheKey, Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _topic.PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>())
            .Returns(_ => true);

        await Sut.SetAsync(_cacheKey, "v", callerTtl, policy: null, TestContext.Current.CancellationToken);

        await _innerCache.Received(1).SetAsync<string?>(
            _cacheKey,
            "v",
            Arg.Is<DateTimeOffset?>(d => d.HasValue
                && d.Value - DateTimeOffset.UtcNow > callerTtl - TimeSpan.FromSeconds(5)
                && d.Value - DateTimeOffset.UtcNow < callerTtl + TimeSpan.FromSeconds(5)),
            Arg.Any<CachePolicy?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetOrAdd_jitters_policy_derived_expiration_within_max()
    {
        var baseTtl = TimeSpan.FromMinutes(5);
        var maxJitter = TimeSpan.FromSeconds(30);
        var defaultPolicy = new CachePolicy { DistributedExpiration = baseTtl, JitterMaxDuration = maxJitter };
        var policyFactory = new DefaultCachePolicyFactory(Array.Empty<KeyValuePair<string, CachePolicy>>(), defaultPolicy);
        _fixture.Inject<ICachePolicyFactory>(policyFactory);
        _sut = null;

        Func<CancellationToken, Task<string?>> generator = _ => Task.FromResult<string?>("v");

        _innerCache.GetCacheEntryAsync<string>(_cacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(new TestCacheEntry<string?> { Value = null });

        await Sut.GetOrAddAsync(_cacheKey, generator, policy: null, token: TestContext.Current.CancellationToken);

        await _innerCache.Received(1).SetAsync<string?>(
            _cacheKey,
            "v",
            Arg.Is<DateTimeOffset?>(d => d.HasValue
                && d.Value - DateTimeOffset.UtcNow >= baseTtl - TimeSpan.FromSeconds(5)
                && d.Value - DateTimeOffset.UtcNow <= baseTtl + maxJitter + TimeSpan.FromSeconds(5)),
            Arg.Any<CachePolicy?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetAsync_jitters_options_DefaultExpiration_when_policy_DistributedExpiration_is_null()
    {
        var optionsTtl = _options.DefaultExpiration!.Value;
        var maxJitter = TimeSpan.FromSeconds(30);
        var defaultPolicy = new CachePolicy { JitterMaxDuration = maxJitter }; // no DistributedExpiration
        var policyFactory = new DefaultCachePolicyFactory(Array.Empty<KeyValuePair<string, CachePolicy>>(), defaultPolicy);
        _fixture.Inject<ICachePolicyFactory>(policyFactory);
        _sut = null;

        _innerCache.SetAsync<string?>(_cacheKey, Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _topic.PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>())
            .Returns(_ => true);

        await Sut.SetAsync(_cacheKey, "v", (TimeSpan?)null, policy: null, TestContext.Current.CancellationToken);

        await _innerCache.Received(1).SetAsync<string?>(
            _cacheKey,
            "v",
            Arg.Is<DateTimeOffset?>(d => d.HasValue
                && d.Value - DateTimeOffset.UtcNow >= optionsTtl - TimeSpan.FromSeconds(5)
                && d.Value - DateTimeOffset.UtcNow <= optionsTtl + maxJitter + TimeSpan.FromSeconds(5)),
            Arg.Any<CachePolicy?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetAsync_DateTimeOffset_overload_jitters_when_caller_passes_null()
    {
        var baseTtl = TimeSpan.FromMinutes(5);
        var maxJitter = TimeSpan.FromSeconds(30);
        var defaultPolicy = new CachePolicy { DistributedExpiration = baseTtl, JitterMaxDuration = maxJitter };
        var policyFactory = new DefaultCachePolicyFactory(Array.Empty<KeyValuePair<string, CachePolicy>>(), defaultPolicy);
        _fixture.Inject<ICachePolicyFactory>(policyFactory);
        _sut = null;

        _innerCache.SetAsync<string?>(_cacheKey, Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _topic.PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>())
            .Returns(_ => true);

        await Sut.SetAsync(_cacheKey, "v", (DateTimeOffset?)null, policy: null, TestContext.Current.CancellationToken);

        await _innerCache.Received(1).SetAsync<string?>(
            _cacheKey,
            "v",
            Arg.Is<DateTimeOffset?>(d => d.HasValue
                && d.Value - DateTimeOffset.UtcNow >= baseTtl - TimeSpan.FromSeconds(5)
                && d.Value - DateTimeOffset.UtcNow <= baseTtl + maxJitter + TimeSpan.FromSeconds(5)),
            Arg.Any<CachePolicy?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetAsync_bulk_KeyValuePair_overload_jitters_policy_derived_expiration()
    {
        var baseTtl = TimeSpan.FromMinutes(5);
        var maxJitter = TimeSpan.FromSeconds(30);
        var defaultPolicy = new CachePolicy { DistributedExpiration = baseTtl, JitterMaxDuration = maxJitter };
        var policyFactory = new DefaultCachePolicyFactory(Array.Empty<KeyValuePair<string, CachePolicy>>(), defaultPolicy);
        _fixture.Inject<ICachePolicyFactory>(policyFactory);
        _sut = null;

        _innerCache.SetAsync<string?>(Arg.Any<KeyValuePair<CacheKey, string?>[]>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _topic.PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>())
            .Returns(_ => true);

        var keyValues = new[] { new KeyValuePair<CacheKey, string?>(_cacheKey, "v") };
        await Sut.SetAsync(keyValues, (CachePolicy?)null, TestContext.Current.CancellationToken);

        await _innerCache.Received(1).SetAsync<string?>(
            Arg.Any<KeyValuePair<CacheKey, string?>[]>(),
            Arg.Is<DateTimeOffset?>(d => d.HasValue
                && d.Value - DateTimeOffset.UtcNow >= baseTtl - TimeSpan.FromSeconds(5)
                && d.Value - DateTimeOffset.UtcNow <= baseTtl + maxJitter + TimeSpan.FromSeconds(5)),
            Arg.Any<CachePolicy?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetAsync_jitter_actually_varies_across_calls()
    {
        // Capture per-call relative TTL (expireAt − now_at_capture) so wall-clock drift across
        // the 30 iterations doesn't masquerade as jitter variance on a contended CI agent.
        var baseTtl = TimeSpan.FromSeconds(1);
        var maxJitter = TimeSpan.FromSeconds(10);
        var defaultPolicy = new CachePolicy { DistributedExpiration = baseTtl, JitterMaxDuration = maxJitter };
        var policyFactory = new DefaultCachePolicyFactory(Array.Empty<KeyValuePair<string, CachePolicy>>(), defaultPolicy);
        _fixture.Inject<ICachePolicyFactory>(policyFactory);
        _sut = null;

        var ttls = new List<TimeSpan>();
        _innerCache.SetAsync<string?>(_cacheKey, Arg.Any<string?>(),
                Arg.Do<DateTimeOffset?>(d => { if (d.HasValue) { ttls.Add(d.Value - DateTimeOffset.UtcNow); } }),
                Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _topic.PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>())
            .Returns(_ => true);

        for (int i = 0; i < 30; i++)
        {
            await Sut.SetAsync(_cacheKey, "v", (CachePolicy?)null, TestContext.Current.CancellationToken);
        }

        ttls.Should().HaveCount(30);
        (ttls.Max() - ttls.Min()).Should().BeGreaterThan(TimeSpan.FromSeconds(1),
            "across 30 draws with maxJitter=10s, per-call TTL spread should clearly exceed 1s — a no-op ApplyJitter would produce zero spread independent of wall-clock time");
    }

    [Fact]
    public async Task SetAsync_clamps_jitter_when_base_plus_jitter_would_overflow_DateTime()
    {
        var baseTtl = TimeSpan.FromHours(1);
        var defaultPolicy = new CachePolicy { DistributedExpiration = baseTtl, JitterMaxDuration = TimeSpan.MaxValue };
        var policyFactory = new DefaultCachePolicyFactory(Array.Empty<KeyValuePair<string, CachePolicy>>(), defaultPolicy);
        _fixture.Inject<ICachePolicyFactory>(policyFactory);
        _sut = null;

        _innerCache.SetAsync<string?>(_cacheKey, Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _topic.PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>())
            .Returns(_ => true);

        Func<Task> act = async () =>
            await Sut.SetAsync(_cacheKey, "v", (CachePolicy?)null, TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync(
            "ApplyJitter must clamp the result to UtcNow's remaining range so an absurd JitterMaxDuration can't crash writes");

        await _innerCache.Received(1).SetAsync<string?>(
            _cacheKey, "v",
            Arg.Is<DateTimeOffset?>(d => d.HasValue && d.Value <= DateTimeOffset.MaxValue),
            Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
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
