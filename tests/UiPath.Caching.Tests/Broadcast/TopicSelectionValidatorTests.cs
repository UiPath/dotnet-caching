using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using UiPath.Caching.Broadcast;
using UiPath.Caching.Config;

namespace UiPath.Caching.Tests.Broadcast;

public class TopicSelectionValidatorTests
{
    [Theory]
    [InlineData("RedisPubSub")]
    [InlineData("Nonsense")]
    [InlineData("")]
    public void With_broadcast_off_no_DefaultTopic_is_an_error(string topic) =>
        Sut(new CacheOptions { BroadcastEnabled = false, DefaultTopic = topic }, []).Validate().Should().BeEmpty();

    [Fact]
    public void A_registered_enabled_provider_is_accepted() =>
        Sut(new CacheOptions { DefaultTopic = "redisstreams" }, [Provider("RedisStreams", true)]).Validate().Should().BeEmpty();

    [Fact]
    public void A_known_provider_that_is_not_registered_names_the_setting_that_enables_it() =>
        Sut(new CacheOptions { DefaultTopic = KnownTopicNames.RedisPubSub }, [Provider("RedisStreams", true)]).Validate()
            .Should().ContainSingle().Which.Should().Contain("RedisPubSub").And.Contain("Broadcast:RedisPubSub:Enabled");

    [Fact]
    public void A_registered_but_disabled_provider_is_an_error_naming_it()
    {
        var failures = Sut(new CacheOptions { DefaultTopic = "Kafka" }, [Provider("Kafka", false)]).Validate().ToList();

        failures.Should().ContainSingle().Which.Should().Contain("'Kafka'").And.Contain("disabled");
    }

    [Fact]
    public void An_unknown_provider_is_an_error_listing_the_registered_ones() =>
        Sut(new CacheOptions { DefaultTopic = "Kafka" }, [Provider("RedisStreams", true)]).Validate()
            .Should().ContainSingle().Which.Should().Contain("'Kafka'").And.Contain("not registered").And.Contain("RedisStreams");

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void An_empty_DefaultTopic_is_an_error(string topic) =>
        Sut(new CacheOptions { DefaultTopic = topic }, [Provider("RedisStreams", true)]).Validate()
            .Should().ContainSingle().Which.Should().Contain("DefaultTopic is empty");

    [Fact]
    public void A_cache_topic_that_selects_a_disabled_provider_is_an_error_when_the_default_is_unusable_too()
    {
        var providers = new[] { Provider("RedisStreams", false) };

        var failures = Sut(new CacheOptions(), providers, inMemoryRedis: new InMemoryRedisCacheOptions { Topic = "RedisPubSub" }).Validate().ToList();
        failures.Should().HaveCount(2);
        failures[0].Should().Contain("DefaultTopic").And.Contain("Broadcast:RedisStreams:Enabled");
        failures[1].Should().Contain("InMemoryRedisCacheOptions.Topic").And.Contain("Broadcast:RedisPubSub:Enabled");
        Sut(new CacheOptions(), providers, inMemory: new InMemoryCacheOptions { BroadcastEnable = true, Topic = "Kafka" }).Validate()
            .Should().HaveCount(2).And.Contain(f => f.Contains("InMemoryCacheOptions.Topic"));
    }

    [Fact]
    public void A_cache_topic_that_selects_a_disabled_provider_falls_back_to_an_enabled_default()
    {
        var providers = new[] { Provider("RedisStreams", true), Provider("RedisPubSub", false) };

        Sut(new CacheOptions(), providers, inMemoryRedis: new InMemoryRedisCacheOptions { Topic = "RedisPubSub" }).Validate().Should().BeEmpty();
        Sut(new CacheOptions(), providers, inMemory: new InMemoryCacheOptions { BroadcastEnable = true, Topic = "Kafka" }).Validate().Should().BeEmpty();
    }

    [Fact]
    public void A_disabled_default_is_not_checked_when_no_cache_resolves_to_it()
    {
        var providers = new[] { Provider("RedisStreams", false), Provider("RedisPubSub", true) };

        Sut(new CacheOptions(), providers, inMemoryRedis: new InMemoryRedisCacheOptions { Topic = "RedisPubSub" }).Validate().Should().BeEmpty();
        Sut(new CacheOptions(), providers, inMemoryRedis: new InMemoryRedisCacheOptions { BroadcastEnable = false }).Validate().Should().BeEmpty();
    }

