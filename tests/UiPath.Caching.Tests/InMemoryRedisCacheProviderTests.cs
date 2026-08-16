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


    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Broadcast_option(bool enabled)
    {
        _options.BroadcastEnable = enabled;
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
            .Without(x => x.LocalLockEnabled)
            .Without(x => x.DistributedLockEnabled)
            .Create();
        _fixture.Inject(Options.Create(_options));
        return ValueTask.CompletedTask;
    }
}
