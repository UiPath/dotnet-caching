using Microsoft.Extensions.Primitives;
using UiPath.Platform.Caching;

namespace UiPath.Platform.Caching.Tests;

public class InMemoryCacheProviderTests : IAsyncLifetime
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubsitute();
    private InMemoryCacheOptions _options = default!;

    private InMemoryCacheProvider? _sut = null;


    private InMemoryCacheProvider Sut => _sut ??= _fixture.Create<InMemoryCacheProvider>();

    [Fact]
    public void Works_as_expected()
    {
        Sut.CreateCache().Should().BeOfType<MultilayerCache>();
        Sut.CreateHashCache().Should().BeOfType<MultilayerHashCache>();
        Sut.Name.Should().Be("InMemory");
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
        topicFactory.Get(Arg.Any<string?>(), Arg.Any<Type>())
            .Returns(topicProvider);
        var topic = _fixture.Create<ITopic<ICacheEvent>>();
        topic.PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>())
            .Returns(_ => true);
        TopicKey topicKey = _fixture.Create<string>();
        var topicCallsCount = 0;
        topicProvider.Create(topicKey)
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

        var memCache = Sut.CreateCache();
        var memHashCache = Sut.CreateHashCache();
        memCache.Should().BeOfType<MultilayerCache>();
        memHashCache.Should().BeOfType<MultilayerHashCache>();
        await memCache.SetAsync(_fixture.Create<string>(), _fixture.Create<string>(), CancellationToken.None);
        var values = _fixture.Create<IDictionary<string, string?>>();
        await memHashCache.SetAsync(_fixture.Create<string>(), values, CancellationToken.None);
        if (enabled)
        {
            topicCallsCount.Should().BeGreaterThan(0);
            tokenCallsCount.Should().BeGreaterThan(0);
            eventCallsCount.Should().BeGreaterThan(0);
        }
        else
        {
            topicCallsCount.Should().Be(0);
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

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    public Task InitializeAsync()
    {
        _options = _fixture.Create<InMemoryCacheOptions>();
        _fixture.Inject(Options.Create(_options));

        
        return Task.CompletedTask;
    }
}
