namespace UiPath.Caching.Config;

[ExcludeFromCodeCoverage]
public static class SetCacheCollectionExtensions
{
    public static ICachingBuilder AddRedisSetCache(this ICachingBuilder builder) =>
        builder.AddRedisSetCache(static _ => { });

    public static ICachingBuilder AddRedisSetCache(this ICachingBuilder builder, Action<RedisSetCacheOptions> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configureOptions);
        if (!builder.Enabled)
        {
            return builder;
        }
        builder.Services.AddRedisSetCache(configureOptions);
        return builder;
    }

    /// <inheritdoc cref="AddRedisSetCache(IServiceCollection, Action{RedisSetCacheOptions})"/>
    public static IServiceCollection AddRedisSetCache(this IServiceCollection services) =>
        services.AddRedisSetCache(static _ => { });

    /// <summary>
    /// Registers <see cref="ISetCache"/> and <see cref="ISetCache{T}"/> on the service collection.
    /// </summary>
    /// <remarks>
    /// Prerequisite: the core caching and Redis services must already be registered (via
    /// <c>AddCaching(... builder.AddRedis())</c>). The set cache resolves <see cref="IRedisConnector"/>,
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
        services.Configure(configureOptions);
        services.TryAddSingleton<ISetCache>(BuildRedisSetCache);
        services.TryAddSingleton<IQueueCacheFactory, QueueCacheFactory>();
        services.TryAddTransient(typeof(ISetCache<>), typeof(SetCache<>));
        return services;
    }

    private static RedisSetCache BuildRedisSetCache(IServiceProvider sp) =>
        new(
            sp.GetRequiredService<IRedisConnector>(),
            sp.GetRequiredService<ISerializerProxy<RedisValue>>(),
            sp.GetRequiredService<IResiliencePipelineProvider>(),
            sp.GetService<ICachingTelemetryProvider>() ?? NullTelemetryProvider.Instance,
            sp.GetRequiredService<IOptions<RedisCacheOptions>>().Value,
            sp.GetRequiredService<IOptions<CacheOptions>>().Value,
            sp.GetRequiredService<IOptions<RedisSetCacheOptions>>().Value,
            sp.GetRequiredService<ICachePolicyFactory>(),
            (sp.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance).CreateLogger<RedisSetCache>());
}
