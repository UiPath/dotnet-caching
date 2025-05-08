namespace UiPath.Platform.Caching.Config;

[ExcludeFromCodeCoverage]
public static class InMemoryRedisCollectionExtensions
{
    private static int _callbackRegistered = 0;

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
        if (Interlocked.Exchange(ref _callbackRegistered, 1) == 0)
        {
            builder.RegisterOnCompleteCallback(builder =>
            {
                builder.Services.TryAddSingleton<IChangeTokenFactory, ChangeTokenFactory<RedisValue>>();
                builder.Services.TryAddSingleton<IEventFormatterProxy<ICacheEvent>, CacheEventFormatter>();
                builder.Services.TryAddSingleton<ICacheEventFactory, CacheEventFactory>();
            });
        }

        return builder;
    }
}
