using Microsoft.Extensions.Caching.Distributed;
using UiPath.Caching.Distributed;
using UiPath.Caching.Locking;
using UiPath.Caching.Policies;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Config;

public static class DistributedCacheCollectionExtensions
{
    /// <summary>Service key of the distributed cache's private <see cref="ICacheProvider"/> and <see cref="ICache"/> registrations.</summary>
    public const string DistributedCacheServiceKey = "UiPath.Caching.Distributed";

    /// <summary>Registers an <see cref="IDistributedCache"/> backed by a dedicated case-sensitive, raw-byte cache instance. <paramref name="providerName"/> selects the backing tier: Redis (recommended), InMemoryRedis, or InMemory.</summary>
    public static ICachingBuilder AddDistributedCache(
        this ICachingBuilder builder,
        string providerName,
        Action<UiPathDistributedCacheOptions>? configure = null)
    {
        Guard.NotNullOrWhiteSpace(providerName, nameof(providerName));
        var options = new UiPathDistributedCacheOptions();
        configure?.Invoke(options);
        if (!builder.Enabled)
        {
            return builder;
        }

        if (providerName is KnownCacheProviderNames.InMemory or KnownCacheProviderNames.InMemoryRedis)
        {
            builder.Services.TryAddMemoryCacheFactory();
        }

        builder.Services.AddKeyedSingleton<ICacheProvider>(DistributedCacheServiceKey,
            (sp, _) => CreateProvider(sp, providerName, options));
        builder.Services.AddKeyedSingleton<ICache>(DistributedCacheServiceKey,
            (sp, key) => sp.GetRequiredKeyedService<ICacheProvider>(key!).CreateCache());
        builder.Services.TryAddSingleton<IDistributedCache>(sp => new UiPathDistributedCache(
            sp.GetRequiredKeyedService<ICache>(DistributedCacheServiceKey),
            options,
            sp.GetService<ICachePolicyFactory>(),
            sp.GetRequiredService<ILoggerFactory>().Create<UiPathDistributedCache>(),
            slideByRewrite: providerName == KnownCacheProviderNames.InMemory));
        return builder;
    }

    private static ICacheProvider CreateProvider(IServiceProvider sp, string providerName, UiPathDistributedCacheOptions options)
    {
        var provider = CreateProviderCore(sp, providerName);
        EnsureBoundedWrites(sp, providerName, options);
        return provider;
    }

    /// <summary>Writes without a caller expiration take the provider default TTL; reject configurations where that default resolves to unbounded.</summary>
    private static void EnsureBoundedWrites(IServiceProvider sp, string providerName, UiPathDistributedCacheOptions options)
    {
        TimeSpan? tierDefault = providerName switch
        {
            KnownCacheProviderNames.Redis => sp.GetRequiredService<IOptions<RedisCacheOptions>>().Value.DefaultExpiration,
            KnownCacheProviderNames.InMemoryRedis => sp.GetRequiredService<IOptions<InMemoryRedisCacheOptions>>().Value.DefaultExpiration,
            _ => sp.GetRequiredService<IOptions<InMemoryCacheOptions>>().Value.DefaultExpiration,
        };
        if (tierDefault is not null)
        {
            return;
        }

        var policyFactory = sp.GetService<ICachePolicyFactory>();
        var policy = options.PolicyName is { } policyName ? policyFactory?.Resolve(policyName) : null;
        if ((policy ?? policyFactory?.Default)?.DistributedExpiration is null)
        {
            throw new InvalidOperationException(
                $"AddDistributedCache('{providerName}') would store entries without an expiration: the provider's DefaultExpiration is null and no cache policy supplies DistributedExpiration. Configure DefaultExpiration or a policy with DistributedExpiration.");
        }
    }

    private static ICacheProvider CreateProviderCore(IServiceProvider sp, string providerName) =>
        providerName switch
        {
            KnownCacheProviderNames.Redis => CreateRedisProvider(sp),
            KnownCacheProviderNames.InMemoryRedis => new InMemoryRedisCacheProvider(
                sp.GetRequiredService<IOptions<InMemoryRedisCacheOptions>>(),
                sp.GetRequiredService<IOptions<CacheOptions>>(),
                sp.GetRequiredService<IMemoryCacheFactory>(),
                () => new CacheFactory(
                    sp.GetRequiredService<IOptions<CacheOptions>>(),
                    [CreateRedisProvider(sp)],
                    sp.GetService<ICachePolicyFactory>()),
                sp.GetRequiredService<IChangeTokenFactory>(),
                sp.GetRequiredService<ITopicFactory>(),
                sp.GetRequiredService<ICacheEventFactory>(),
                sp.GetRequiredService<ICachingTelemetryProvider>(),
                sp.GetRequiredService<ILoggerFactory>(),
                sp.GetRequiredService<ILocalLock>(),
                sp.GetRequiredService<IDistributedLock>(),
                sp.GetRequiredService<ICachePolicyFactory>()),
            KnownCacheProviderNames.InMemory => new InMemoryCacheProvider(
                sp.GetRequiredService<IOptions<InMemoryCacheOptions>>(),
                sp.GetRequiredService<IOptions<CacheOptions>>(),
                sp.GetRequiredService<IMemoryCacheFactory>(),
                sp.GetRequiredService<ICacheEventFactory>(),
                sp.GetRequiredService<IChangeTokenFactory>(),
                sp.GetRequiredService<ITopicFactory>(),
                sp.GetRequiredService<ICachingTelemetryProvider>(),
                sp.GetRequiredService<ILoggerFactory>(),
                sp.GetRequiredService<ILocalLock>(),
                sp.GetRequiredService<ICachePolicyFactory>()),
            _ => throw new InvalidOperationException(
                $"Cache provider '{providerName}' is not supported by AddDistributedCache. " +
                $"Supported: {KnownCacheProviderNames.Redis}, {KnownCacheProviderNames.InMemoryRedis}, {KnownCacheProviderNames.InMemory}."),
        };

    private static RedisCacheProvider CreateRedisProvider(IServiceProvider sp) =>
        new(
            sp.GetRequiredService<IOptions<RedisCacheOptions>>(),
            sp.GetRequiredService<IOptions<CacheOptions>>(),
            sp.GetService<IRedisConnector>() ?? throw new InvalidOperationException(
                "AddDistributedCache with a Redis-backed provider requires a Redis connection. Call AddRedisConnection on the caching builder."),
            new RedisValueSerializerProxy(sp.GetRequiredService<ISerializerProxy<byte[]>>()),
            sp.GetRequiredService<IResiliencePipelineProvider>(),
            sp.GetRequiredService<ICachingTelemetryProvider>(),
            sp.GetRequiredService<ILoggerFactory>(),
            sp.GetRequiredService<ICachePolicyFactory>());
}
