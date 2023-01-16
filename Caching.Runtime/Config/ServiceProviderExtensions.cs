using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Polly;
using Polly.Timeout;
using UiPath.Platform.Caching.Broadcast;
using UiPath.Platform.Caching.Broadcast.Redis;
using UiPath.Platform.Caching.Hybrid;
using UiPath.Platform.Caching.Redis;

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

        return new RedisCache(databaseAccessor, serializer, policyHolder, config, logger);
    }

    public static IHybridCache BuildHybridCache(this IServiceProvider serviceProvider, Func<IServiceProvider, ICache>? innerCacheAccessor = null, HybridCacheOptions? options = null)
    {
        var config = options != null ? Options.Create(options) : serviceProvider.GetService<IOptions<HybridCacheOptions>>();
        config ??= Options.Create(new HybridCacheOptions());
        var memoryCacheAccessor = serviceProvider.GetRequiredService<Func<IMemoryCache>>();
        var changeTokenFactory = serviceProvider.GetRequiredService<IChangeTokenFactory>();
        var channelPublisher = serviceProvider.GetRequiredService<IChannelPublisher>();
        var channelResolver = serviceProvider.GetRequiredService<IChannelResolver>();

        var loggerFactory = serviceProvider.GetService<ILoggerFactory>();
        var logger = loggerFactory?.CreateLogger<HybridCache>() ?? NullLogger<HybridCache>.Instance;
        var innerCache = innerCacheAccessor?.Invoke(serviceProvider) ?? serviceProvider.GetRequiredService<IRedisCache>();
        return new HybridCache(
            innerCache,
            memoryCacheAccessor,
            changeTokenFactory,
            channelPublisher,
            channelResolver,
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

        return new RedisHashSetCache(databaseAccessor, serializer, policyHolder, config, logger);
    }

    public static IHybridRegionCache BuildHybridRegionCache(this IServiceProvider serviceProvider, Func<IServiceProvider, IRegionCache>? innerCacheAccessor = null, HybridCacheOptions? options = null)
    {
        var config = options != null ? Options.Create(options) : serviceProvider.GetService<IOptions<HybridCacheOptions>>();
        config ??= Options.Create(new HybridCacheOptions());
        var memoryCacheAccessor = serviceProvider.GetRequiredService<Func<IMemoryCache>>();
        var changeTokenFactory = serviceProvider.GetRequiredService<IChangeTokenFactory>();
        var channelPublisher = serviceProvider.GetRequiredService<IChannelPublisher>();
        var channelResolver = serviceProvider.GetRequiredService<IChannelResolver>();

        var loggerFactory = serviceProvider.GetService<ILoggerFactory>();
        var logger = loggerFactory?.CreateLogger<HybridRegionCache>() ?? NullLogger<HybridRegionCache>.Instance;
        var innerCache = innerCacheAccessor?.Invoke(serviceProvider) ?? serviceProvider.GetRequiredService<IRedisRegionCache>();
        return new HybridRegionCache(innerCache, memoryCacheAccessor, changeTokenFactory, channelPublisher, channelResolver, config, logger);
    }

    public static IChannelSubscriber BuildRedisChannelSubscriber(this IServiceProvider serviceProvider)
    {
        var subscriber = serviceProvider.GetRequiredService<ISubscriber>();
        var cloudEventFormatter = serviceProvider.GetRequiredService<CloudEventFormatter>();
        var loggerFactory = serviceProvider.GetService<ILoggerFactory>();
        var logger = loggerFactory?.CreateLogger<RedisChannelSubscriber>() ?? NullLogger<RedisChannelSubscriber>.Instance;
        return new RedisChannelSubscriber(subscriber, cloudEventFormatter, logger);
    }

    public static IChannelPublisher BuildRedisChannelPublisher(this IServiceProvider serviceProvider)
    {
        var databaseAccessor = serviceProvider.GetRequiredService<Func<IDatabase>>();
        var cloudEventFormatter = serviceProvider.GetRequiredService<CloudEventFormatter>();
        var loggerFactory = serviceProvider.GetService<ILoggerFactory>();
        var logger = loggerFactory?.CreateLogger<RedisChannelPublisher>() ?? NullLogger<RedisChannelPublisher>.Instance;
        return new RedisChannelPublisher(databaseAccessor, cloudEventFormatter, logger);
    }

    public static IAsyncPolicy BuildCircuitBreakerPolicy(this IServiceProvider serviceProvider, RedisCacheOptions? options = null)
    {
        var config = options != null ? Options.Create(options) : serviceProvider.GetService<IOptions<RedisCacheOptions>>();
        config ??= Options.Create(new RedisCacheOptions());
        var redisCacheOptions = config.Value;
        var loggerFactory = serviceProvider.GetService<ILoggerFactory>();
        var logger = loggerFactory?.CreateLogger("CircuitBreaker") ?? NullLogger.Instance;
        return Policy
            .Handle<Exception>()
            .CircuitBreakerAsync(
                exceptionsAllowedBeforeBreaking: redisCacheOptions.ExceptionsAllowedBeforeBreaking,
                durationOfBreak: redisCacheOptions.DurationOfBreak,
                onBreak: (exception, breakDelay) => logger.LogWarning(exception, "CircuitBreaker for Redis operation: Breaking the circuit for {}!", redisCacheOptions.DurationOfBreak),
                onReset: () => logger.LogWarning("CircuitBreaker for Redis operation: Circuit closed, requests flow normally."),
                onHalfOpen: () => logger.LogWarning("CircuitBreaker for Redis operation: Circuit in test mode, one request will be allowed.")
            );
    }

    public static IAsyncPolicy BuildTimeoutPolicy(this IServiceProvider serviceProvider, RedisCacheOptions? options = null)
    {
        var config = options != null ? Options.Create(options) : serviceProvider.GetService<IOptions<RedisCacheOptions>>();
        config ??= Options.Create(new RedisCacheOptions());
        var redisCacheOptions = config.Value;
        return Policy.TimeoutAsync(redisCacheOptions.RequestTimeout, TimeoutStrategy.Pessimistic);
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
