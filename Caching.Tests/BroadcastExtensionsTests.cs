using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using UiPath.Platform.Caching.Broadcast;
using UiPath.Platform.Caching.Broadcast.Redis;
using UiPath.Platform.Caching.Config;

namespace UiPath.Platform.Caching.Tests;

public class BroadcastExtensionsTests
{
    [Fact]
    public void AddRedisStreams_registry_resolves_topic_overrides_under_scoped_Caching_root()
    {
        var rootConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Caching:Broadcast:RedisStreams:Topics:0:Name"] = "orders",
                ["Caching:Broadcast:RedisStreams:Topics:0:MaxLength"] = "131072",
            })
            .Build();
        var scopedSection = rootConfig.GetSection("Caching");

        var services = new ServiceCollection();
        var builder = new CachingBuilder(services, scopedSection);
        builder.AddRedisStreams(o => o.Enabled = true);

        var registry = services.BuildServiceProvider()
            .GetRequiredService<PerTopicOptionsRegistry<RedisStreamsTopicOptions>>();
        var resolved = registry.Resolve(new TopicKey("orders"),
            () => new RedisStreamsTopicOptions { MaxLength = 32768 },
            NullLogger.Instance);

        resolved.Should().NotBeNull();
        resolved!.MaxLength.Should().Be(131072);
    }

    [Fact]
    public void AddRedisPubSub_registry_resolves_topic_overrides_under_scoped_Caching_root()
    {
        var rootConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Caching:Broadcast:RedisPubSub:Topics:0:Name"] = "orders",
                ["Caching:Broadcast:RedisPubSub:Topics:0:ConsumerCapacity"] = "8192",
            })
            .Build();
        var scopedSection = rootConfig.GetSection("Caching");

        var services = new ServiceCollection();
        var builder = new CachingBuilder(services, scopedSection);
        builder.AddRedisPubSub(o => o.Enabled = true);

        var registry = services.BuildServiceProvider()
            .GetRequiredService<PerTopicOptionsRegistry<RedisPubSubTopicOptions>>();
        var resolved = registry.Resolve(new TopicKey("orders"),
            () => new RedisPubSubTopicOptions { ConsumerCapacity = 2048 },
            NullLogger.Instance);

        resolved.Should().NotBeNull();
        resolved!.ConsumerCapacity.Should().Be(8192);
    }

    [Fact]
    public void ConfigureRedisStreamsTopic_throws_on_blank_topic_name()
    {
        var services = new ServiceCollection();
        var builder = new CachingBuilder(services);
        builder.AddRedisStreams(_ => { });

        var act = () => builder.ConfigureRedisStreamsTopic("   ", _ => { });

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ConfigureRedisPubSubTopic_throws_on_blank_topic_name()
    {
        var services = new ServiceCollection();
        var builder = new CachingBuilder(services);
        builder.AddRedisPubSub(_ => { });

        var act = () => builder.ConfigureRedisPubSubTopic("   ", _ => { });

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ConfigureRedisStreamsTopic_called_twice_stacks_both_actions()
    {
        var services = new ServiceCollection();
        var builder = new CachingBuilder(services);

        builder.AddRedisStreams(_ => { });
        builder.ConfigureRedisStreamsTopic("orders", o => o.MaxLength = 100);
        builder.ConfigureRedisStreamsTopic("orders", o => o.MaxLength = 200);

        var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<PerTopicOptionsRegistry<RedisStreamsTopicOptions>>();

        registry.GetActions("orders").Should().HaveCount(2);
    }

    [Fact]
    public void ConfigureRedisPubSubTopic_called_twice_stacks_both_actions()
    {
        var services = new ServiceCollection();
        var builder = new CachingBuilder(services);

        builder.AddRedisPubSub(_ => { });
        builder.ConfigureRedisPubSubTopic("orders", o => o.ConsumerCapacity = 100);
        builder.ConfigureRedisPubSubTopic("orders", o => o.ConsumerCapacity = 200);

        var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<PerTopicOptionsRegistry<RedisPubSubTopicOptions>>();

        registry.GetActions("orders").Should().HaveCount(2);
    }

    [Fact]
    public void AddRedisStreams_registers_PerTopicOptionsRegistry_as_singleton()
    {
        var services = new ServiceCollection();
        var builder = new CachingBuilder(services, new ConfigurationBuilder().Build());

        builder.AddRedisStreams(o => o.Enabled = true);

        var provider = services.BuildServiceProvider();
        var a = provider.GetRequiredService<PerTopicOptionsRegistry<RedisStreamsTopicOptions>>();
        var b = provider.GetRequiredService<PerTopicOptionsRegistry<RedisStreamsTopicOptions>>();
        a.Should().BeSameAs(b);
        a.TopicsSection.Path.Should().Be("Broadcast:RedisStreams:Topics");
    }

    [Fact]
    public void AddRedisPubSub_registers_PerTopicOptionsRegistry_as_singleton()
    {
        var services = new ServiceCollection();
        var builder = new CachingBuilder(services, new ConfigurationBuilder().Build());

        builder.AddRedisPubSub(o => o.Enabled = true);

        var provider = services.BuildServiceProvider();
        var a = provider.GetRequiredService<PerTopicOptionsRegistry<RedisPubSubTopicOptions>>();
        var b = provider.GetRequiredService<PerTopicOptionsRegistry<RedisPubSubTopicOptions>>();
        a.Should().BeSameAs(b);
        a.TopicsSection.Path.Should().Be("Broadcast:RedisPubSub:Topics");
    }

    [Fact]
    public void ConfigureRedisStreamsTopic_throws_when_called_before_AddRedisStreams()
    {
        var services = new ServiceCollection();
        var builder = new CachingBuilder(services);

        var act = () => builder.ConfigureRedisStreamsTopic("orders", o => o.MaxLength = 99);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*AddRedisStreams*before*ConfigureRedisStreamsTopic*");
    }

    [Fact]
    public void ConfigureRedisPubSubTopic_throws_when_called_before_AddRedisPubSub()
    {
        var services = new ServiceCollection();
        var builder = new CachingBuilder(services);

        var act = () => builder.ConfigureRedisPubSubTopic("orders", o => o.ConsumerCapacity = 99);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*AddRedisPubSub*before*ConfigureRedisPubSubTopic*");
    }

    [Fact]
    public void AddRedisStreams_called_twice_with_different_section_names_throws()
    {
        var services = new ServiceCollection();
        var builder = new CachingBuilder(services, new ConfigurationBuilder().Build());

        builder.AddRedisStreams(sectionName: "Broadcast:RedisStreams");
        var act = () => builder.AddRedisStreams(sectionName: "Other:Streams");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Broadcast:RedisStreams:Topics*Other:Streams:Topics*");
    }

    [Fact]
    public void AddRedisPubSub_called_twice_with_different_section_names_throws()
    {
        var services = new ServiceCollection();
        var builder = new CachingBuilder(services, new ConfigurationBuilder().Build());

        builder.AddRedisPubSub(sectionName: "Broadcast:RedisPubSub");
        var act = () => builder.AddRedisPubSub(sectionName: "Other:PubSub");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Broadcast:RedisPubSub:Topics*Other:PubSub:Topics*");
    }

    [Fact]
    public void AddRedisStreams_with_action_and_custom_section_binds_registry_to_that_section()
    {
        var services = new ServiceCollection();
        var builder = new CachingBuilder(services, new ConfigurationBuilder().Build());

        builder.AddRedisStreams(o => o.Enabled = true, sectionName: "CustomBroadcast:RedisStreams");

        var registry = services.BuildServiceProvider()
            .GetRequiredService<PerTopicOptionsRegistry<RedisStreamsTopicOptions>>();
        registry.TopicsSection.Path.Should().Be("CustomBroadcast:RedisStreams:Topics");
    }

    [Fact]
    public void AddRedisPubSub_with_action_and_custom_section_binds_registry_to_that_section()
    {
        var services = new ServiceCollection();
        var builder = new CachingBuilder(services, new ConfigurationBuilder().Build());

        builder.AddRedisPubSub(o => o.Enabled = true, sectionName: "CustomBroadcast:RedisPubSub");

        var registry = services.BuildServiceProvider()
            .GetRequiredService<PerTopicOptionsRegistry<RedisPubSubTopicOptions>>();
        registry.TopicsSection.Path.Should().Be("CustomBroadcast:RedisPubSub:Topics");
    }

    [Fact]
    public void ConfigureRedisStreamsTopic_uses_section_chosen_by_AddRedisStreams()
    {
        var rootConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CustomBroadcast:RedisStreams:Enabled"] = "true",
            })
            .Build();

        var services = new ServiceCollection();
        var builder = new CachingBuilder(services, rootConfig);
        builder.AddRedisStreams(sectionName: "CustomBroadcast:RedisStreams");
        builder.ConfigureRedisStreamsTopic("orders", _ => { });

        var registry = services.BuildServiceProvider()
            .GetRequiredService<PerTopicOptionsRegistry<RedisStreamsTopicOptions>>();
        registry.TopicsSection.Path.Should().Be("CustomBroadcast:RedisStreams:Topics");
        registry.GetActions("orders").Should().ContainSingle();
    }
}
