using Microsoft.Extensions.Configuration;

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

    [Fact]
    public void Create_with_no_overrides_returns_topic_with_app_wide_options_instance()
    {
        InjectEmptyConfiguration();
        var topic = (RedisStreamsTopic<ICacheEvent>)Sut.Create("orders");
        topic.GetResolvedOptionsForTests().Should().BeSameAs(_redisStreamsTopicOptions);
    }

    [Fact]
    public void Create_with_config_override_applies_delta_overlay()
    {
        InjectConfiguration(new Dictionary<string, string?>
        {
            ["Broadcast:RedisStreams:Topics:0:Name"] = "orders",
            ["Broadcast:RedisStreams:Topics:0:MaxLength"] = "131072",
        });

        var topic = (RedisStreamsTopic<ICacheEvent>)Sut.Create("orders");
        var resolved = topic.GetResolvedOptionsForTests();

        resolved.MaxLength.Should().Be(131072);
        resolved.PollInterval.Should().Be(_redisStreamsTopicOptions.PollInterval, "unspecified fields inherit");
    }

    [Fact]
    public void Create_with_code_override_wins_over_config()
    {
        InjectConfiguration(
            new Dictionary<string, string?>
            {
                ["Broadcast:RedisStreams:Topics:0:Name"] = "orders",
                ["Broadcast:RedisStreams:Topics:0:MaxLength"] = "131072",
            },
            reg => reg.Configure("orders", o => o.MaxLength = 999_999));

        var topic = (RedisStreamsTopic<ICacheEvent>)Sut.Create("orders");
        topic.GetResolvedOptionsForTests().MaxLength.Should().Be(999_999);
    }

    [Fact]
    public void Create_with_topic_name_containing_colon_resolves_via_array_layout()
    {
        InjectConfiguration(new Dictionary<string, string?>
        {
            ["Broadcast:RedisStreams:Topics:0:Name"] = "ilist:simplefolder",
            ["Broadcast:RedisStreams:Topics:0:MaxLength"] = "555",
        });

        var topic = (RedisStreamsTopic<ICacheEvent>)Sut.Create("ilist:simplefolder");
        var resolved = topic.GetResolvedOptionsForTests();

        resolved.MaxLength.Should().Be(555);
    }

    [Fact]
    public void Create_with_blank_Name_entry_is_skipped()
    {
        InjectConfiguration(new Dictionary<string, string?>
        {
            ["Broadcast:RedisStreams:Topics:0:Name"] = "",
            ["Broadcast:RedisStreams:Topics:0:MaxLength"] = "1",
            ["Broadcast:RedisStreams:Topics:1:Name"] = "orders",
            ["Broadcast:RedisStreams:Topics:1:MaxLength"] = "999",
        });

        var topic = (RedisStreamsTopic<ICacheEvent>)Sut.Create("orders");
        topic.GetResolvedOptionsForTests().MaxLength.Should().Be(999);
    }

    [Fact]
    public void Create_with_duplicate_Name_entries_uses_last()
    {
        InjectConfiguration(new Dictionary<string, string?>
        {
            ["Broadcast:RedisStreams:Topics:0:Name"] = "orders",
            ["Broadcast:RedisStreams:Topics:0:MaxLength"] = "111",
            ["Broadcast:RedisStreams:Topics:1:Name"] = "orders",
            ["Broadcast:RedisStreams:Topics:1:MaxLength"] = "222",
        });

        var topic = (RedisStreamsTopic<ICacheEvent>)Sut.Create("orders");
        topic.GetResolvedOptionsForTests().MaxLength.Should().Be(222);
    }

    [Fact]
    public void Create_with_TopicKey_Null_skips_per_topic_resolution()
    {
        InjectConfiguration(new Dictionary<string, string?>
        {
            ["Broadcast:RedisStreams:Topics:0:Name"] = "",
            ["Broadcast:RedisStreams:Topics:0:MaxLength"] = "777",
        });

        var topic = (RedisStreamsTopic<ICacheEvent>)Sut.Create(TopicKey.Null);
        topic.GetResolvedOptionsForTests().MaxLength.Should().Be(_redisStreamsTopicOptions.MaxLength);
    }

    [Fact]
    public void Create_with_Enabled_false_in_section_does_not_flip_provider_enablement()
    {
        InjectConfiguration(new Dictionary<string, string?>
        {
            ["Broadcast:RedisStreams:Topics:0:Name"] = "orders",
            ["Broadcast:RedisStreams:Topics:0:Enabled"] = "false",
        });
        Sut.Create("orders");
        Sut.Enabled.Should().BeTrue();
    }

    [Fact]
    public void Create_with_different_TopicKeys_returns_distinct_options_instances()
    {
        InjectConfiguration(new Dictionary<string, string?>
        {
            ["Broadcast:RedisStreams:Topics:0:Name"] = "orders",
            ["Broadcast:RedisStreams:Topics:0:MaxLength"] = "111",
            ["Broadcast:RedisStreams:Topics:1:Name"] = "payments",
            ["Broadcast:RedisStreams:Topics:1:MaxLength"] = "222",
        });

        var ordersResolved = ((RedisStreamsTopic<ICacheEvent>)Sut.Create("orders")).GetResolvedOptionsForTests();
        var paymentsResolved = ((RedisStreamsTopic<ICacheEvent>)Sut.Create("payments")).GetResolvedOptionsForTests();

        ordersResolved.Should().NotBeSameAs(paymentsResolved);
        ordersResolved.MaxLength.Should().Be(111);
        paymentsResolved.MaxLength.Should().Be(222);
    }

    [Fact]
    public void Create_with_code_only_override_applies_on_top_of_app_wide()
    {
        InjectEmptyConfiguration(reg => reg.Configure("orders", o => o.MaxLength = 654_321));

        var topic = (RedisStreamsTopic<ICacheEvent>)Sut.Create("orders");
        var resolved = topic.GetResolvedOptionsForTests();

        resolved.MaxLength.Should().Be(654_321);
        resolved.PollInterval.Should().Be(_redisStreamsTopicOptions.PollInterval, "unspecified fields inherit");
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

        InjectRegistry(new ConfigurationBuilder().Build());

        _redisConnector = _fixture.Freeze<IRedisConnector>();
        _redisConnector.IsConnected.Returns(ctx => _isConnected);

        _fixture.Inject<IConnectionState>(_redisConnector);

        return ValueTask.CompletedTask;
    }

    private void InjectEmptyConfiguration(Action<PerTopicOptionsRegistry<RedisStreamsTopicOptions>>? configureRegistry = null) =>
        InjectConfiguration(new Dictionary<string, string?>(), configureRegistry);

    private void InjectConfiguration(IDictionary<string, string?> values, Action<PerTopicOptionsRegistry<RedisStreamsTopicOptions>>? configureRegistry = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        InjectRegistry(configuration, configureRegistry);
    }

    private void InjectRegistry(IConfiguration configuration, Action<PerTopicOptionsRegistry<RedisStreamsTopicOptions>>? configureRegistry = null)
    {
        var registry = new PerTopicOptionsRegistry<RedisStreamsTopicOptions>(configuration.GetSection("Broadcast:RedisStreams:Topics"));
        configureRegistry?.Invoke(registry);
        _fixture.Inject(registry);
        _sut = null; // force fixture to rebuild Sut with the new injected registry
    }
}
