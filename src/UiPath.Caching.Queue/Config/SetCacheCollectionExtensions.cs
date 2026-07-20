using UiPath.Caching.Config;

namespace UiPath.Caching.Queue.Config;

[ExcludeFromCodeCoverage]
public static class SetCacheCollectionExtensions
{
    public static ICachingBuilder AddInMemorySetCache(this ICachingBuilder builder) =>
        builder.AddInMemorySetCache(static _ => { });

    public static ICachingBuilder AddInMemorySetCache(this ICachingBuilder builder, Action<InMemorySetCacheOptions> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configureOptions);
        if (!builder.Enabled)
        {
            builder.Services.AddNullSetCache();
            return builder;
        }

        builder.Services.AddInMemorySetCache(configureOptions);
        return builder;
    }

    /// <inheritdoc cref="AddInMemorySetCache(IServiceCollection, Action{InMemorySetCacheOptions})"/>
    public static IServiceCollection AddInMemorySetCache(this IServiceCollection services) =>
        services.AddInMemorySetCache(static _ => { });

    /// <summary>
    /// Registers the in-process <see cref="ISetCache"/> provider (<c>InMemory</c>) plus
    /// <see cref="IQueueCacheFactory"/>, <see cref="ISetCache"/> and <see cref="ISetCache{T}"/>.
    /// Requires the core caching services (<c>AddCaching</c>). Unlike the Redis providers, this one
    /// has no Redis prerequisite.
    /// </summary>
    public static IServiceCollection AddInMemorySetCache(this IServiceCollection services, Action<InMemorySetCacheOptions> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configureOptions);
        var options = new InMemorySetCacheOptions();
        configureOptions.Invoke(options);
        services.TryConfigure(configureOptions);
        if (!options.Enabled)
        {
            return services.AddNullSetCache();
        }

        services.TryAddMemoryCacheFactory();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISetCacheProvider, InMemorySetCacheProvider>());
        return services.AddSetCacheCore();
    }

    public static ICachingBuilder AddRedisSetCache(this ICachingBuilder builder) =>
        builder.AddRedisSetCache(static _ => { });

    public static ICachingBuilder AddRedisSetCache(this ICachingBuilder builder, Action<RedisSetCacheOptions> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configureOptions);
        if (!builder.Enabled)
        {
            builder.Services.AddNullSetCache();
            return builder;
        }

        builder.Services.AddRedisSetCache(configureOptions);
        return builder;
    }

    /// <inheritdoc cref="AddRedisSetCache(IServiceCollection, Action{RedisSetCacheOptions})"/>
    public static IServiceCollection AddRedisSetCache(this IServiceCollection services) =>
        services.AddRedisSetCache(static _ => { });

    /// <summary>
    /// Registers the Redis-backed <see cref="ISetCache"/> provider (<c>Redis</c>) plus
    /// <see cref="IQueueCacheFactory"/>, <see cref="ISetCache"/> and <see cref="ISetCache{T}"/>.
    /// </summary>
    /// <remarks>
    /// Prerequisite: the core caching and Redis services must already be registered (via
    /// <c>AddCaching(... builder.AddRedis())</c>). The provider resolves <see cref="IRedisConnector"/>,
    /// <see cref="ISerializerProxy{RedisValue}"/>, <see cref="IResiliencePipelineProvider"/>, the core
    /// cache options and <see cref="ICachePolicyFactory"/> from the container; if <c>AddCaching()</c>
    /// has not run, resolving <see cref="ISetCache"/> throws at first use. Prefer the
    /// <see cref="AddRedisSetCache(ICachingBuilder, Action{RedisSetCacheOptions})"/> overload, which
    /// guarantees that ordering.
    /// </remarks>
    public static IServiceCollection AddRedisSetCache(this IServiceCollection services, Action<RedisSetCacheOptions> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configureOptions);
        var options = new RedisSetCacheOptions();
        configureOptions.Invoke(options);
        services.TryConfigure(configureOptions);
        if (!options.Enabled)
        {
            return services.AddNullSetCache();
        }

        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISetCacheProvider, RedisSetCacheProvider>());
        return services.AddSetCacheCore();
    }

    public static ICachingBuilder AddInMemoryRedisSetCache(this ICachingBuilder builder) =>
        builder.AddInMemoryRedisSetCache(static _ => { });

    public static ICachingBuilder AddInMemoryRedisSetCache(this ICachingBuilder builder, Action<InMemoryRedisSetCacheOptions> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configureOptions);
        if (!builder.Enabled)
        {
            builder.Services.AddNullSetCache();
            return builder;
        }

        builder.Services.AddInMemoryRedisSetCache(configureOptions);
        return builder;
    }

    /// <inheritdoc cref="AddInMemoryRedisSetCache(IServiceCollection, Action{InMemoryRedisSetCacheOptions})"/>
    public static IServiceCollection AddInMemoryRedisSetCache(this IServiceCollection services) =>
        services.AddInMemoryRedisSetCache(static _ => { });

    /// <summary>
    /// Registers the multilayer <see cref="ISetCache"/> provider (<c>InMemoryRedis</c>): a local
    /// in-process snapshot in front of the Redis set cache. Also registers the <c>Redis</c> provider,
    /// since the multilayer provider obtains its L2 tier from the factory
    /// (<see cref="IQueueCacheFactory.CreateSetCache(string)"/> for <c>Redis</c>). Same Redis
    /// prerequisites as <see cref="AddRedisSetCache(IServiceCollection, Action{RedisSetCacheOptions})"/>;
    /// the Redis tier reuses <see cref="RedisSetCacheOptions"/> / <see cref="RedisCacheOptions"/>.
    /// </summary>
    public static IServiceCollection AddInMemoryRedisSetCache(this IServiceCollection services, Action<InMemoryRedisSetCacheOptions> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configureOptions);
        var options = new InMemoryRedisSetCacheOptions();
        configureOptions.Invoke(options);
        services.TryConfigure(configureOptions);
        if (!options.Enabled)
        {
            return services.AddNullSetCache();
        }

        services.TryAddMemoryCacheFactory();
        // The multilayer provider resolves its Redis L2 through the factory, so the Redis provider
        // must be present. TryAddEnumerable makes this idempotent with an explicit AddRedisSetCache.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISetCacheProvider, RedisSetCacheProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISetCacheProvider, InMemoryRedisSetCacheProvider>());
        return services.AddSetCacheCore();
    }

    private static IServiceCollection AddSetCacheCore(this IServiceCollection services)
    {
        services.TryAddSingleton<IQueueCacheFactory>(sp =>
            new QueueCacheFactory(sp.GetServices<ISetCacheProvider>(), sp.GetRequiredService<IOptions<CacheOptions>>()));
        // Deferred accessor so a provider can reach the factory without a construction-time cycle
        // (the factory is built from the providers). Mirrors the core's Func<ICacheFactory>.
        services.TryAddTransient<Func<IQueueCacheFactory>>(sp => () => sp.GetRequiredService<IQueueCacheFactory>());
        services.TryAddSingleton<ISetCache>(sp => sp.GetRequiredService<IQueueCacheFactory>().CreateSetCache());
        services.TryAddTransient(typeof(ISetCache<>), typeof(SetCache<>));
        return services;
    }

    private static IServiceCollection AddNullSetCache(this IServiceCollection services)
    {
        services.TryAddSingleton<ISetCache>(NullSetCache.Instance);
        services.TryAddSingleton<IQueueCacheFactory>(NullQueueCacheFactory.Instance);
        services.TryAddTransient(typeof(ISetCache<>), typeof(SetCache<>));
        return services;
    }
}
