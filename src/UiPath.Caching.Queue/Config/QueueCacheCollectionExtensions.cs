using Microsoft.Extensions.Configuration;
using UiPath.Caching.Config;

namespace UiPath.Caching.Queue.Config;

/// <summary>
/// Per-backing registration for the queue-package caches, mirroring the core's
/// <c>AddMemory</c> / <c>AddRedis</c> / <c>AddInMemoryRedis</c>: one method per backing, each
/// registering a single <see cref="IQueueCacheProvider"/> that serves every collection kind (sets
/// today; further kinds such as lists plug into the same provider). Each method also registers
/// <see cref="IQueueCacheFactory"/>, <see cref="ISetCache"/> and <see cref="ISetCache{T}"/>, and
/// degrades all of them to no-ops when caching or the backing is disabled.
/// </summary>
[ExcludeFromCodeCoverage]
public static class QueueCacheCollectionExtensions
{
    public static ICachingBuilder AddQueueMemory(this ICachingBuilder builder) =>
        builder.AddQueueMemory(KnownCacheProviderNames.InMemory);

    public static ICachingBuilder AddQueueMemory(this ICachingBuilder builder, string sectionName) =>
        builder.AddQueueMemory(opt => builder.Configuration.GetSection(sectionName).Bind(opt));

    /// <summary>
    /// Registers the in-process backing (<c>InMemory</c>). Requires the core caching services
    /// (<c>AddCaching</c>). Unlike the Redis backings, this one has no Redis prerequisite. The
    /// factory selects providers via <see cref="CacheOptions.DefaultCache"/> (default
    /// <c>InMemoryRedis</c>, like the core cache factory) — point it at <c>InMemory</c> when this is
    /// the only registered queue-cache backing.
    /// </summary>
    public static ICachingBuilder AddQueueMemory(this ICachingBuilder builder, Action<InMemoryQueueCacheOptions> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configureOptions);
        var options = new InMemoryQueueCacheOptions();
        configureOptions(options);
        builder.Services.TryConfigure(configureOptions);
        if (!builder.Enabled || !options.Enabled)
        {
            builder.Services.AddNullQueueCache();
            return builder;
        }

        builder.Services.TryAddMemoryCacheFactory();
        builder.AddLocalLock();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IQueueCacheProvider, InMemoryQueueCacheProvider>());
        builder.Services.AddQueueCacheCore();
        return builder;
    }

    public static ICachingBuilder AddQueueRedis(this ICachingBuilder builder) =>
        builder.AddQueueRedis(KnownCacheProviderNames.Redis);

    public static ICachingBuilder AddQueueRedis(this ICachingBuilder builder, string sectionName) =>
        builder.AddQueueRedis(opt => builder.Configuration.GetSection(sectionName).Bind(opt));

    /// <summary>Registers the Redis backing (<c>Redis</c>).</summary>
    /// <remarks>
    /// Prerequisite: the core caching and Redis services must already be registered (via
    /// <c>AddCaching(... builder.AddRedis())</c>). The provider resolves <see cref="IRedisConnector"/>,
    /// <see cref="ISerializerProxy{RedisValue}"/>, <see cref="IResiliencePipelineProvider"/>,
    /// <see cref="ICachingTelemetryProvider"/>, <see cref="ILoggerFactory"/>, the core cache options
    /// and <see cref="ICachePolicyFactory"/> from the container — the same set as the core
    /// <c>RedisCacheProvider</c>; if <c>AddCaching()</c> has not run, resolving
    /// <see cref="ISetCache"/> throws at first use.
    /// </remarks>
    public static ICachingBuilder AddQueueRedis(this ICachingBuilder builder, Action<RedisSetCacheOptions> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configureOptions);
        var options = new RedisSetCacheOptions();
        configureOptions.Invoke(options);
        builder.Services.TryConfigure(configureOptions);
        if (!builder.Enabled || !options.Enabled)
        {
            builder.Services.AddNullQueueCache();
            return builder;
        }

        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IQueueCacheProvider, RedisQueueCacheProvider>());
        builder.Services.AddQueueCacheCore();
        return builder;
    }

    public static ICachingBuilder AddQueueInMemoryRedis(this ICachingBuilder builder) =>
        builder.AddQueueInMemoryRedis(KnownCacheProviderNames.InMemoryRedis);

    public static ICachingBuilder AddQueueInMemoryRedis(this ICachingBuilder builder, string sectionName) =>
        builder.AddQueueInMemoryRedis(opt => builder.Configuration.GetSection(sectionName).Bind(opt));

    /// <summary>
    /// Registers the multilayer backing (<c>InMemoryRedis</c>): a local in-process snapshot in front
    /// of the Redis set cache. Also registers the <c>Redis</c> backing, since the multilayer provider
    /// obtains its L2 tier from the factory
    /// (<see cref="IQueueCacheFactory.CreateSetCache(string)"/> for <c>Redis</c>). Same Redis
    /// prerequisites as <see cref="AddQueueRedis(ICachingBuilder, Action{RedisSetCacheOptions})"/>;
    /// the Redis tier reuses <see cref="RedisSetCacheOptions"/> / <see cref="RedisCacheOptions"/>.
    /// </summary>
    public static ICachingBuilder AddQueueInMemoryRedis(this ICachingBuilder builder, Action<InMemoryRedisQueueCacheOptions> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configureOptions);
        var options = new InMemoryRedisQueueCacheOptions();
        configureOptions.Invoke(options);
        builder.Services.TryConfigure(configureOptions);
        if (!builder.Enabled || !options.Enabled)
        {
            builder.Services.AddNullQueueCache();
            return builder;
        }

        builder.Services.TryAddMemoryCacheFactory();
        builder.AddLocalLock();
        // The multilayer provider resolves its Redis L2 through the factory, so the Redis provider
        // must be present. TryAddEnumerable makes this idempotent with an explicit AddQueueRedis.
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IQueueCacheProvider, RedisQueueCacheProvider>());
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IQueueCacheProvider, InMemoryRedisQueueCacheProvider>());
        builder.Services.AddQueueCacheCore();
        return builder;
    }
    
    private static IServiceCollection AddQueueCacheCore(this IServiceCollection services)
    {
        services.TryAddSingleton<IQueueCacheFactory>(sp =>
            new QueueCacheFactory(sp.GetRequiredService<IOptions<CacheOptions>>(), sp.GetServices<IQueueCacheProvider>()));
        // Deferred accessor so a provider can reach the factory without a construction-time cycle
        // (the factory is built from the providers). Mirrors the core's Func<ICacheFactory>.
        services.TryAddTransient<Func<IQueueCacheFactory>>(sp => () => sp.GetRequiredService<IQueueCacheFactory>());
        services.TryAddSingleton<ISetCache>(sp => sp.GetRequiredService<IQueueCacheFactory>().CreateSetCache());
        services.TryAddTransient(typeof(ISetCache<>), typeof(SetCache<>));
        return services;
    }

    private static IServiceCollection AddNullQueueCache(this IServiceCollection services)
    {
        services.TryAddSingleton<ISetCache>(NullSetCache.Instance);
        services.TryAddSingleton<IQueueCacheFactory>(NullQueueCacheFactory.Instance);
        services.TryAddTransient(typeof(ISetCache<>), typeof(SetCache<>));
        return services;
    }
}
