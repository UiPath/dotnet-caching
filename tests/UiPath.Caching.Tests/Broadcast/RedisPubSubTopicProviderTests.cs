using Microsoft.Extensions.Configuration;

namespace UiPath.Caching.Tests.Broadcast;

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
        var token = testContextAccessor.Current.CancellationToken;
        TopicKey topicKey = _fixture.Create<string>();
        var topic = Sut.Create(topicKey);
        topic.Should().NotBeNull();
        Sut.Keys.Should().NotBeEmpty();
        topic.Dispose();
        for (var attempt = 0; Sut.Keys.Any() && attempt < 50; attempt++)
        {
            await Task.Delay(20, token);
        }
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
    public void Create_with_no_overrides_returns_topic_with_app_wide_options_instance()
    {
        InjectEmptyConfiguration();
        var topic = (RedisPubSubTopic<ICacheEvent>)Sut.Create("orders");
        topic.GetResolvedOptionsForTests().Should().BeSameAs(_redisPubSubTopicOptions);
    }

    [Fact]
    public void Create_with_config_override_applies_delta_overlay()
    {
        InjectConfiguration(new Dictionary<string, string?>
        {
            ["Broadcast:RedisPubSub:Topics:0:Name"] = "orders",
            ["Broadcast:RedisPubSub:Topics:0:ConsumerCapacity"] = "8192",
        });
        var topic = (RedisPubSubTopic<ICacheEvent>)Sut.Create("orders");
        var resolved = topic.GetResolvedOptionsForTests();
        resolved.ConsumerCapacity.Should().Be(8192);
        resolved.SlowObserverThreshold.Should().Be(_redisPubSubTopicOptions.SlowObserverThreshold);
    }

    [Fact]
    public void Create_with_code_override_wins_over_config()
    {
        InjectConfiguration(
            new Dictionary<string, string?>
            {
                ["Broadcast:RedisPubSub:Topics:0:Name"] = "orders",
                ["Broadcast:RedisPubSub:Topics:0:ConsumerCapacity"] = "8192",
            },
            reg => reg.Configure("orders", o => o.ConsumerCapacity = 4242));

        var topic = (RedisPubSubTopic<ICacheEvent>)Sut.Create("orders");
        topic.GetResolvedOptionsForTests().ConsumerCapacity.Should().Be(4242);
    }

    [Fact]
    public void Create_with_topic_name_containing_colon_resolves_via_array_layout()
    {
        InjectConfiguration(new Dictionary<string, string?>
        {
            ["Broadcast:RedisPubSub:Topics:0:Name"] = "ilist:simplefolder",
            ["Broadcast:RedisPubSub:Topics:0:ConsumerCapacity"] = "777",
        });
        var topic = (RedisPubSubTopic<ICacheEvent>)Sut.Create("ilist:simplefolder");
        topic.GetResolvedOptionsForTests().ConsumerCapacity.Should().Be(777);
    }

    [Fact]
    public void Create_with_TopicKey_Null_skips_per_topic_resolution()
    {
        InjectConfiguration(new Dictionary<string, string?>
        {
            ["Broadcast:RedisPubSub:Topics:0:Name"] = "",
            ["Broadcast:RedisPubSub:Topics:0:ConsumerCapacity"] = "333",
        });
        var topic = (RedisPubSubTopic<ICacheEvent>)Sut.Create(TopicKey.Null);
        topic.GetResolvedOptionsForTests().ConsumerCapacity.Should().Be(_redisPubSubTopicOptions.ConsumerCapacity);
    }

    [Fact]
    public void Create_with_Enabled_false_in_section_does_not_flip_provider_enablement()
    {
        _redisPubSubTopicOptions.Enabled = true;
        InjectConfiguration(new Dictionary<string, string?>
        {
            ["Broadcast:RedisPubSub:Topics:0:Name"] = "orders",
            ["Broadcast:RedisPubSub:Topics:0:Enabled"] = "false",
        });
        Sut.Create("orders");
        Sut.Enabled.Should().BeTrue();
    }

    [Fact]
    public void Create_with_different_TopicKeys_returns_distinct_options_instances()
    {
        InjectConfiguration(new Dictionary<string, string?>
        {
            ["Broadcast:RedisPubSub:Topics:0:Name"] = "orders",
            ["Broadcast:RedisPubSub:Topics:0:ConsumerCapacity"] = "111",
            ["Broadcast:RedisPubSub:Topics:1:Name"] = "payments",
            ["Broadcast:RedisPubSub:Topics:1:ConsumerCapacity"] = "222",
        });

        var ordersResolved = ((RedisPubSubTopic<ICacheEvent>)Sut.Create("orders")).GetResolvedOptionsForTests();
        var paymentsResolved = ((RedisPubSubTopic<ICacheEvent>)Sut.Create("payments")).GetResolvedOptionsForTests();

        ordersResolved.Should().NotBeSameAs(paymentsResolved);
        ordersResolved.ConsumerCapacity.Should().Be(111);
        paymentsResolved.ConsumerCapacity.Should().Be(222);
    }

    [Fact]
    public void Create_with_code_only_override_applies_on_top_of_app_wide()
    {
        InjectEmptyConfiguration(reg => reg.Configure("orders", o => o.ConsumerCapacity = 65_432));

        var topic = (RedisPubSubTopic<ICacheEvent>)Sut.Create("orders");
        var resolved = topic.GetResolvedOptionsForTests();

        resolved.ConsumerCapacity.Should().Be(65_432);
        resolved.SlowObserverThreshold.Should().Be(_redisPubSubTopicOptions.SlowObserverThreshold, "unspecified fields inherit");
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

        InjectRegistry(new ConfigurationBuilder().Build());

        return ValueTask.CompletedTask;
    }

    private void InjectEmptyConfiguration(Action<PerTopicOptionsRegistry<RedisPubSubTopicOptions>>? configureRegistry = null) =>
        InjectConfiguration(new Dictionary<string, string?>(), configureRegistry);

    private void InjectConfiguration(IDictionary<string, string?> values, Action<PerTopicOptionsRegistry<RedisPubSubTopicOptions>>? configureRegistry = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        InjectRegistry(configuration, configureRegistry);
    }

    private void InjectRegistry(IConfiguration configuration, Action<PerTopicOptionsRegistry<RedisPubSubTopicOptions>>? configureRegistry = null)
    {
        var registry = new PerTopicOptionsRegistry<RedisPubSubTopicOptions>(configuration.GetSection("Broadcast:RedisPubSub:Topics"));
        configureRegistry?.Invoke(registry);
        _fixture.Inject(registry);
        _sut = null;
    }
}
