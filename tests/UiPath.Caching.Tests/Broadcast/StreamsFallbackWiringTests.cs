using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute.ExceptionExtensions;
using StackExchange.Redis;
using UiPath.Caching.Config;
using UiPath.Caching.Negotiation;
using UiPath.Caching.Policies;
using UiPath.Caching.Serialization;
using UiPath.Caching.Telemetry;
using UiPath.Caching.Tests.Fakes;

namespace UiPath.Caching.Tests.Broadcast;

public class StreamsFallbackWiringTests(ITestContextAccessor testContextAccessor)
{
    private CancellationToken Token => testContextAccessor.Current.CancellationToken;

    [Fact]
    public void The_public_constructor_makes_the_raw_Streams_topic()
    {
        using var provider = Provider(StreamsFakes.Connector(out _, out _), new RedisStreamsTopicOptions(), fallback: null);

        provider.Create("orders").Should().BeOfType<RedisStreamsTopic<ICacheEvent>>();
    }

    [Fact]
    public void A_fallback_makes_the_topic_that_can_switch()
    {
        var connector = StreamsFakes.Connector(out _, out _);
        using var pubSub = PubSub(connector);
        using var provider = Provider(connector, new RedisStreamsTopicOptions(), pubSub);

        using var topic = provider.Create("orders");

        topic.Should().BeOfType<StreamsFallbackTopic>();
    }

    [Fact]
    public void The_Fail_action_keeps_the_raw_topic_even_with_a_fallback()
    {
        var connector = StreamsFakes.Connector(out _, out _);
        using var pubSub = PubSub(connector);
        using var provider = Provider(connector, new RedisStreamsTopicOptions { StreamsUnsupported = StreamsUnsupportedAction.Fail }, pubSub);

        provider.Create("orders").Should().BeOfType<RedisStreamsTopic<ICacheEvent>>();
    }

    [Fact]
    public void The_Disable_action_makes_the_topic_that_can_switch_with_no_fallback()
    {
        using var provider = Provider(StreamsFakes.Connector(out _, out _), new RedisStreamsTopicOptions { StreamsUnsupported = StreamsUnsupportedAction.Disable }, fallback: null);

        provider.Create("orders").Should().BeOfType<StreamsFallbackTopic>();
    }

    [Fact]
    public async Task Stream_keys_leave_the_list_when_the_topic_switches_to_Pub_Sub()
    {
        var connector = StreamsFakes.Connector(out _, out _);
        using var pubSub = PubSub(connector);
        using var provider = Provider(connector, new RedisStreamsTopicOptions(), pubSub);
        provider.Create("orders");
        provider.Create("invoices");
        var topic = (StreamsFallbackTopic)provider.Create("orders");
        await topic.PublishAsync(new TestCacheEvent { Id = "1", Source = new Uri("urn:test") }, Token);
        await WaitForAsync(() => provider.StreamKeys().Count == 2);

        BroadcastCapability.For(connector).Refused(StreamsFakes.UnknownCommand());
        await WaitForAsync(() => provider.StreamKeys().Count == 0);

        // The key leaves the provider's map after the old Streams topic is disposed, a moment after the topic stops reporting Streams.
        await WaitForAsync(() => provider.StreamKeyCount == 0);
        provider.StreamKeyCount.Should().Be(0);
    }

    [Fact]
    public async Task Two_providers_on_one_connector_share_what_one_of_them_learned()
    {
        var connector = StreamsFakes.Connector(out _, out _);
        using var pubSub = PubSub(connector);
        using var first = Provider(connector, new RedisStreamsTopicOptions(), pubSub);
        using var second = Provider(connector, new RedisStreamsTopicOptions(), pubSub);
        var topic = (StreamsFallbackTopic)second.Create("orders");
        await topic.PublishAsync(new TestCacheEvent { Id = "1", Source = new Uri("urn:test") }, Token);
        topic.OnStreams.Should().BeTrue();

        BroadcastCapability.For(connector).Refused(StreamsFakes.UnknownCommand());

        await WaitForAsync(() => !topic.OnStreams);
        ((StreamsFallbackTopic)first.Create("orders")).OnStreams.Should().BeFalse("it is made after the switch, so it starts on the fallback");
    }

