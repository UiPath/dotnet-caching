using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute.ExceptionExtensions;
using UiPath.Caching;
using UiPath.Caching.Config;
using UiPath.Caching.Locking;
using UiPath.Caching.Telemetry;
using UiPath.Caching.Tests.Broadcast;

namespace UiPath.Caching.Tests;

/// <summary>
/// Conditional add across the two tiers. The invariant: L1 never arbitrates while an L2 exists — a
/// key missing locally may still exist in the shared store, so a local probe would hand the same win
/// to every node.
/// </summary>
public class MultilayerCacheTryAddTests(ITestContextAccessor testContextAccessor) : IAsyncLifetime
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubstitute();

    private ICache _innerCache = default!;
    private IChangeTokenFactory _changeTokenFactory = default!;
    private ITopicFactory _topicFactory = default!;
    private ITopicProviderWithConnectionState _topicProvider = default!;
    private ITopic<ICacheEvent> _topic = default!;
    private IMemoryCache _memoryCache = default!;
    private IMemoryCacheFactory _memoryCacheFactory = default!;
    private InMemoryRedisCacheOptions _options = default!;
    private TopicKey _topicKey = default!;
    private CacheKey _cacheKey = default!;
    private ILogger _logger = default!;

    private MultilayerCache? _sut;

    private MultilayerCache Sut => _sut ??= _fixture.Create<MultilayerCache>();

    private CancellationToken Ct => testContextAccessor.Current.CancellationToken;

    [Fact]
    public async Task TryAdd_delegates_the_decision_to_the_inner_cache()
    {
        var value = _fixture.Create<string>();
        _innerCache.TryAddAsync<string?>(_cacheKey, value, Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _topic.PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>()).Returns(_ => true);

        var added = await Sut.TryAddAsync(_cacheKey, value, policy: null, token: Ct);

        added.Should().BeTrue();
        await _innerCache.Received(1).TryAddAsync<string?>(_cacheKey, value, Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
        _memoryCache.Received(1).CreateEntry(_cacheKey);
    }

    [Fact]
    public async Task A_local_hit_reports_the_loss_without_asking_the_L2()
    {
        _memoryCache.TryGetValue(_cacheKey, out Arg.Any<object?>())
            .Returns(x =>
            {
                x[1] = new TestCacheEntry<string?> { Value = _fixture.Create<string>() };
                return true;
            });

        var added = await Sut.TryAddAsync(_cacheKey, _fixture.Create<string>(), policy: null, token: Ct);

        added.Should().BeFalse();
        await _innerCache.DidNotReceive().TryAddAsync<string?>(_cacheKey, Arg.Any<string?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryAdd_leaves_both_tiers_untouched_when_the_inner_cache_reports_a_loss()
    {
        _innerCache.TryAddAsync<string?>(_cacheKey, Arg.Any<string?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var added = await Sut.TryAddAsync(_cacheKey, _fixture.Create<string>(), policy: null, token: Ct);

        added.Should().BeFalse();
        _memoryCache.DidNotReceive().CreateEntry(_cacheKey);
        await _topic.DidNotReceive().PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryAdd_broadcasts_after_a_win_so_peers_drop_stale_local_copies()
    {
        _innerCache.TryAddAsync<string?>(_cacheKey, Arg.Any<string?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _topic.PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>()).Returns(_ => true);

        await Sut.TryAddAsync(_cacheKey, _fixture.Create<string>(), policy: null, token: Ct);

        await _topic.Received(1).PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryAdd_still_reports_the_win_when_the_broadcast_fails()
    {
        _innerCache.TryAddAsync<string?>(_cacheKey, Arg.Any<string?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _topic.PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("broadcast down"));

        var added = await Sut.TryAddAsync(_cacheKey, _fixture.Create<string>(), policy: null, token: Ct);

        added.Should().BeTrue("the key is claimed in the shared store; denying the win would strand it with no owner");
    }

    [Fact]
    public async Task TryAdd_fails_closed_when_the_inner_cache_throws()
    {
        _innerCache.TryAddAsync<string?>(_cacheKey, Arg.Any<string?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("redis down"));

        var added = await Sut.TryAddAsync(_cacheKey, _fixture.Create<string>(), policy: null, token: Ct);

        added.Should().BeFalse();
        _memoryCache.DidNotReceive().CreateEntry(_cacheKey);
    }

    [Fact]
    public async Task TryAdd_reports_a_loss_when_the_inner_cache_cannot_arbitrate_at_all()
    {
        _innerCache.TryAddAsync<string?>(_cacheKey, Arg.Any<string?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new NotSupportedException("no NX here"));

        var added = await Sut.TryAddAsync(_cacheKey, _fixture.Create<string>(), policy: null, token: Ct);

        added.Should().BeFalse("an inner cache that cannot arbitrate is logged and reported as a loss, which is the fail-closed direction the ambiguous false already covers");
        _memoryCache.DidNotReceive().CreateEntry(_cacheKey);
    }

    /// <summary>
    /// A deadline that has passed used to be a silent no-op returning false. The expiration is no
    /// longer nullable, so there is nothing left for such a value to mean and it is rejected at the
    /// boundary instead of being confused with "somebody else holds the key".
    /// </summary>
    [Fact]
    public async Task TryAdd_rejects_an_expiration_that_has_already_passed()
    {
        var act = async () => await Sut.TryAddAsync(_cacheKey, _fixture.Create<string>(), DateTimeOffset.UtcNow.AddMinutes(-5), token: Ct);

        (await act.Should().ThrowAsync<ArgumentOutOfRangeException>()).And.ParamName.Should().Be("expiration");
        await _innerCache.DidNotReceive().TryAddAsync<string?>(_cacheKey, Arg.Any<string?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
        _memoryCache.DidNotReceive().CreateEntry(_cacheKey);
    }

    /// <inheritdoc cref="TryAdd_rejects_an_expiration_that_has_already_passed"/>
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task TryAdd_rejects_a_non_positive_expiration(int minutes)
    {
        var act = async () => await Sut.TryAddAsync(_cacheKey, _fixture.Create<string>(), TimeSpan.FromMinutes(minutes), token: Ct);

        (await act.Should().ThrowAsync<ArgumentOutOfRangeException>()).And.ParamName.Should().Be("expiration");
        await _innerCache.DidNotReceive().TryAddAsync<string?>(_cacheKey, Arg.Any<string?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_broadcast_that_reports_not_published_still_stands_and_is_logged()
    {
        // CacheSetAsync signals an ordinary publish failure with false rather than throwing, so the
        // catch alone would let it pass unreported.
        _innerCache.TryAddAsync<string?>(_cacheKey, Arg.Any<string?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _topic.PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>()).Returns(_ => false);

        var added = await Sut.TryAddAsync(_cacheKey, _fixture.Create<string>(), policy: null, token: Ct);

        added.Should().BeTrue();
        _logger.Received().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<Arg.AnyType>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<Arg.AnyType, Exception?, string>>());
    }

    [Fact]
    public async Task A_failed_broadcast_still_populates_the_local_tier()
    {
        // The broadcast and the L1 write are independent best-effort steps after the win; a dead
        // topic must not cost the winning node its local copy.
        _innerCache.TryAddAsync<string?>(_cacheKey, Arg.Any<string?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _topic.PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("broadcast down"));

        var added = await Sut.TryAddAsync(_cacheKey, _fixture.Create<string>(), policy: null, token: Ct);

        added.Should().BeTrue();
        _memoryCache.Received(1).CreateEntry(_cacheKey);
    }

    [Fact]
    public async Task TryAdd_fails_closed_when_the_inner_cache_cannot_claim_the_key()
    {
        _options.UseLocalOnlyWhenDisconnected = true;
        _options.ConnectionMonitorEnabled = true;
        _innerCache.TryAddAsync<string?>(_cacheKey, Arg.Any<string?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(false);
        _sut = null;

        var added = await Sut.TryAddAsync(_cacheKey, _fixture.Create<string>(), policy: null, token: Ct);

        added.Should().BeFalse("a local-only claim would be granted to every node independently");
        _memoryCache.DidNotReceive().CreateEntry(_cacheKey);
    }

    [Fact]
    public async Task A_disconnected_broadcast_transport_does_not_stop_a_healthy_L2_from_arbitrating()
    {
        _options.UseLocalOnlyWhenDisconnected = true;
        _options.ConnectionMonitorEnabled = true;
        _topicProvider.IsConnected.Returns(false);
        _innerCache.TryAddAsync<string?>(_cacheKey, Arg.Any<string?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _sut = null;

        var added = await Sut.TryAddAsync(_cacheKey, _fixture.Create<string>(), policy: null, token: Ct);

        added.Should().BeTrue("the aggregate connection state covers the topic too, and broadcast is best-effort after a win");
        await _innerCache.Received(1).TryAddAsync<string?>(_cacheKey, Arg.Any<string?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryAdd_surfaces_a_cancellation_raised_by_the_inner_cache()
    {
        using var cts = new CancellationTokenSource();
#pragma warning disable CA2012
        _innerCache.TryAddAsync<string?>(_cacheKey, Arg.Any<string?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns<ValueTask<bool>>(_ =>
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            });
#pragma warning restore CA2012

        var act = async () => await Sut.TryAddAsync(_cacheKey, _fixture.Create<string>(), policy: null, token: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "reporting false would say someone else owns the key, which InMemoryRedis must not claim any more than Redis does");
        await _innerCache.Received(1).TryAddAsync<string?>(_cacheKey, Arg.Any<string?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryAdd_of_a_null_value_never_deletes_and_never_reaches_the_inner_cache()
    {
        _options.CacheNullValues = false;
        _sut = null;

        var added = await Sut.TryAddAsync(_cacheKey, default(string), policy: null, token: Ct);

        added.Should().BeFalse();
        _memoryCache.DidNotReceive().Remove(_cacheKey);
        await _innerCache.DidNotReceive().RemoveAsync<string>(_cacheKey, Arg.Any<CancellationToken>());
        await _innerCache.DidNotReceive().TryAddAsync<string?>(_cacheKey, Arg.Any<string?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryAdd_of_a_null_value_reaches_the_inner_cache_when_CacheNullValues_is_on()
    {
        _options.CacheNullValues = true;
        _sut = null;
        _innerCache.TryAddAsync<string?>(_cacheKey, default, Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _topic.PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>()).Returns(_ => true);

        var added = await Sut.TryAddAsync(_cacheKey, default(string), policy: null, token: Ct);

        added.Should().BeTrue();
        await _innerCache.Received(1).TryAddAsync<string?>(_cacheKey, default, Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryAdd_forwards_the_caller_expiration_to_the_inner_cache()
    {
        var ttl = TimeSpan.FromMinutes(7);
        _innerCache.TryAddAsync<string?>(_cacheKey, Arg.Any<string?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _topic.PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>()).Returns(_ => true);

        await Sut.TryAddAsync(_cacheKey, _fixture.Create<string>(), ttl, token: Ct);

        await _innerCache.Received(1).TryAddAsync<string?>(
            _cacheKey,
            Arg.Any<string?>(),
            Arg.Is<DateTimeOffset>(e => e > DateTimeOffset.UtcNow),
            Arg.Any<CachePolicy?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_L2_answer_is_taken_as_given_whatever_the_L2_is()
    {
        var inner = CreateInMemorySut();
        _fixture.Inject<ICache>(inner);
        using var sut = _fixture.Create<MultilayerCache>();

        var added = await sut.TryAddAsync(_cacheKey, _fixture.Create<string>(), policy: null, token: Ct);

        added.Should().BeTrue("the L2 granted the claim, and the outer cache does not second-guess it");
    }

    private static MultilayerCache CreateInMemorySut()
    {
        var options = new InMemoryCacheOptions();
        var cacheOptions = new CacheOptions { AppShortName = "test" };
        return new MultilayerCache(
            KnownCacheProviderNames.InMemory,
            NullCache.Instance,
            new MemoryCacheFactory(null, NullLoggerFactory.Instance),
            NullChangeTokenFactory.Instance,
            NullTopicFactory.Instance,
            NullCacheEventFactory.Instance,
            NullTelemetryProvider.Instance,
            options,
            options,
            cacheOptions,
            localLock: new AsyncKeyedLocalLock(Options.Create(cacheOptions)),
            distributedLock: NullDistributedLock.Instance,
            policyFactory: NullCachePolicyFactory.Instance,
            logger: NullLogger.Instance);
    }

    [Fact]
    public async Task TryAdd_rejects_a_null_key()
    {
        string? nullKey = null;
        var act = async () => await Sut.TryAddAsync(nullKey!, _fixture.Create<string>(), policy: null, token: Ct);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    public ValueTask InitializeAsync()
    {
        _cacheKey = _fixture.Create<string>();
        _topicKey = _fixture.Create<string>();

        _changeTokenFactory = _fixture.Freeze<IChangeTokenFactory>();
        _memoryCache = _fixture.Freeze<IMemoryCache>();
        _innerCache = _fixture.Freeze<ICache>();
        _logger = _fixture.Freeze<ILogger>();
        _logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        _options = new()
        {
            DefaultExpiration = TimeSpan.FromMinutes(10),
            EntryFactory = new TestCacheEntryFactory(),
        };

        var cacheKeyStrategy = _fixture.Create<ICacheKeyStrategy>();
        var topicKeyStrategy = _fixture.Create<ITopicKeyStrategy>();
        cacheKeyStrategy.GetCacheKey<string>(_cacheKey).Returns(_cacheKey);
        topicKeyStrategy.GetTopicKey<string>().Returns(_topicKey);
        _topicFactory = _fixture.Freeze<ITopicFactory>();
        _topicProvider = _fixture.Freeze<ITopicProviderWithConnectionState>();
        _topic = _fixture.Freeze<ITopic<ICacheEvent>>();
        _topicFactory.Get(Arg.Any<string>()).Returns(_topicProvider);
        _topicProvider.Create(_topicKey).Returns(_topic);
        _topicProvider.Create(Arg.Any<TopicKey>()).Returns(_topic);
        _memoryCacheFactory = _fixture.Freeze<IMemoryCacheFactory>();
        _memoryCacheFactory.Get(Arg.Any<IMemoryCacheOptions>()).Returns(_ => _memoryCache);
        _fixture.Inject<IMultilayerCacheOptions>(_options);
        _fixture.Inject<ILocalLock>(new AsyncKeyedLocalLock(Options.Create(new CacheOptions { AppShortName = "test" })));
        _memoryCache.TryGetValue(Arg.Any<object>(), out Arg.Any<object?>()).Returns(false);
        _fixture.Inject<IMemoryCacheOptions>(_options);
        _fixture.Inject<IEventFormatterProxy<ICacheEvent>>(new CacheClearEventFormatterProxy());
        var cacheEventFactory = _fixture.Freeze<ICacheEventFactory>();
        cacheEventFactory.Create(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CacheEventData>(), Arg.Any<string?>())
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
        _sut?.Dispose();
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    public interface ITopicProviderWithConnectionState : ITopicProvider, IConnectionState
    {
    }
}

/// <summary>
/// The memory-only provider: a real <see cref="MultilayerCache"/> over <see cref="NullCache"/>, so
/// the local tier is the storage <em>and</em> the arbiter. Exclusion here is in-process only, which
/// is the honest ceiling for a cache with no shared store — these tests pin that it is at least
/// correct within the process.
/// </summary>
public class InMemoryCacheTryAddTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static MultilayerCache CreateSut(
        InMemoryCacheOptions? options = null,
        ILocalLock? localLock = null)
    {
        options ??= new InMemoryCacheOptions();
        var cacheOptions = new CacheOptions { AppShortName = "test" };
        return new MultilayerCache(
            KnownCacheProviderNames.InMemory,
            NullCache.Instance,
            new MemoryCacheFactory(null, NullLoggerFactory.Instance),
            NullChangeTokenFactory.Instance,
            NullTopicFactory.Instance,
            NullCacheEventFactory.Instance,
            NullTelemetryProvider.Instance,
            options,
            options,
            cacheOptions,
            localLock: localLock ?? new AsyncKeyedLocalLock(Options.Create(cacheOptions)),
            distributedLock: NullDistributedLock.Instance,
            policyFactory: NullCachePolicyFactory.Instance,
            logger: NullLogger.Instance);
    }

    [Fact]
    public async Task First_caller_adds_and_the_second_loses()
    {
        using var sut = CreateSut();

        (await sut.TryAddAsync("k", "first", policy: null, token: Ct)).Should().BeTrue();
        (await sut.TryAddAsync("k", "second", policy: null, token: Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task A_lost_add_does_not_overwrite_the_winner_value()
    {
        using var sut = CreateSut();

        await sut.TryAddAsync("k", "first", policy: null, token: Ct);
        await sut.TryAddAsync("k", "second", policy: null, token: Ct);

        (await sut.GetAsync<string>("k", policy: null, token: Ct)).Should().Be("first");
    }

    [Fact]
    public async Task The_key_is_claimable_again_once_removed()
    {
        using var sut = CreateSut();

        await sut.TryAddAsync("k", "first", policy: null, token: Ct);
        await sut.RemoveAsync<string>("k", Ct);

        (await sut.TryAddAsync("k", "second", policy: null, token: Ct)).Should().BeTrue();
        (await sut.GetAsync<string>("k", policy: null, token: Ct)).Should().Be("second");
    }

    [Fact]
    public async Task TryAdd_does_not_claim_a_key_an_unconditional_set_already_wrote()
    {
        using var sut = CreateSut();

        await sut.SetAsync("k", "written", policy: null, token: Ct);

        (await sut.TryAddAsync("k", "claimed", policy: null, token: Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task Exactly_one_of_many_concurrent_callers_wins()
    {
        using var sut = CreateSut();
        const int callers = 32;

        var results = await Task.WhenAll(Enumerable.Range(0, callers).Select(i =>
            Task.Run(async () => await sut.TryAddAsync("k", $"caller-{i}", policy: null, token: Ct), Ct)));

        results.Count(won => won).Should().Be(1, "the local lock is what makes probe-then-write atomic");
    }

    [Fact]
    public async Task The_local_probe_narrows_an_L2_that_grants_everyone_a_win()
    {
        using var sut = CreateSut();

        (await sut.TryAddAsync("k", "first", policy: null, token: Ct)).Should().BeTrue();
        (await sut.TryAddAsync("k", "second", policy: null, token: Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task Exactly_one_caller_still_wins_with_the_local_lock_disabled()
    {
        using var sut = CreateSut(new InMemoryCacheOptions { LocalLockEnabled = false });
        const int callers = 32;

        var results = await Task.WhenAll(Enumerable.Range(0, callers).Select(i =>
            Task.Run(async () => await sut.TryAddAsync("k", $"caller-{i}", policy: null, token: Ct), Ct)));

        results.Count(won => won).Should().Be(1, "an unserialized probe-then-write would hand the same win to several callers");
    }

    [Fact]
    public async Task A_caller_that_cannot_take_the_local_lock_is_told_it_lost()
    {
        var options = new InMemoryCacheOptions { LocalLockTimeout = TimeSpan.FromMilliseconds(50) };
        using var sut = CreateSut(options, localLock: new NeverGrantingLocalLock());

        var added = await sut.TryAddAsync("k", "first", policy: null, token: Ct);

        added.Should().BeFalse();
        (await sut.GetAsync<string>("k", policy: null, token: Ct)).Should().BeNull("a loss must not write anything either");
    }

    [Fact]
    public async Task A_size_limited_memory_cache_that_drops_the_entry_still_reports_the_win()
    {
        using var sut = CreateSut(new InMemoryCacheOptions { SizeLimit = 1, SizeProvider = new OversizedEntryProvider() });

        (await sut.TryAddAsync("k", "first", policy: null, token: Ct)).Should().BeTrue();
        (await sut.TryAddAsync("k", "second", policy: null, token: Ct)).Should().BeTrue();
    }

    private sealed class OversizedEntryProvider : ICacheEntrySizeProvider
    {
        public long GetSize(ICacheEntry entry) => long.MaxValue;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task A_non_positive_local_retention_claims_nothing(int minutes)
    {
        using var sut = CreateSut();
        var policy = new CachePolicy { LocalExpiration = TimeSpan.FromMinutes(minutes) };

        (await sut.TryAddAsync("k", "first", policy, token: Ct)).Should().BeFalse();
        (await sut.TryAddAsync("k", "second", policy, token: Ct)).Should().BeFalse();
        (await sut.GetAsync<string>("k", policy: null, token: Ct)).Should().BeNull();
    }

    [Fact]
    public async Task An_expiration_that_has_already_passed_is_rejected()
    {
        using var sut = CreateSut();
        var past = DateTimeOffset.UtcNow.AddMinutes(-5);

        var act = async () => await sut.TryAddAsync("k", "first", past, token: Ct);

        (await act.Should().ThrowAsync<ArgumentOutOfRangeException>()).And.ParamName.Should().Be("expiration");
        (await sut.GetAsync<string>("k", policy: null, token: Ct)).Should().BeNull();
    }

    /// <summary>
    /// Stands in for a local lock held by someone else for longer than the acquire budget: the wait is
    /// abandoned by the linked timeout, which is the only way <c>AcquireLocalLockAsync</c> answers null.
    /// </summary>
    private sealed class NeverGrantingLocalLock : ILocalLock
    {
        public async ValueTask<IDisposable> AcquireAsync(string key, CancellationToken token)
        {
            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
            throw new InvalidOperationException("unreachable: the delay above only ever cancels");
        }
    }
}
