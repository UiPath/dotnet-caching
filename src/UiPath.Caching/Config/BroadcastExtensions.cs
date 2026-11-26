using UiPath.Caching.Broadcast.Redis;
using UiPath.Caching.Policies;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Config;

[ExcludeFromCodeCoverage]
public static class BroadcastExtensions
{
    private const string BroadcastEnabledKey = "BroadcastEnabled";
    private const string RedisPubSubSectionName = "Broadcast:RedisPubSub";
    private const string RedisStreamsSectionName = "Broadcast:RedisStreams";

    public static ICachingBuilder AddBroadcast(this ICachingBuilder builder) =>
        builder.AddBroadcast(BroadcastEnabledKey);

    public static ICachingBuilder AddBroadcast(this ICachingBuilder builder, string fieldName) =>
        builder.AddBroadcast(builder.Configuration.GetValue<bool?>(fieldName).GetValueOrDefault(true));

    public static ICachingBuilder AddBroadcast(this ICachingBuilder builder, bool enabled)
    {
        if (builder.Enabled && enabled)
        {
            builder.Services.TryAddSingleton<ITopicFactory, TopicFactory>();
            builder.Services.AddHostedService<TopicSelectionValidator>();
            builder.AddRedisPubSub().AddRedisStreams();
        }

        return builder;
    }

    public static ICachingBuilder AddRedisPubSub(this ICachingBuilder builder, string sectionName = RedisPubSubSectionName) =>
        builder.AddRedisPubSub(opt => builder.Configuration.GetSection(sectionName).Bind(opt), sectionName);

    public static ICachingBuilder AddRedisPubSub(this ICachingBuilder builder, Action<RedisPubSubTopicOptions> configureOptions) =>
        builder.AddRedisPubSub(configureOptions, RedisPubSubSectionName);

    public static ICachingBuilder AddRedisPubSub(this ICachingBuilder builder, Action<RedisPubSubTopicOptions> configureOptions, string sectionName)
    {
        RedisPubSubTopicOptions options = new();
        configureOptions.Invoke(options);
        builder.Services.TryConfigure(configureOptions);
        EnsureRegistry<RedisPubSubTopicOptions>(builder, sectionName);
        if (builder.Enabled && options.Enabled)
        {
            AddRedisPubSubProvider(builder.Services);
            builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<ITopicProvider, RedisPubSubTopicProvider>(static services => services.GetRequiredService<RedisPubSubTopicProvider>()));
        }

