namespace UiPath.Platform.Caching.Tests.Broadcast;
public class RedisStreamsTopicProviderTests : IAsyncLifetime
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubsitute();
    private  RedisStreamsTopicOptions _redisStreamsTopicOptions = default!;
    private  RedisCacheOptions _redisCacheOptions = default!;
    private  CacheOptions _cacheOptions = default!;

    private RedisStreamsTopicProvider? _sut = null;
    private RedisStreamsTopicProvider Sut => _sut ??= _fixture.Create<RedisStreamsTopicProvider>();

    [Fact]
    public void Works_as_expected()
    {
        Sut.Name.Should().Be("RedisStreams");
        Sut.Enabled.Should().Be(_redisStreamsTopicOptions.Enabled);
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
        _redisStreamsTopicOptions = _fixture.Create<RedisStreamsTopicOptions>();
        _fixture.Inject(Options.Create(_redisStreamsTopicOptions));
        _redisCacheOptions = _fixture.Create<RedisCacheOptions>();
        _fixture.Inject(Options.Create(_redisCacheOptions));
        _cacheOptions = _fixture.Create<CacheOptions>();
        _fixture.Inject(Options.Create(_cacheOptions));

        return Task.CompletedTask;
    }
}