    [Fact]
    public void The_standby_Pub_Sub_provider_is_registered_but_not_selected_and_is_the_Streams_fallback()
    {
        using var services = Build(streams => { });

        var streams = services.GetRequiredService<RedisStreamsTopicProvider>();
        var standby = services.GetRequiredService<RedisPubSubTopicProvider>();

        streams.PubSubFallback.Should().BeSameAs(standby);
        standby.Enabled.Should().BeFalse();
        services.GetServices<ITopicProvider>().Should().ContainSingle().Which.Should().BeSameAs(streams);
        var factory = new TopicFactory(Options.Create(new CacheOptions { AppShortName = "app" }), [streams, standby]);
        factory.ProviderNames.Should().Equal(KnownTopicNames.RedisStreams);
        factory.Get(KnownTopicNames.RedisPubSub).Should().NotBeSameAs(standby);
    }

    [Fact]
    public void An_enabled_Pub_Sub_provider_is_the_one_instance_behind_both_roles()
    {
        using var services = Build(streams => { }, pubSub => pubSub.Enabled = true);

        var providers = services.GetServices<ITopicProvider>().ToList();
        var pubSub = providers.OfType<RedisPubSubTopicProvider>().Should().ContainSingle().Subject;

        pubSub.Should().BeSameAs(services.GetRequiredService<RedisPubSubTopicProvider>());
        services.GetRequiredService<RedisStreamsTopicProvider>().PubSubFallback.Should().BeSameAs(pubSub);
        providers.OfType<RedisStreamsTopicProvider>().Should().ContainSingle();
    }

    [Fact]
    public void Streams_added_alone_leaves_a_standby_that_binds_the_default_section_and_is_never_selected()
    {
        using var services = BuildOrdered(
            new Dictionary<string, string?> { ["Broadcast:RedisPubSub:Enabled"] = "true" },
            builder => builder.AddRedisStreams(streams => { }));

        var standby = services.GetRequiredService<RedisPubSubTopicProvider>();

        standby.Enabled.Should().BeTrue("it was bound from the default section");
        SelectableNames(services).Should().Equal([KnownTopicNames.RedisStreams], "Pub/Sub was never added, so the factory is not offered it");
    }

    [Fact]
    public void Streams_then_Pub_Sub_with_a_custom_section_does_not_throw_and_the_provider_uses_that_section()
    {
        using var services = BuildOrdered(
            new Dictionary<string, string?> { ["Custom:Enabled"] = "true", ["Custom:ConsumerCapacity"] = "33", ["Broadcast:RedisPubSub:Enabled"] = "false" },
            builder => builder.AddRedisStreams(streams => { }).AddRedisPubSub("Custom"));

        var provider = services.GetRequiredService<RedisPubSubTopicProvider>();

        provider.Enabled.Should().BeTrue("Custom:Enabled, not the default section's, applies");
        services.GetRequiredService<IOptions<RedisPubSubTopicOptions>>().Value.ConsumerCapacity.Should().Be(33);
        services.GetRequiredService<RedisStreamsTopicProvider>().PubSubFallback.Should().BeSameAs(provider);
    }

    [Fact]
    public void Streams_then_Pub_Sub_with_options_applies_them_and_the_factory_selects_Pub_Sub()
    {
        using var services = BuildOrdered(
            [],
            builder => builder.AddRedisStreams(streams => { }).AddRedisPubSub(pubSub =>
            {
                pubSub.Enabled = true;
                pubSub.ConsumerCapacity = 77;
            }));

        var provider = services.GetRequiredService<RedisPubSubTopicProvider>();

        provider.Enabled.Should().BeTrue();
        services.GetRequiredService<IOptions<RedisPubSubTopicOptions>>().Value.ConsumerCapacity.Should().Be(77);
        SelectableNames(services).Should().BeEquivalentTo(KnownTopicNames.RedisStreams, KnownTopicNames.RedisPubSub);
        services.GetServices<ITopicProvider>().OfType<RedisPubSubTopicProvider>().Should().ContainSingle().Which.Should().BeSameAs(provider);
        services.GetRequiredService<RedisStreamsTopicProvider>().PubSubFallback.Should().BeSameAs(provider);
    }

    [Fact]
    public void Pub_Sub_then_Streams_has_one_provider_behind_both_roles()
    {
        using var services = BuildOrdered(
            [],
            builder => builder.AddRedisPubSub(pubSub => pubSub.Enabled = true).AddRedisStreams(streams => { }));

        var provider = services.GetRequiredService<RedisPubSubTopicProvider>();

        services.GetServices<ITopicProvider>().OfType<RedisPubSubTopicProvider>().Should().ContainSingle().Which.Should().BeSameAs(provider);
        services.GetRequiredService<RedisStreamsTopicProvider>().PubSubFallback.Should().BeSameAs(provider);
        SelectableNames(services).Should().BeEquivalentTo(KnownTopicNames.RedisStreams, KnownTopicNames.RedisPubSub);
    }