        return builder;
    }

    public static ICachingBuilder AddRedisStreams(this ICachingBuilder builder, string sectionName = RedisStreamsSectionName) =>
        builder.AddRedisStreams(opt => ConfigurationBinding.Bind(builder.Configuration.GetSection(sectionName), opt), sectionName);

    public static ICachingBuilder AddRedisStreams(this ICachingBuilder builder, Action<RedisStreamsTopicOptions> configureOptions) =>
        builder.AddRedisStreams(configureOptions, RedisStreamsSectionName);

    public static ICachingBuilder AddRedisStreams(this ICachingBuilder builder, Action<RedisStreamsTopicOptions> configureOptions, string sectionName)
    {
        RedisStreamsTopicOptions options = new();
        configureOptions.Invoke(options);
        builder.Services.TryConfigure(configureOptions);
        EnsureRegistry<RedisStreamsTopicOptions>(builder, sectionName);
        if (builder.Enabled && options.Enabled)
        {
            RedisStreamsTopicOptionsValidator.ThrowIfInvalid(options);
            builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<RedisStreamsTopicOptions>, RedisStreamsTopicOptionsValidator>());
            if (options.StreamsUnsupported == StreamsUnsupportedAction.PubSub)
            {
                AddRedisPubSubStandby(builder);
            }

            // One instance behind both registrations, made through the internal constructor that takes the fallback.
            builder.Services.TryAddSingleton(static services => new RedisStreamsTopicProvider(
                services.GetRequiredService<IOptions<RedisStreamsTopicOptions>>(),
                services.GetRequiredService<IOptions<CacheOptions>>(),
                services.GetRequiredService<PerTopicOptionsRegistry<RedisStreamsTopicOptions>>(),
                services.GetRequiredService<IRedisConnector>(),
                services.GetRequiredService<IEventFormatterProxy<ICacheEvent>>(),
                services.GetRequiredService<IResiliencePipelineProvider>(),
                services.GetRequiredService<ILoggerFactory>(),
                services.GetRequiredService<ICachingTelemetryProvider>(),
                services.GetRequiredService<IRedisProfiler>(),
                services.GetService<RedisPubSubTopicProvider>(),
                services.GetRequiredService<TimeProvider>()));
            builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<ITopicProvider, RedisStreamsTopicProvider>(static services => services.GetRequiredService<RedisStreamsTopicProvider>()));
            if (options.StreamsUnsupported == StreamsUnsupportedAction.Fail)
            {
                builder.Services.AddHostedService<StreamsSupportGate>();
            }

            if (options.MaintainerEnabled)
            {
                builder.Services.AddHostedService<RedisStreamHealthMaintainer>();
            }
        }
        return builder;
    }

    public static ICachingBuilder ConfigureRedisStreamsTopic(this ICachingBuilder builder, string topicName, Action<RedisStreamsTopicOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(topicName);
        ArgumentNullException.ThrowIfNull(configure);
        RequireRegistry<RedisStreamsTopicOptions>(builder, nameof(AddRedisStreams), nameof(ConfigureRedisStreamsTopic))
            .Configure(topicName, configure);
        return builder;
    }

    public static ICachingBuilder ConfigureRedisPubSubTopic(this ICachingBuilder builder, string topicName, Action<RedisPubSubTopicOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(topicName);
        ArgumentNullException.ThrowIfNull(configure);
        RequireRegistry<RedisPubSubTopicOptions>(builder, nameof(AddRedisPubSub), nameof(ConfigureRedisPubSubTopic))
            .Configure(topicName, configure);
        return builder;
    }

    private static void AddRedisPubSubProvider(IServiceCollection services) => services.TryAddSingleton<RedisPubSubTopicProvider>();

    // Claims nothing at registration, so AddRedisPubSub may come before or after: the provider is made on first use, from the options and registry the app registered
    // for Pub/Sub, else from the default section. Its Enabled is whatever those say, and only AddRedisPubSub puts it among the providers TopicFactory chooses from.
    private static void AddRedisPubSubStandby(ICachingBuilder builder) =>
        builder.Services.TryAddSingleton(services =>
        {
            var configured = services.GetServices<IConfigureOptions<RedisPubSubTopicOptions>>().Any();
            var section = builder.Configuration.GetSection(RedisPubSubSectionName);
            var options = configured ? services.GetRequiredService<IOptions<RedisPubSubTopicOptions>>() : Options.Create(section.Get<RedisPubSubTopicOptions>() ?? new());
            var registry = (configured ? services.GetService<PerTopicOptionsRegistry<RedisPubSubTopicOptions>>() : null)
                ?? new PerTopicOptionsRegistry<RedisPubSubTopicOptions>(builder.Configuration.GetSection($"{RedisPubSubSectionName}:Topics"));
            return new RedisPubSubTopicProvider(
                options,
                services.GetRequiredService<IOptions<CacheOptions>>(),
                registry,
                services.GetRequiredService<IRedisConnector>(),
                services.GetRequiredService<IEventFormatterProxy<ICacheEvent>>(),
                services.GetRequiredService<IResiliencePipelineProvider>(),
                services.GetRequiredService<ICachingTelemetryProvider>(),
                services.GetRequiredService<IRedisProfiler>(),
                services.GetRequiredService<ILoggerFactory>(),
                services.GetRequiredService<TimeProvider>());
        });

    private static void EnsureRegistry<TOptions>(ICachingBuilder builder, string sectionName)
        where TOptions : class
    {
        var topicsSection = builder.Configuration.GetSection($"{sectionName}:Topics");
        var descriptor = builder.Services.FirstOrDefault(sd => sd.ServiceType == typeof(PerTopicOptionsRegistry<TOptions>));
        if (descriptor?.ImplementationInstance is PerTopicOptionsRegistry<TOptions> existing)
        {
            if (!string.Equals(existing.TopicsSection.Path, topicsSection.Path, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"A {nameof(PerTopicOptionsRegistry<TOptions>)} for {typeof(TOptions).Name} is already registered against section '{existing.TopicsSection.Path}'; a different section '{topicsSection.Path}' was requested. Call AddRedis* once per provider with a consistent sectionName.");
            }
            return;
        }

        builder.Services.AddSingleton(new PerTopicOptionsRegistry<TOptions>(topicsSection));
    }

    private static PerTopicOptionsRegistry<TOptions> RequireRegistry<TOptions>(ICachingBuilder builder, string addMethodName, string configureMethodName)
        where TOptions : class
    {
        var descriptor = builder.Services.FirstOrDefault(sd => sd.ServiceType == typeof(PerTopicOptionsRegistry<TOptions>));
        if (descriptor?.ImplementationInstance is PerTopicOptionsRegistry<TOptions> existing)
        {
            return existing;
        }

        throw new InvalidOperationException(
            $"Call {addMethodName} before {configureMethodName} so the per-topic registry binds to the correct configuration section.");
    }
}
