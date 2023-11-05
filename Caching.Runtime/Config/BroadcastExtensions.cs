using UiPath.Platform.Caching.Broadcast.Redis;

namespace UiPath.Platform.Caching.Config;

[ExcludeFromCodeCoverage]
public static class BroadcastExtensions
{
    private const string DefaultFielKey = "BroadcastEnabled";
    private const string RedisPubSubSectionName = "Broadcast:RedisPubSub";
    private const string RedisStreamsSectionName = "Broadcast:RedisStreams";

    public static ICachingBuilder AddBroadcast(this ICachingBuilder builder) =>
        builder.AddBroadcast(DefaultFielKey);

    public static ICachingBuilder AddBroadcast(this ICachingBuilder builder, string fieldName) =>
        builder.AddBroadcast(builder.Configuration.GetValue<bool?>(fieldName).GetValueOrDefault(true));

    public static ICachingBuilder AddBroadcast(this ICachingBuilder builder, bool enabled)
    {
        if (builder.Enabled)
        {
            if (enabled)
            {
                builder.Services.TryAddSingleton<ITopicFactory, TopicFactory>();
                builder.AddRedisPubSub().AddRedisStreams();
            }
            else
            {
                builder.Services.TryAddSingleton<ITopicFactory, NullTopicFactory>();
            }
        }

        return builder;
    }

    public static ICachingBuilder AddRedisPubSub(this ICachingBuilder builder, string sectionName = RedisPubSubSectionName) =>
            builder.AddRedisPubSub(opt => builder.Configuration.GetSection(sectionName).Bind(opt));

    public static ICachingBuilder AddRedisPubSub(this ICachingBuilder builder, Action<RedisPubSubTopicOptions> configureOptions)
    {
        RedisPubSubTopicOptions options = new();
        configureOptions.Invoke(options);
        builder.Services.TryConfigure(configureOptions);
        if (options.Enabled)
        {
            builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<ITopicProvider, RedisPubSubTopicProvider>());
        }

        return builder;
    }

    public static ICachingBuilder AddRedisStreams(this ICachingBuilder builder, string sectionName = RedisStreamsSectionName) =>
        builder.AddRedisStreams(opt => builder.Configuration.GetSection(sectionName).Bind(opt));

    public static ICachingBuilder AddRedisStreams(this ICachingBuilder builder, Action<RedisStreamsTopicOptions> configureOptions)
    {
        RedisStreamsTopicOptions options = new();
        configureOptions.Invoke(options);
        builder.Services.TryConfigure(configureOptions);
        if (options.Enabled)
        {
            builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<ITopicProvider, RedisStreamsTopicProvider>());
        }
        return builder;
    }
}
