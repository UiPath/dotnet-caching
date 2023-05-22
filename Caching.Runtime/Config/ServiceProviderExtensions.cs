using UiPath.Platform.Caching.Broadcast.Redis;
using UiPath.Platform.Caching.Memory;
using UiPath.Platform.Caching.Telemetry;

namespace UiPath.Platform.Caching.Config;

[ExcludeFromCodeCoverage]
public static class ServiceProviderExtensions
{
    public static IRedisCache BuildRedisCache(this IServiceProvider serviceProvider, RedisCacheOptions? options = null) =>
        serviceProvider.InternalBuildRedisCache<RedisCache>(options);

    public static IHybridCache BuildHybridCache(this IServiceProvider serviceProvider, Func<IServiceProvider, ICache>? innerCacheAccessor = null, HybridCacheOptions? options = null)
    {
        ICache innerCacheFunc(IServiceProvider sp) => innerCacheAccessor != null ? innerCacheAccessor(sp) : sp.GetRequiredService<IRedisCache>();
        return serviceProvider.InternalBuildHybridCache<HybridCache, ICache>(innerCacheFunc, options);
    }

    public static IMemCache BuildMemoryCache(this IServiceProvider serviceProvider, MemCacheOptions? options = null) =>
        serviceProvider.InternalBuildMemoryCache<MemCache>(options);

    public static IRedisRegionCache BuildRedisRegionCache(this IServiceProvider serviceProvider, RedisCacheOptions? options = null) =>
        serviceProvider.InternalBuildRedisCache<RedisHashSetCache>(options);

    public static IHybridRegionCache BuildHybridRegionCache(this IServiceProvider serviceProvider, Func<IServiceProvider, IRegionCache>? innerCacheAccessor = null, HybridCacheOptions? options = null)
    {
        IRegionCache innerCacheFunc(IServiceProvider sp) => innerCacheAccessor != null ? innerCacheAccessor(sp) : sp.GetRequiredService<IRedisRegionCache>();
        return serviceProvider.InternalBuildHybridCache<HybridRegionCache, IRegionCache>(innerCacheFunc, options);
    }

    public static IMemRegionCache BuildMemoryRegionCache(this IServiceProvider serviceProvider, MemCacheOptions? options = null) =>
        serviceProvider.InternalBuildMemoryCache<MemRegionCache>(options);

    public static IChannelSubscriber<T> BuildRedisChannelSubscriber<T>(this IServiceProvider serviceProvider) where T : class, IPubSubEvent
    {
        var subscriber = serviceProvider.GetRequiredService<ISubscriber>();
        var eventFormatter = serviceProvider.GetRequiredService<IEventFormatterProxy<T>>();
        var loggerFactory = serviceProvider.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance;
        return new RedisChannelSubscriber<T>(subscriber, eventFormatter, loggerFactory);
    }

    public static IRedisChannelPublisher<T> BuildRedisChannelPublisher<T>(this IServiceProvider serviceProvider) where T : class, IPubSubEvent
    {
        var databaseAccessor = serviceProvider.GetRequiredService<Func<IDatabase>>();
        var eventFormatter = serviceProvider.GetRequiredService<IEventFormatterProxy<T>>();
        var loggerFactory = serviceProvider.GetService<ILoggerFactory>();
        var logger = loggerFactory?.CreateLogger<RedisChannelPublisher<T>>() ?? NullLogger<RedisChannelPublisher<T>>.Instance;
        return new RedisChannelPublisher<T>(databaseAccessor, eventFormatter, logger);
    }

