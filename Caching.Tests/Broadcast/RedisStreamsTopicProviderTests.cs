namespace UiPath.Platform.Caching.Tests.Broadcast;
public class RedisStreamsTopicProviderTests(ITestContextAccessor testContextAccessor) : IAsyncLifetime
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubstitute();
    private  RedisStreamsTopicOptions _redisStreamsTopicOptions = default!;
    private  RedisCacheOptions _redisCacheOptions = default!;
    private  CacheOptions _cacheOptions = default!;

    private RedisStreamsTopicProvider? _sut = null;
    private IRedisConnector _redisConnector = default!;
    private bool _isConnected = true;

    private RedisStreamsTopicProvider Sut => _sut ??= _fixture.Create<RedisStreamsTopicProvider>();

    [Fact]
    public void Works_as_expected()
    {
        Sut.Name.Should().Be("RedisStreams");
        Sut.Enabled.Should().Be(_redisStreamsTopicOptions.Enabled);
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

    [Fact]
    public void ConnectionState_not_connected_monitored_works_as_expected()
    {
        _isConnected = false;
        _redisStreamsTopicOptions.ConnectionMonitorEnabled = false;
        _redisConnector.OnConnectionFailed += Raise.Event();
        Sut.IsConnected.Should().Be(true);
    }

    [Fact]
    public void ConnectionState_connected_monitored_works_as_expected()
    {
        _isConnected = false;
        _redisStreamsTopicOptions.ConnectionMonitorEnabled = false;
        Sut.IsConnected.Should().Be(true);
    }


    [Fact]
    public void OnConnectionFailed()
    {
        bool wasCalled = false;

        void Sut_OnEvent(object? sender, EventArgs e) => wasCalled = true;

        Sut.OnConnectionFailed += Sut_OnEvent;
        _redisConnector.OnConnectionFailed += Raise.Event();
        wasCalled.Should().Be(true);
        Sut.OnConnectionFailed -= Sut_OnEvent;
        wasCalled = false;
        _redisConnector.OnConnectionFailed += Raise.Event();
        wasCalled.Should().Be(false);
    }

    [Fact]
    public void OnConnectionRestored()
    {
        bool wasCalled = false;

        void Sut_OnEvent(object? sender, EventArgs e) => wasCalled = true;

        Sut.OnConnectionRestored += Sut_OnEvent;
        _redisConnector.OnConnectionRestored += Raise.Event();
        wasCalled.Should().Be(true);
        Sut.OnConnectionRestored -= Sut_OnEvent;
        wasCalled = false;
        _redisConnector.OnConnectionRestored += Raise.Event();
        wasCalled.Should().Be(false);
    }

    [Fact]
    public void OnReconnected()
    {
        bool wasCalled = false;

        void Sut_OnEvent(object? sender, EventArgs e) => wasCalled = true;

        Sut.OnReconnected += Sut_OnEvent;
        _redisConnector.OnReconnected += Raise.Event();
        wasCalled.Should().Be(true);
        Sut.OnReconnected -= Sut_OnEvent;
        wasCalled = false;
        _redisConnector.OnReconnected += Raise.Event();
        wasCalled.Should().Be(false);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void ConnectionState_connected_should_be_monitored(bool connected, bool expected)
    {
        _isConnected = connected;
        _redisConnector.OnConnectionFailed += Raise.Event();
        Sut.IsConnected.Should().Be(expected);
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    public ValueTask InitializeAsync()
    {
        _redisStreamsTopicOptions = _fixture.Create<RedisStreamsTopicOptions>();
        _redisStreamsTopicOptions.Enabled = true;
        _redisStreamsTopicOptions.ConnectionMonitorEnabled = true;
        _fixture.Inject(Options.Create(_redisStreamsTopicOptions));
        _redisCacheOptions = _fixture.Create<RedisCacheOptions>();
        _redisCacheOptions.ConnectionMonitorEnabled = true;
        _fixture.Inject(Options.Create(_redisCacheOptions));
        _cacheOptions = _fixture.Create<CacheOptions>();
        _fixture.Inject(Options.Create(_cacheOptions));

        _redisConnector = _fixture.Freeze<IRedisConnector>();
        _redisConnector.IsConnected.Returns(ctx => _isConnected);

        _fixture.Inject<IConnectionState>(_redisConnector);

        return ValueTask.CompletedTask;
    }
}
