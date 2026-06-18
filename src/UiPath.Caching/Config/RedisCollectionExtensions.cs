namespace UiPath.Caching.Config;

[ExcludeFromCodeCoverage]
public static class RedisCollectionExtensions
{
    public const string Connections = "Connections:Redis";

    public static ICachingBuilder AddRedis(this ICachingBuilder builder, string sectionName = KnownCacheProviderNames.Redis)
    {
        return builder.AddRedis(opt => builder.Configuration.GetSection(sectionName).Bind(opt));
    }

    public static ICachingBuilder AddRedis(this ICachingBuilder builder,  Action<RedisCacheOptions> configure)
    {
        var options = new RedisCacheOptions();
        configure(options);
        builder.Services.Configure(configure);
        if (!builder.Enabled || !options.Enabled)
        {
            return builder;
        }
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<ICacheProvider, RedisCacheProvider>());
        return builder;
    }

    public static ICachingBuilder AddRedisConnection(this ICachingBuilder builder, string sectionName = Connections) =>
        builder.AddRedisConnection(opt => builder.Configuration.GetSection(sectionName).Bind(opt));

    public static ICachingBuilder AddRedisConnection(this ICachingBuilder builder, Action<RedisConnectionOptions> configure)
    {
        RedisConnectionOptions redisConnectionOptions = new RedisConnectionOptions();
        configure(redisConnectionOptions);
        builder.Services.Configure(configure);
        builder.AddRedisConnection(redisConnectionOptions);
        return builder;
    }

    public static ICachingBuilder AddRedisConnection(this ICachingBuilder builder, string sectionName, Action<RedisConnectionOptions> configure)
    {
        void configureOptions(RedisConnectionOptions opt)
        {
            builder.Configuration.GetSection(sectionName).Bind(opt);
            configure(opt);
        }

        RedisConnectionOptions redisConnectionOptions = new RedisConnectionOptions();
        configureOptions(redisConnectionOptions);
        builder.Services.Configure((Action<RedisConnectionOptions>)configureOptions);
        builder.AddRedisConnection(redisConnectionOptions);
        return builder;
    }

    private static ICachingBuilder AddRedisConnection(this ICachingBuilder builder, RedisConnectionOptions redisConnectionOptions)
    {
        builder
            .AddRedisProfiler(redisConnectionOptions.ProfilerEnabled)
            .AddIRedisPlannedMaintenance(redisConnectionOptions.PlannedMaintenanceEnabled);
        builder.Services.TryAddSingleton<IRedisConnector, RedisConnector>();
        builder.Services.TryAddTransient<IRedisConfigurationOptionsProvider, RedisConfigurationOptionsProvider>();
        var factoryImplementationType = typeof(ConnectionMultiplexerFactory);
        if (!string.IsNullOrWhiteSpace(redisConnectionOptions.ConnectionMultiplexerFactoryType))
        {
            try
            {
                var tempType = Type.GetType(redisConnectionOptions.ConnectionMultiplexerFactoryType);
                if (tempType is not null && typeof(IConnectionMultiplexerFactory).IsAssignableFrom(tempType))
                {
                    factoryImplementationType = tempType;
                }
            }
            catch
            {
                // ignored
            }
        }

        builder.Services.TryAddTransient(typeof(IConnectionMultiplexerFactory), factoryImplementationType);
        return builder;
    }

    public static ICachingBuilder AddRedisProfiler(this ICachingBuilder builder, bool enabled)
    {
        if (enabled)
        {
            builder.Services.TryAddSingleton<IProfiledCommandProcessor, ProfiledCommandProcessor>();
            builder.Services.TryAddSingleton<IProfilingSessionCommandReader, ProfilingSessionCommandReader>();
            builder.Services.TryAddSingleton<IRedisProfiler, RedisProfiler>();
        }
        else
        {
            builder.Services.TryAddSingleton<IRedisProfiler>(sp => NullRedisProfiler.Instance);
        }
        return builder;
    }

    public static ICachingBuilder AddIRedisPlannedMaintenance(this ICachingBuilder builder, bool enabled)
    {
        if (enabled)
        {
            builder.Services.TryAddSingleton<IRedisPlannedMaintenance, RedisPlannedMaintenance>();
        }
        return builder;
    }
}
