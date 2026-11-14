#pragma warning disable CA2012 // NSubstitute setups call the ValueTask-returning members without consuming the result.
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using UiPath.Caching;

namespace UiPath.Caching.Tests;

public class InMemoryRedisCacheProviderTests : IAsyncLifetime
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubstitute();
    private InMemoryRedisCacheOptions _options = default!;

    private InMemoryRedisCacheProvider? _sut = null;
    private InMemoryRedisCacheProvider Sut => _sut ??= _fixture.Create<InMemoryRedisCacheProvider>();

    [Fact]
    public void Works_as_expected()
    {
        Sut.CreateCache().Should().BeOfType<MultilayerCache>();
        Sut.CreateHashCache().Should().BeOfType<MultilayerHashCache>();
        Sut.Name.Should().Be("InMemoryRedis");
        Sut.Enabled.Should().Be(_options.Enabled);
    }


    [Fact]
    public void An_uncapped_local_tier_logs_one_warning_naming_the_setting()
    {
        var logger = FreezeRecordingLogger();
        _options.Enabled = true;
        _options.LocalMaxExpiration = null;
        InjectDefaultPolicy(null);

        _ = Sut;
        Sut.CreateCache();
        Sut.CreateHashCache();

        logger.Warnings.Should().ContainSingle().Which.Should().Contain("LocalMaxExpiration");
    }

    [Fact]
    public void A_maximum_local_max_expiration_is_no_cap_and_logs_the_warning()
    {
        var logger = FreezeRecordingLogger();
        _options.Enabled = true;
        _options.LocalMaxExpiration = TimeSpan.MaxValue;
        InjectDefaultPolicy(null);

        _ = Sut;

        logger.Warnings.Should().ContainSingle().Which.Should().Contain("LocalMaxExpiration");
    }

    [Fact]
    public void A_maximum_default_policy_local_expiration_is_no_cap_and_logs_the_warning()
    {
        var logger = FreezeRecordingLogger();
        _options.Enabled = true;
        _options.LocalMaxExpiration = null;
        InjectDefaultPolicy(new CachePolicy { LocalExpiration = TimeSpan.MaxValue });

        _ = Sut;

        logger.Warnings.Should().ContainSingle().Which.Should().Contain("LocalExpiration");
    }

    [Fact]
    public void A_fail_safe_default_policy_is_no_cap_and_logs_the_warning()
    {
        var logger = FreezeRecordingLogger();
        _options.Enabled = true;
        _options.LocalMaxExpiration = null;
        _options.SizeLimit = null;
        InjectDefaultPolicy(new CachePolicy { FailSafeEnabled = true, FailSafeMaxStaleAge = TimeSpan.FromMinutes(5) });

        _ = Sut;

        logger.Warnings.Should().ContainSingle(w => w.StartsWith("The InMemoryRedis local tier has no lifetime cap", StringComparison.Ordinal), "a fail-safe stale age is no cap on the local tier");
        logger.Warnings.Should().ContainSingle(w => w.Contains("enables fail-safe") && w.Contains(FailSafeLifetimeWarning.DefaultPolicyName), "the fail-safe policy is named once");
    }

    [Fact]
    public void A_size_limited_provider_logs_no_fail_safe_warning()
    {
        var logger = FreezeRecordingLogger();
        _options.Enabled = true;
        _options.LocalMaxExpiration = TimeSpan.FromMinutes(5);
        _options.SizeLimit = 1000;
        InjectDefaultPolicy(new CachePolicy { FailSafeEnabled = true });

        _ = Sut;

        logger.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void A_capped_provider_without_a_size_limit_still_logs_the_fail_safe_warning()
    {
        var logger = FreezeRecordingLogger();
        _options.Enabled = true;
        _options.LocalMaxExpiration = TimeSpan.FromMinutes(5);
        _options.SizeLimit = null;
        InjectDefaultPolicy(new CachePolicy { FailSafeEnabled = true });

        _ = Sut;

        logger.Warnings.Should().ContainSingle().Which.Should().Contain("enables fail-safe").And.Contain("InMemoryRedis SizeLimit is unset");
    }

    [Fact]
    public void A_capped_local_tier_logs_no_warning()
    {
        var logger = FreezeRecordingLogger();
        _options.Enabled = true;
        _options.LocalMaxExpiration = TimeSpan.FromMinutes(5);

        Sut.CreateCache();

        logger.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void A_cap_from_the_default_policy_logs_no_warning()
    {
        var logger = FreezeRecordingLogger();
        _options.Enabled = true;
        _options.LocalMaxExpiration = null;
        InjectDefaultPolicy(new CachePolicy { LocalExpiration = TimeSpan.FromMinutes(5) });

        _ = Sut;

        logger.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void A_disabled_provider_logs_no_warning()
    {
        var logger = FreezeRecordingLogger();
        _options.Enabled = false;
        _options.LocalMaxExpiration = null;
        InjectDefaultPolicy(null);

        _ = Sut;

        logger.Warnings.Should().BeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Broadcast_option(bool enabled)
    {
        _options.BroadcastEnable = enabled;
        // A set announces only what the inner tier took, so the substitutes must take it.
        var factory = _fixture.Freeze<ICacheFactory>();
        var inner = Substitute.For<ICache>();
        inner.SetAsync<string?>(Arg.Any<CacheKey>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()).Returns(new ValueTask<bool>(true));
        factory.CreateCache(Arg.Any<string>()).Returns(inner);
        var innerHash = Substitute.For<IHashCache>();
        innerHash.SetAsync<string?>(Arg.Any<CacheKey>(), Arg.Any<IDictionary<string, string?>>(), Arg.Any<HashCacheEntryOptions>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()).Returns(new ValueTask<bool>(true));
        factory.CreateHashCache(Arg.Any<string>()).Returns(innerHash);
        var topicFactory = _fixture.Freeze<ITopicFactory>();
        var topicProvider = _fixture.Freeze<ITopicProvider>();
        topicFactory.Get(Arg.Any<string?>()).Returns(topicProvider);
        var topic = _fixture.Create<ITopic<ICacheEvent>>();
        topic.PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>()).Returns(_ => true);
        var topicCallsCount = 0;
        topicProvider.Create(_fixture.Create<string>())
            .ReturnsForAnyArgs(_ =>
            {
                topicCallsCount++;
                return topic;
            });

        var changeTokenFactory = _fixture.Freeze<IChangeTokenFactory>();
        var token = _fixture.Create<IChangeToken>();
        var tokenCallsCount = 0;
        changeTokenFactory.Create(Arg.Any<string>(), Arg.Any<ITopic<ICacheEvent>>(), Arg.Any<string>(), Arg.Any<Type>())
            .ReturnsForAnyArgs(_ =>
            {
                tokenCallsCount++;
                return token;
            });

        var cacheEventFactory = _fixture.Freeze<ICacheEventFactory>();
        var cacheEvent = _fixture.Create<ICacheEvent>();
        var eventCallsCount = 0;
        cacheEventFactory.Create(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CacheEventData>(), Arg.Any<string>())
            .ReturnsForAnyArgs(_ =>
            {
                eventCallsCount++;
                return cacheEvent;
            });

        var cache = Sut.CreateCache();
        var hashCache = Sut.CreateHashCache();
        await cache.SetAsync(_fixture.Create<string>(), _fixture.Create<string>(), policy: null, token: TestContext.Current.CancellationToken);
        var values = _fixture.Create<IDictionary<string, string?>>();
        await hashCache.SetAsync(_fixture.Create<string>(), values, policy: null, token: TestContext.Current.CancellationToken);

        if (enabled)
        {
            // The token and event counters also depend on the L1 write path, and the inner Redis cache is a
            // substitute here, so topic resolution is the signal that the broadcast wiring is live.
            topicCallsCount.Should().BeGreaterThan(0);
        }
        else
        {
            topicCallsCount.Should().Be(0, "BroadcastEnable: false must keep L1+L2 working without any broadcast traffic");
            tokenCallsCount.Should().Be(0);
            eventCallsCount.Should().Be(0);
        }
    }

    [Fact]
    public void Dispose_can_be_called()
    {
        var x = Sut.CreateCache();
        var y = Sut.CreateHashCache();
        Action act = () => Sut.Dispose();
        act.Should().NotThrow();
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    public ValueTask InitializeAsync()
    {
        _options = _fixture.Build<InMemoryRedisCacheOptions>()
            .Without(x => x.LocalSingleFlightEnabled)
            .Without(x => x.DistributedLockEnabled)
            .Without(x => x.CacheKeyStrategy)
            .Create();
        _fixture.Inject(Options.Create(_options));
        return ValueTask.CompletedTask;
    }

    private void InjectDefaultPolicy(CachePolicy? policy)
    {
        var factory = Substitute.For<ICachePolicyFactory>();
        factory.Default.Returns(policy);
        factory.Keys.Returns([]);
        _fixture.Inject(factory);
    }

    private RecordingLogger FreezeRecordingLogger()
    {
        var logger = new RecordingLogger();
        _fixture.Freeze<ILoggerFactory>().CreateLogger("InMemoryRedis").Returns(logger);
        return logger;
    }

    private sealed class RecordingLogger : ILogger
    {
        private readonly List<string> _warnings = [];

        public IReadOnlyList<string> Warnings => _warnings;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                _warnings.Add(formatter(state, exception));
            }
        }
    }
}
