namespace UiPath.Platform.Caching.Tests.Broadcast;

public class RedisPubSubTopicProviderTests : IAsyncLifetime
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubsitute();
    private RedisPubSubTopicOptions _redisPubSubTopicOptions = default!;
    private RedisCacheOptions _redisCacheOptions = default!;
    private CacheOptions _cacheOptions = default!;

    private RedisPubSubTopicProvider? _sut = null;
    private RedisPubSubTopicProvider Sut => _sut ??= _fixture.Create<RedisPubSubTopicProvider>();

    [Fact]
    public void Works_as_expected()
    {
        Sut.Name.Should().Be("RedisPubSub");
        Sut.Enabled.Should().Be(_redisPubSubTopicOptions.Enabled);
        TopicKey topicKey = _fixture.Create<string>();
        Sut.CreateTopic(topicKey).Should().NotBeNull();
    }

    [Fact]
    public void Dispose_works_as_expected()
    {
        Action act = () => Sut.Dispose();
        act.Should().NotThrow();
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    public Task InitializeAsync()
    {
        _redisPubSubTopicOptions = _fixture.Create<RedisPubSubTopicOptions>();
        _fixture.Inject(Options.Create(_redisPubSubTopicOptions));
        _redisCacheOptions = _fixture.Create<RedisCacheOptions>();
        _fixture.Inject(Options.Create(_redisCacheOptions));
        _cacheOptions = _fixture.Create<CacheOptions>();
        _fixture.Inject(Options.Create(_cacheOptions));

        return Task.CompletedTask;
    }
}
