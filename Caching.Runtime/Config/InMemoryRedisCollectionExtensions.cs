using UiPath.Platform.Caching.Telemetry;

namespace UiPath.Platform.Caching.Config;

[ExcludeFromCodeCoverage]
public static class InMemoryRedisCollectionExtensions
{
    public static ICachingBuilder AddInMemoryRedis(this ICachingBuilder builder, string sectionName = KnownCacheProviderNames.InMemoryRedis) =>
    builder.AddInMemoryRedis(opt => builder.Configuration.GetSection(sectionName).Bind(opt));

    public static ICachingBuilder AddInMemoryRedis(this ICachingBuilder builder, Action<InMemoryRedisCacheOptions> configure)
    {
        builder.Services.Configure(configure);
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<ICacheProvider, InMemoryRedisCacheProvider>());
        builder.AddBroadcast();
        return builder.AddCallback();
    }

    private static ICachingBuilder AddCallback(this ICachingBuilder builder)
    {
        builder.RegisterOnCompleteCallback(builder =>
        {
            builder.Services.TryAddSingleton<IChangeTokenFactory, ChangeTokenFactory>();
            builder.Services.TryAddSingleton<IEventFormatterProxy<ICacheEvent>, CacheEventFormatter>();
            builder.Services.TryAddSingleton<ICacheEventFactory, CacheEventFactory>();
            builder.Services.AddMemoryCacheFactory();
        });

        return builder;
    }
}
