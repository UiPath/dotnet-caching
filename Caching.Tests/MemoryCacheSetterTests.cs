using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;
using UiPath.Platform.Caching;
using UiPath.Platform.Caching.Telemetry;
using UiPath.Platform.Caching.Tests.Broadcast;

namespace UiPath.Platform.Caching.Tests;

public class MemoryCacheSetterTests : IAsyncLifetime
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubstitute();

    private IChangeTokenFactory _changeTokenFactory = default!;
    private ITopicFactory _topicFactory = default!;
    private ITopicProvider _topicProvider = default!;
    private ITopic<ICacheEvent> _topic = default!;
    private ICacheKeyStrategy _cacheKeyStrategy = default!;
    private ITopicKeyStrategy _topicKeyStrategy = default!;
    private IMemoryCache _memoryCache = default!;
    private ISystemClock _clock = default!;
    private IEventFormatterProxy<ICacheEvent> _formatter = default!;
    private ICacheEventFactory _cacheEventFactory = default!;
    private InMemoryRedisCacheOptions _options = default!;
    private TopicKey _topicKey = default!;
    private CacheKey _cacheKey = default!;
    private CacheClock _cacheClock = default!;
    private ICachingTelemetryProvider _telemetryProvider = default!;

    private HashLocalMemorySetter? _sut = null;

    private HashLocalMemorySetter Sut => _sut ??= _fixture.Create<HashLocalMemorySetter>();

    [Fact]
    public void Setter_inner_exception()
    {
        _memoryCache = new MemoryCache(Options.Create(new MemoryCacheOptions
        {
            Clock = _clock
        }));
        _fixture.Inject(_memoryCache);

        var token = new TestChangeToken
        {
            ActiveChangeCallbacks = true,
            HasChanged = false,
            Expiration = _clock.UtcNow.AddDays(1),
            TransportId = "1234567890"
        };
        _changeTokenFactory.Create(Arg.Any<string>(), Arg.Any<ITopic<ICacheEvent>>(), Arg.Any<string>(), Arg.Any<Type>())
            .Returns(c => token, c=> throw new Exception());
        var x = new InternalHashCacheEntryOptions()
        {
            CacheKey = _cacheKey,
            TopicKey = _topicKey,
            Expiration = _clock.UtcNow.AddDays(1),
        };

        Sut.Set(x, _fixture.Create<ICacheEntry>(), _fixture.Create<Type>(), _fixture.Create<TimeSpan?>());
        token.InvokeCallbacks();
        _memoryCache.TryGetValue(_cacheKey, out _).Should().BeFalse();
    }

    [Fact]
    public void Setter_emits_failure_event()
    {
        _memoryCache = new MemoryCache(Options.Create(new MemoryCacheOptions
        {
            Clock = _clock
        }));
        _fixture.Inject(_memoryCache);

        var token = new TestChangeToken
        {
            ActiveChangeCallbacks = true,
            HasChanged = false,
            Expiration = _clock.UtcNow.AddDays(1),

        };
        bool @throw = false;
        _changeTokenFactory.Create(Arg.Any<string>(), Arg.Any<ITopic<ICacheEvent>>(), Arg.Any<string>(), Arg.Any<Type>())
            .Returns(c =>
            {
                if (@throw)
                {
                    throw new Exception();
                }
                else
                {
                    return token;
                }
            });
        var x = new InternalHashCacheEntryOptions()
        {
            CacheKey = _cacheKey,
            TopicKey = _topicKey,
            Expiration = _clock.UtcNow.AddDays(1),
        };

        Sut.Set(x, _fixture.Create<ICacheEntry>(), _fixture.Create<Type>(), TimeSpan.FromMinutes(1));
        @throw = true;
        token.InvokeCallbacks();
        var eventDimensionPredicate = (IDictionary<string, string> d) =>
        {
            return d["CacheKey"] == _cacheKey && d["TopicKey"] == _topicKey && (d["TransportId"] == null || d["TransportId"] == string.Empty);
        };

        _telemetryProvider.Received().TrackEvent($"Caching.{nameof(MemoryCacheSetter)}.{nameof(MemoryCacheSetter.RefreshMetadata)}.Failed",
            Arg.Is<IDictionary<string, string>>(d => eventDimensionPredicate(d)),
            Arg.Any<IDictionary<string, double>>());

    }

    [Fact]
    public void Setter_max_duration()
    {
        _memoryCache = new MemoryCache(Options.Create(new MemoryCacheOptions
        {
            Clock = _clock
        }));
        _fixture.Inject(_memoryCache);

        var token = new TestChangeToken
        {
            ActiveChangeCallbacks = true,
            HasChanged = false,
            Expiration = _clock.UtcNow.AddDays(1),
        };
        _changeTokenFactory.Create(Arg.Any<string>(), Arg.Any<ITopic<ICacheEvent>>(), Arg.Any<string>(), Arg.Any<Type>())
            .Returns(c => token);
        var x = new InternalHashCacheEntryOptions()
        {
            CacheKey = _cacheKey,
            TopicKey = _topicKey,
            Expiration = _clock.UtcNow.AddDays(1),
        };

        Sut.Set(x, _fixture.Create<ICacheEntry>(), _fixture.Create<Type>(), TimeSpan.FromMinutes(1));
        token.InvokeCallbacks();
        _memoryCache.TryGetValue(_cacheKey, out _).Should().BeTrue();
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    public Task InitializeAsync()
    {
        _cacheKey = _fixture.Create<string>();
        _topicKey = _fixture.Create<string>();
        _cacheKeyStrategy = _fixture.Create<ICacheKeyStrategy>();
        _topicKeyStrategy = _fixture.Create<ITopicKeyStrategy>();
        _cacheKeyStrategy.GetCacheKey<string>(_cacheKey).Returns(_cacheKey);
        _topicKeyStrategy.GetTopicKey<string>().Returns(_topicKey);
        _changeTokenFactory = _fixture.Freeze<IChangeTokenFactory>();
        _memoryCache = _fixture.Freeze<IMemoryCache>();
        _telemetryProvider = _fixture.Freeze<ICachingTelemetryProvider>();
        _clock = new SystemClock();

        _options = new()
        {
            DefaultExpiration = TimeSpan.FromMinutes(10),
            EntryFactory = new TestCacheEntryFactory(),
            CacheKeyStrategy = _cacheKeyStrategy,
            TopicKeyStrategy = _topicKeyStrategy,
        };
        _cacheClock = new CacheClock(_clock);
        _fixture.Inject(_cacheClock);
        _topicFactory = _fixture.Freeze<ITopicFactory>();
        _topicProvider = _fixture.Freeze<ITopicProvider>();
        _topic = _fixture.Freeze<ITopic<ICacheEvent>>();
        _topicFactory.Get(Arg.Any<string>()).Returns(_topicProvider);
        _topicProvider.Create(_topicKey).Returns(_topic);
        _fixture.Inject(_memoryCache);
        _fixture.Inject<IMultilayerCacheOptions>(_options);
        _formatter = new CacheClearEventFormatterProxy();
        _fixture.Inject(_formatter);
        _cacheEventFactory = _fixture.Freeze<ICacheEventFactory>();
        _cacheEventFactory.Create(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CacheEventData>(), Arg.Any<string?>())
            .Returns(c =>
                new TestCacheEvent
                {
                    Id = c.ArgAt<string?>(3),
                    Data = c.Arg<CacheEventData>(),
                    Type = c.ArgAt<string>(1),
                }
            );
        return Task.CompletedTask;
    }
}