    [Theory]
    [InlineData(StreamsUnsupportedAction.Disable)]
    [InlineData(StreamsUnsupportedAction.Fail)]
    public void Only_the_PubSub_action_registers_the_standby_provider(StreamsUnsupportedAction action)
    {
        using var services = Build(streams => streams.StreamsUnsupported = action);

        services.GetRequiredService<RedisStreamsTopicProvider>().PubSubFallback.Should().BeNull();
        services.GetService<RedisPubSubTopicProvider>().Should().BeNull();
    }

    [Theory]
    [InlineData(StreamsUnsupportedAction.PubSub, false)]
    [InlineData(StreamsUnsupportedAction.Disable, false)]
    [InlineData(StreamsUnsupportedAction.Fail, true)]
    public void The_gate_is_registered_for_Fail_only(StreamsUnsupportedAction action, bool registered)
    {
        using var services = Build(streams =>
        {
            streams.StreamsUnsupported = action;
            streams.MaintainerEnabled = false;
        });

        services.GetServices<IHostedService>().OfType<StreamsSupportGate>().Any().Should().Be(registered);
    }

    [Fact]
    public void The_Streams_provider_stays_registered_once_when_added_twice()
    {
        var services = new ServiceCollection();
        var builder = new CachingBuilder(services, new ConfigurationBuilder().Build());

        builder.AddRedisStreams(streams => { }).AddRedisStreams(streams => { });

        services.Count(descriptor => descriptor.ServiceType == typeof(ITopicProvider)).Should().Be(1);
    }

    [Fact]
    public async Task The_gate_fails_the_start_when_the_probe_proves_Streams_missing()
    {
        var connector = StreamsFakes.Connector(out var database, out _);
        database.ProbeFails(StreamsFakes.UnknownCommand());
        var sut = Gate(connector);

        await FluentActions.Awaiting(() => sut.StartAsync(Token)).Should().ThrowAsync<InvalidOperationException>().WithMessage("*StreamsUnsupported*Fail*");
    }

    [Fact]
    public async Task The_gate_passes_when_the_server_runs_Streams()
    {
        var connector = StreamsFakes.Connector(out _, out _);
        var sut = Gate(connector);

        await sut.StartAsync(Token);
        await sut.StopAsync(Token);

        BroadcastCapability.For(connector).State.Should().Be(StreamsState.Supported);
    }

    [Fact]
    public async Task The_gate_never_fails_the_start_for_a_server_that_is_down()
    {
        var connector = StreamsFakes.Connector(out _, out _);
        ValueTaskStubs.Returns(connector.ConnectAsync(Arg.Any<CancellationToken>()), _ => new ValueTask(new TaskCompletionSource().Task));
        var sut = Gate(connector);
        sut.ProbeBudget = TimeSpan.FromMilliseconds(100);

        await sut.StartAsync(Token);
        await sut.StopAsync(Token);

        BroadcastCapability.For(connector).State.Should().Be(StreamsState.Unknown);
    }

    [Fact]
    public void Negotiation_counts_the_channels_of_the_fallback_that_is_not_selected()
    {
        using var services = Build(streams => { }, extra: services => services
            .AddSingleton(Options.Create(new RedisCacheOptions()))
            .AddSingleton(new ValueSerializerRegistry([RegistryEntry.Of("fast", new PassthroughValueSerializer(0x10))]))
            .AddSingleton<ISerializerProxy<byte[]>>(new SystemJsonByteSerializerProxy())
            .AddSingleton(TimeProvider.System)
            .AddSingleton<ICachePolicyFactory>(sp => new DefaultCachePolicyFactory(null, null, valueSerializers: sp.GetRequiredService<ValueSerializerRegistry>())));

        var negotiation = FormatNegotiation.Create(services);

        negotiation.PubSub.Should().BeSameAs(services.GetRequiredService<RedisPubSubTopicProvider>());
        negotiation.StreamKeys.Should().NotBeNull();
    }

    [Fact]
    public async Task Negotiation_lists_the_channel_of_a_topic_after_it_switches()
    {
        var connector = StreamsFakes.Connector(out _, out _);
        using var pubSub = PubSub(connector);
        using var provider = Provider(connector, new RedisStreamsTopicOptions(), pubSub);
        BroadcastCapability.For(connector).Refused(StreamsFakes.UnknownCommand());
        var topic = (StreamsFallbackTopic)provider.Create("orders");
        await topic.PublishAsync(new TestCacheEvent { Id = "1", Source = new Uri("urn:test") }, Token);

        ((IPubSubSubscriptions)pubSub).Channels().Should().ContainSingle().Which.Should().EndWith("orders");
        provider.StreamKeys().Should().BeEmpty();
    }

