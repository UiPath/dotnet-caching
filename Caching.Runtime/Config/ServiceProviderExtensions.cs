using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using UiPath.Platform.Caching.Broadcast;
using UiPath.Platform.Caching.Broadcast.Redis;
using UiPath.Platform.Caching.Hybrid;
using UiPath.Platform.Caching.Redis;
using UiPath.Platform.Caching.Telemetry;

namespace UiPath.Platform.Caching.Config;

[ExcludeFromCodeCoverage]
public static class ServiceProviderExtensions
{
    public static IRedisCache BuildRedisCache(this IServiceProvider serviceProvider, RedisCacheOptions? options = null)
    {
        var config = options != null ? Options.Create(options) : serviceProvider.GetService<IOptions<RedisCacheOptions>>();
        config ??= Options.Create(new RedisCacheOptions());
        var databaseAccessor = serviceProvider.GetRequiredService<Func<IDatabase>>();
        var serializer = serviceProvider.GetRequiredService<ISerializerProxy>();
        var policyHolder = serviceProvider.GetRequiredService<IPolicyHolder>();
        var loggerFactory = serviceProvider.GetService<ILoggerFactory>();
        var logger = loggerFactory?.CreateLogger<RedisCache>() ?? NullLogger<RedisCache>.Instance;
        var telemetryProvider = serviceProvider.GetService<ICachingTelemetryProvider>() ?? NullTelemetryProvider.Instance;

        return new RedisCache(databaseAccessor, serializer, policyHolder, telemetryProvider, config, logger);
    }

    public static IHybridCache BuildHybridCache(this IServiceProvider serviceProvider, Func<IServiceProvider, ICache>? innerCacheAccessor = null, HybridCacheOptions? options = null)
    {
        var config = options != null ? Options.Create(options) : serviceProvider.GetService<IOptions<HybridCacheOptions>>();
        config ??= Options.Create(new HybridCacheOptions());
        var memoryCacheAccessor = serviceProvider.GetRequiredService<Func<HybridCacheOptions, IMemoryCache>>();
        var changeTokenFactory = serviceProvider.GetRequiredService<IChangeTokenFactory>();
        var channelPublisher = serviceProvider.GetRequiredService<IChannelPublisher>();
        var channelResolver = serviceProvider.GetRequiredService<IChannelResolver>();
        var clearCacheEventFactory = serviceProvider.GetRequiredService<IClearCacheEventFactory>();
        var loggerFactory = serviceProvider.GetService<ILoggerFactory>();
        var logger = loggerFactory?.CreateLogger<HybridCache>() ?? NullLogger<HybridCache>.Instance;
        var innerCache = innerCacheAccessor?.Invoke(serviceProvider) ?? serviceProvider.GetRequiredService<IRedisCache>();
        var telemetryProvider = serviceProvider.GetService<ICachingTelemetryProvider>() ?? NullTelemetryProvider.Instance;

        return new HybridCache(
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

    public static IRedisRegionCache BuildRedisRegionCache(this IServiceProvider serviceProvider, RedisCacheOptions? options = null)
    {
        var config = options != null ? Options.Create(options) : serviceProvider.GetService<IOptions<RedisCacheOptions>>();
        config ??= Options.Create(new RedisCacheOptions());
        var databaseAccessor = serviceProvider.GetRequiredService<Func<IDatabase>>();
        var serializer = serviceProvider.GetRequiredService<ISerializerProxy>();
        var policyHolder = serviceProvider.GetRequiredService<IPolicyHolder>();
        var loggerFactory = serviceProvider.GetService<ILoggerFactory>();
        var logger = loggerFactory?.CreateLogger<RedisHashSetCache>() ?? NullLogger<RedisHashSetCache>.Instance;
        var telemetryProvider = serviceProvider.GetService<ICachingTelemetryProvider>() ?? NullTelemetryProvider.Instance;

        return new RedisHashSetCache(
            databaseAccessor,
            serializer,
            policyHolder,
            telemetryProvider,
            config,
            logger);
    }

    public static IHybridRegionCache BuildHybridRegionCache(this IServiceProvider serviceProvider, Func<IServiceProvider, IRegionCache>? innerCacheAccessor = null, HybridCacheOptions? options = null)
    {
        var config = options != null ? Options.Create(options) : serviceProvider.GetService<IOptions<HybridCacheOptions>>();
        config ??= Options.Create(new HybridCacheOptions());
        var memoryCacheAccessor = serviceProvider.GetRequiredService<Func<HybridCacheOptions, IMemoryCache>>();
        var changeTokenFactory = serviceProvider.GetRequiredService<IChangeTokenFactory>();
        var channelPublisher = serviceProvider.GetRequiredService<IChannelPublisher>();
        var channelResolver = serviceProvider.GetRequiredService<IChannelResolver>();
        var clearCacheEventFactory = serviceProvider.GetRequiredService<IClearCacheEventFactory>();

        var loggerFactory = serviceProvider.GetService<ILoggerFactory>();
        var logger = loggerFactory?.CreateLogger<HybridRegionCache>() ?? NullLogger<HybridRegionCache>.Instance;
        var innerCache = innerCacheAccessor?.Invoke(serviceProvider) ?? serviceProvider.GetRequiredService<IRedisRegionCache>();
        var telemetryProvider = serviceProvider.GetService<ICachingTelemetryProvider>() ?? NullTelemetryProvider.Instance;

        return new HybridRegionCache(
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

    public static IChannelSubscriber BuildRedisChannelSubscriber(this IServiceProvider serviceProvider)
    {
        var subscriber = serviceProvider.GetRequiredService<ISubscriber>();
        var eventFormatter = serviceProvider.GetRequiredService<IEventFormatterProxy>();
        var loggerFactory = serviceProvider.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance;
        return new RedisChannelSubscriber(subscriber, eventFormatter, loggerFactory);
    }

    public static IChannelPublisher BuildRedisChannelPublisher(this IServiceProvider serviceProvider)
    {
        var databaseAccessor = serviceProvider.GetRequiredService<Func<IDatabase>>();
        var eventFormatter = serviceProvider.GetRequiredService<IEventFormatterProxy>();
        var loggerFactory = serviceProvider.GetService<ILoggerFactory>();
        var logger = loggerFactory?.CreateLogger<RedisChannelPublisher>() ?? NullLogger<RedisChannelPublisher>.Instance;
        return new RedisChannelPublisher(databaseAccessor, eventFormatter, logger);
    }

    public static IRedisCache<T> BuildRedisCache<T>(this IServiceProvider serviceProvider) where T : class =>
    new RedisCache<T>(serviceProvider.GetRequiredService<IRedisCache>());

    public static IHybridCache<T> BuildHybridCache<T>(this IServiceProvider serviceProvider) where T : class =>
        new HybridCache<T>(serviceProvider.GetRequiredService<IHybridCache>());

    public static IRedisRegionCache<T> BuildRedisRegionCache<T>(this IServiceProvider serviceProvider) where T : class =>
        new RedisHashSetCache<T>(serviceProvider.GetRequiredService<IRedisRegionCache>());

    public static IHybridRegionCache<T> BuildHybridRegionCache<T>(this IServiceProvider serviceProvider) where T : class =>
        new HybridRegionCache<T>(serviceProvider.GetRequiredService<IHybridRegionCache>());
}
