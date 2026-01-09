namespace UiPath.Platform.Caching.Tests.Broadcast;

public class RedisPubSubTopicProviderTests(ITestContextAccessor testContextAccessor) : IAsyncLifetime
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubstitute();
    private RedisPubSubTopicOptions _redisPubSubTopicOptions = default!;
    private RedisCacheOptions _redisCacheOptions = default!;
    private CacheOptions _cacheOptions = default!;

    private RedisPubSubTopicProvider? _sut;
    private RedisPubSubTopicProvider Sut => _sut ??= _fixture.Create<RedisPubSubTopicProvider>();

    [Fact]
    public void Works_as_expected()
    {
        Sut.Name.Should().Be("RedisPubSub");
        Sut.Enabled.Should().Be(_redisPubSubTopicOptions.Enabled);
        TopicKey topicKey = _fixture.Create<string>();
        Sut.Create(topicKey).Should().NotBeNull();
    }

    [Fact]
    public async Task Disposing_topic_removes_it_from_provider()
    {
        TopicKey topicKey = _fixture.Create<string>();
        var topic = Sut.Create(topicKey);
        topic.Should().NotBeNull();
        Sut.Keys.Should().NotBeEmpty();
        topic.Dispose();
        await Task.Delay(100, testContextAccessor.Current.CancellationToken);
        Sut.Keys.Should().BeEmpty();
    }

    [Fact]
    public void Remove_topic_from_provider()
    {
        TopicKey topicKey = _fixture.Create<string>();
        var topic = Sut.Create(topicKey);
        topic.Should().NotBeNull();
        Sut.Keys.Should().NotBeEmpty();
        Sut.Remove(topicKey);
        Sut.Keys.Should().BeEmpty();
    }

    [Fact]
    public void Dispose_works_as_expected()
    {
        Action act = () => Sut.Dispose();
        act.Should().NotThrow();
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    public ValueTask InitializeAsync()
    {
        _redisPubSubTopicOptions = _fixture.Create<RedisPubSubTopicOptions>();
        _fixture.Inject(Options.Create(_redisPubSubTopicOptions));
        _redisCacheOptions = _fixture.Create<RedisCacheOptions>();
        _fixture.Inject(Options.Create(_redisCacheOptions));
        _cacheOptions = _fixture.Create<CacheOptions>();
        _fixture.Inject(Options.Create(_cacheOptions));

        return ValueTask.CompletedTask;
    }
}