    [Fact]
    public void A_disabled_default_is_checked_when_any_cache_resolves_to_it()
    {
        var providers = new[] { Provider("RedisStreams", false), Provider("RedisPubSub", true) };
        var pinned = new InMemoryRedisCacheOptions { Topic = "RedisPubSub" };

        Sut(new CacheOptions(), providers, new InMemoryCacheOptions { BroadcastEnable = true }, pinned).Validate()
            .Should().ContainSingle().Which.Should().Contain("DefaultTopic");
        Sut(new CacheOptions(), providers, new InMemoryCacheOptions { BroadcastEnable = true, Topic = "Kafka" }, pinned).Validate()
            .Should().HaveCount(2).And.Contain(f => f.Contains("DefaultTopic")).And.Contain(f => f.Contains("InMemoryCacheOptions.Topic"));
    }

    [Fact]
    public void A_provider_added_to_the_factory_after_construction_is_accepted()
    {
        var factory = new TopicFactory(Options.Create(new CacheOptions { DefaultTopic = "Kafka" }));
        var sut = new TopicSelectionValidator(
            Options.Create(new CacheOptions { DefaultTopic = "Kafka" }), factory, [], Options.Create(new InMemoryCacheOptions()), Options.Create(new InMemoryRedisCacheOptions()));
        sut.Validate().Should().ContainSingle();

        factory.AddProvider(Provider("Kafka", true));

        sut.Validate().Should().BeEmpty();
    }

    [Fact]
    public void A_cache_topic_is_not_checked_when_that_cache_has_broadcast_off_or_names_none()
    {
        var providers = new[] { Provider("RedisStreams", true) };

        Sut(new CacheOptions(), providers, inMemoryRedis: new InMemoryRedisCacheOptions { BroadcastEnable = false, Topic = "RedisPubSub" }).Validate().Should().BeEmpty();
        Sut(new CacheOptions(), providers, inMemory: new InMemoryCacheOptions { Topic = "Kafka" }).Validate().Should().BeEmpty("in-memory broadcast is opt-in and off here");
        Sut(new CacheOptions(), providers, inMemoryRedis: new InMemoryRedisCacheOptions { Topic = null }).Validate().Should().BeEmpty();
        Sut(new CacheOptions { BroadcastEnabled = false }, [], inMemoryRedis: new InMemoryRedisCacheOptions { Topic = "Kafka" }).Validate().Should().BeEmpty();
    }

    [Fact]
    public async Task Starting_a_host_fails_when_the_default_topic_names_a_disabled_provider()
    {
        using var host = Host(streamsEnabled: false, defaultTopic: KnownTopicNames.RedisStreams);

        var start = () => StartValidator(host);

        (await start.Should().ThrowAsync<OptionsValidationException>()).Which.Failures.Should().ContainSingle().Which.Should().Contain("Broadcast:RedisStreams:Enabled");
    }

    [Fact]
    public async Task A_host_starts_when_the_default_topic_names_an_enabled_provider()
    {
        using var host = Host(streamsEnabled: false, defaultTopic: KnownTopicNames.RedisPubSub, pubSubEnabled: true);

        var start = () => StartValidator(host);

        await start.Should().NotThrowAsync();
    }

    private static ServiceProvider Host(bool streamsEnabled, string defaultTopic, bool pubSubEnabled = false) =>
        new ServiceCollection()
            .AddLogging()
            .AddCaching(
                b =>
                {
                    b.AddRedisConnection(o => o.ConnectionString = "127.0.0.1:1,abortConnect=false");
                    b.AddRedisPubSub(o => o.Enabled = pubSubEnabled);
                    b.AddRedisStreams(o => { o.Enabled = streamsEnabled; o.MaintainerEnabled = false; });
                    b.AddBroadcast();
                    b.AddRedis();
                    b.AddInMemoryRedis();
                },
                o => { o.AppShortName = "tsv"; o.DefaultTopic = defaultTopic; })
            .BuildServiceProvider();

    private static Task StartValidator(ServiceProvider host) =>
        host.GetServices<IHostedService>().OfType<TopicSelectionValidator>().Single().StartAsync(TestContext.Current.CancellationToken);

    private static ITopicProvider Provider(string name, bool enabled)
    {
        var provider = Substitute.For<ITopicProvider>();
        provider.Name.Returns(name);
        provider.Enabled.Returns(enabled);
        return provider;
    }

    private static TopicSelectionValidator Sut(CacheOptions cache, ITopicProvider[] providers, InMemoryCacheOptions? inMemory = null, InMemoryRedisCacheOptions? inMemoryRedis = null) =>
        new(Options.Create(cache), new TopicFactory(Options.Create(cache), providers), providers,Options.Create(inMemory ?? new InMemoryCacheOptions()), Options.Create(inMemoryRedis ?? new InMemoryRedisCacheOptions()));
}