    private static ServiceProvider BuildOrdered(Dictionary<string, string?> configuration, Action<ICachingBuilder> register)
    {
        var services = new ServiceCollection();
        var builder = new CachingBuilder(services, new ConfigurationBuilder().AddInMemoryCollection(configuration).Build());
        services
            .AddSingleton(StreamsFakes.Connector(out _, out _))
            .AddSingleton(Substitute.For<IEventFormatterProxy<ICacheEvent>>())
            .AddSingleton(Substitute.For<IResiliencePipelineProvider>())
            .AddSingleton<ICachingTelemetryProvider>(NullTelemetryProvider.Instance)
            .AddSingleton(Substitute.For<IRedisProfiler>())
            .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
            .AddSingleton(Options.Create(new CacheOptions { AppShortName = "app" }));
        register(builder);
        return services.BuildServiceProvider();
    }

    // What a TopicFactory over the registered providers offers.
    private static IEnumerable<string> SelectableNames(IServiceProvider services) =>
        new TopicFactory(services.GetRequiredService<IOptions<CacheOptions>>(), services.GetServices<ITopicProvider>()).ProviderNames;

    private static StreamsSupportGate Gate(IRedisConnector connector)
    {
        var services = new ServiceCollection().AddSingleton(connector).BuildServiceProvider();
        return new StreamsSupportGate(
            services,
            Options.Create(new RedisStreamsTopicOptions { StreamsUnsupported = StreamsUnsupportedAction.Fail }),
            Options.Create(new CacheOptions { AppShortName = "app" }),
            NullLoggerFactory.Instance);
    }

    private static RedisPubSubTopicProvider PubSub(IRedisConnector connector) => new(
        Options.Create(new RedisPubSubTopicOptions()),
        Options.Create(new CacheOptions { AppShortName = "app" }),
        new PerTopicOptionsRegistry<RedisPubSubTopicOptions>(new ConfigurationBuilder().Build().GetSection("Topics")),
        connector,
        Substitute.For<IEventFormatterProxy<ICacheEvent>>(),
        Substitute.For<IResiliencePipelineProvider>(),
        NullTelemetryProvider.Instance,
        Substitute.For<IRedisProfiler>(),
        NullLoggerFactory.Instance);

    private static RedisStreamsTopicProvider Provider(IRedisConnector connector, RedisStreamsTopicOptions options, RedisPubSubTopicProvider? fallback)
    {
        var cacheOptions = Options.Create(new CacheOptions { AppShortName = "app" });
        var registry = new PerTopicOptionsRegistry<RedisStreamsTopicOptions>(new ConfigurationBuilder().Build().GetSection("Topics"));
        var formatter = Substitute.For<IEventFormatterProxy<ICacheEvent>>();
        var resilience = Substitute.For<IResiliencePipelineProvider>();
        return fallback is null && options.StreamsUnsupported == StreamsUnsupportedAction.PubSub
            ? new RedisStreamsTopicProvider(Options.Create(options), cacheOptions, registry, connector, formatter, resilience, NullLoggerFactory.Instance, NullTelemetryProvider.Instance, Substitute.For<IRedisProfiler>())
            : new RedisStreamsTopicProvider(Options.Create(options), cacheOptions, registry, connector, formatter, resilience, NullLoggerFactory.Instance, NullTelemetryProvider.Instance, Substitute.For<IRedisProfiler>(), fallback);
    }

    private static ServiceProvider Build(
        Action<RedisStreamsTopicOptions> streams,
        Action<RedisPubSubTopicOptions>? pubSub = null,
        Dictionary<string, string?>? configure = null,
        Action<IServiceCollection>? extra = null)
    {
        var services = new ServiceCollection();
        var builder = new CachingBuilder(services, new ConfigurationBuilder().AddInMemoryCollection(configure ?? []).Build());
        services
            .AddSingleton(StreamsFakes.Connector(out _, out _))
            .AddSingleton(Substitute.For<IEventFormatterProxy<ICacheEvent>>())
            .AddSingleton(Substitute.For<IResiliencePipelineProvider>())
            .AddSingleton<ICachingTelemetryProvider>(NullTelemetryProvider.Instance)
            .AddSingleton(Substitute.For<IRedisProfiler>())
            .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
            .AddSingleton(Options.Create(new CacheOptions { AppShortName = "app" }));
        extra?.Invoke(services);
        if (pubSub is not null)
        {
            builder.AddRedisPubSub(pubSub);
        }

        builder.AddRedisStreams(streams);
        return services.BuildServiceProvider();
    }

    private async Task WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; !condition() && attempt < 3000; attempt++)
        {
            await Task.Delay(10, Token);
        }

        condition().Should().BeTrue();
    }
}
