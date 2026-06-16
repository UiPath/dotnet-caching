namespace UiPath.Platform.Caching.Config;

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

    public static IServiceCollection AddRedisSetCache(this IServiceCollection services) =>
        services.AddRedisSetCache(static _ => { });

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