    private static TCache InternalBuildRedisCache<TCache>(this IServiceProvider serviceProvider, RedisCacheOptions? options = null)
        where TCache : class
    {
        var config = options != null ? Options.Create(options) : serviceProvider.GetService<IOptions<RedisCacheOptions>>();
        config ??= Options.Create(new RedisCacheOptions());
        var databaseAccessor = serviceProvider.GetRequiredService<Func<IDatabase>>();
        var serializer = serviceProvider.GetRequiredService<ISerializerProxy>();
        var policyHolder = serviceProvider.GetRequiredService<IPolicyHolder>();
        var loggerFactory = serviceProvider.GetService<ILoggerFactory>();
        var logger = loggerFactory?.CreateLogger<TCache>() ?? NullLogger<TCache>.Instance;
        var telemetryProvider = serviceProvider.GetService<ICachingTelemetryProvider>() ?? NullTelemetryProvider.Instance;
        return ActivatorUtilities.CreateInstance<TCache>(serviceProvider, databaseAccessor, serializer, policyHolder, telemetryProvider, config, logger);
    }

    private static TCache InternalBuildHybridCache<TCache, TInnerCache>(this IServiceProvider serviceProvider,
        Func<IServiceProvider, TInnerCache> innerCacheAccessor,
        HybridCacheOptions? options = null)
         where TCache : class
         where TInnerCache : notnull
    {
        var config = options != null ? Options.Create(options) : serviceProvider.GetService<IOptions<HybridCacheOptions>>();
        config ??= Options.Create(new HybridCacheOptions());
        var memoryCacheAccessor = serviceProvider.GetService<Func<HybridCacheOptions, IMemoryCache>>() ?? new Func<HybridCacheOptions, IMemoryCache>(options => {
            return serviceProvider.GetService<IMemoryCache>() ?? new MemoryCache(new MemoryCacheOptions());
        });
        var changeTokenFactory = serviceProvider.GetRequiredService<IChangeTokenFactory>();
        var channelPublisher = serviceProvider.GetService<IRedisChannelPublisher<ICacheEvent>>() ?? serviceProvider.GetRequiredService<IChannelPublisher<ICacheEvent>>();
        var channelResolver = serviceProvider.GetRequiredService<IChannelResolver>();
        var clearCacheEventFactory = serviceProvider.GetRequiredService<ICacheEventFactory>();
        var loggerFactory = serviceProvider.GetService<ILoggerFactory>();
        var logger = loggerFactory?.CreateLogger<TCache>() ?? NullLogger<TCache>.Instance;
        var innerCache = innerCacheAccessor(serviceProvider);
        var telemetryProvider = serviceProvider.GetService<ICachingTelemetryProvider>() ?? NullTelemetryProvider.Instance;
        return ActivatorUtilities.CreateInstance<TCache>(serviceProvider,
            innerCache,
            memoryCacheAccessor,
            changeTokenFactory,
            channelPublisher,
            channelResolver,
            clearCacheEventFactory,
            telemetryProvider,
            config,
            logger);
    }

    private static TCache InternalBuildMemoryCache<TCache>(this IServiceProvider serviceProvider, MemCacheOptions? options = null)
        where TCache : class
    {
        var config = options != null ? Options.Create(options) : serviceProvider.GetService<IOptions<MemCacheOptions>>();
        config ??= Options.Create(new MemCacheOptions());
        var memoryCacheAccessor = serviceProvider.GetService<Func<MemCacheOptions, IMemoryCache>>() ?? new Func<MemCacheOptions, IMemoryCache>(options => {
            return serviceProvider.GetService<IMemoryCache>() ?? new MemoryCache(new MemoryCacheOptions());
        });

        var changeTokenFactory = NullChangeTokenFactory.Instance;
        if (config.Value.EnableChangeToken)
        {
            changeTokenFactory = serviceProvider.GetService<Func<MemCacheOptions, IChangeTokenFactory?>>()?.Invoke(config.Value) ?? serviceProvider.GetService<IChangeTokenFactory>() ?? NullChangeTokenFactory.Instance;
        }

        var loggerFactory = serviceProvider.GetService<ILoggerFactory>();
        var logger = loggerFactory?.CreateLogger<TCache>() ?? NullLogger<TCache>.Instance;
        var telemetryProvider = serviceProvider.GetService<ICachingTelemetryProvider>() ?? NullTelemetryProvider.Instance;
        return ActivatorUtilities.CreateInstance<TCache>(serviceProvider, memoryCacheAccessor, changeTokenFactory, telemetryProvider, config, logger);
    }
}
