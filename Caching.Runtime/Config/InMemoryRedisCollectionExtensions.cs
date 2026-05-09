namespace UiPath.Platform.Caching.Config;

[ExcludeFromCodeCoverage]
public static class InMemoryRedisCollectionExtensions
{
    public static ICachingBuilder AddInMemoryRedis(this ICachingBuilder builder, string sectionName = KnownCacheProviderNames.InMemoryRedis) =>
        builder.AddInMemoryRedis(opt => builder.Configuration.GetSection(sectionName).Bind(opt));

    public static ICachingBuilder AddInMemoryRedis(this ICachingBuilder builder, Action<InMemoryRedisCacheOptions> configure)
    {
        var options = new InMemoryRedisCacheOptions();
        configure(options);
        builder.Services.Configure(configure);
        if (!builder.Enabled || !options.Enabled)
        {
            return builder;
        }
        builder.Services
            .TryAddMemoryCacheFactory()
            .TryAddEnumerable(ServiceDescriptor.Singleton<ICacheProvider, InMemoryRedisCacheProvider>());
        builder.AddBroadcast();
        return builder.AddCallback();
    }

    private static ICachingBuilder AddCallback(this ICachingBuilder builder)
    {
        builder.RegisterOnCompleteCallback(typeof(InMemoryRedisCollectionExtensions), b =>
        {
            b.Services.TryAddSingleton<IChangeTokenFactory, ChangeTokenFactory<RedisValue>>();
            b.Services.TryAddSingleton<IEventFormatterProxy<ICacheEvent>, CacheEventFormatter>();
            b.Services.TryAddSingleton<ICacheEventFactory, CacheEventFactory>();
        });

        return builder;
    }
}
