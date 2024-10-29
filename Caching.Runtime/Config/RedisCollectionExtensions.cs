using StackExchange.Redis.Profiling;

namespace UiPath.Platform.Caching.Config;

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
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<ICacheProvider, RedisCacheProvider>());
        return builder;
    }

    public static ICachingBuilder AddRedisConnection(this ICachingBuilder builder, string sectionName = Connections) =>
        builder.AddRedisConnection(opt => builder.Configuration.GetSection(sectionName).Bind(opt));

    public static ICachingBuilder AddRedisConnection(this ICachingBuilder builder, Action<RedisConnectionOptions> configureOptions)
    {
        RedisConnectionOptions redisConnectionOptions = new RedisConnectionOptions();
        configureOptions(redisConnectionOptions);
        builder.Services.Configure(configureOptions);

        builder.Services.TryAddTransient(sp => CreateRedisConfiguration(sp));

        builder.Services.TryAddTransient<Func<IConnectionMultiplexer>>(sp => () => CreateMultiplexer(sp, redisConnectionOptions));

        if (redisConnectionOptions.ProfilerEnabled)
        {
            builder.Services.TryAddSingleton<IProfiledCommandProcessor, ProfiledCommandProcessor>();
            builder.Services.TryAddSingleton<IProfilingSessionCommandReader, ProfilingSessionCommandReader>();
            builder.Services.TryAddSingleton<IRedisProfiler, RedisProfiler>();
        }
        else
        {
            builder.Services.TryAddSingleton<IRedisProfiler>(sp => NullRedisProfiler.Instance);
        }

        if (redisConnectionOptions.PlannedMaintenanceEnabled)
        {
            builder.Services.TryAddSingleton<IRedisPlannedMaintenance, RedisPlannedMaintenance>();
        }

        builder.Services.TryAddSingleton<IRedisConnector, RedisConnector>();
        return builder;
    }

    internal static ConfigurationOptions CreateRedisConfiguration(IServiceProvider sp)
    {
        var options = sp.GetRequiredService<IOptions<RedisConnectionOptions>>().Value;
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            return new ConfigurationOptions();
        }

        var config = options.CreateConfigurationOptions();

        config.LoggerFactory = sp.GetService<ILoggerFactory>();

        return config;
    }

    private static ConnectionMultiplexer CreateMultiplexer(IServiceProvider sp, RedisConnectionOptions redisConnectionOptions)
    {
        var options = sp.GetRequiredService<ConfigurationOptions>();
        var cnn = ConnectionMultiplexer.Connect(options);
        if (redisConnectionOptions.ProfilerEnabled)
        {
            var factory = ProfilingSessionFactory(sp, redisConnectionOptions);
            cnn.RegisterProfiler(factory);
        }

        return cnn;
    }

    public static Func<ProfilingSession?> ProfilingSessionFactory(IServiceProvider sp, RedisConnectionOptions redisConnectionOptions)
    {
        if (redisConnectionOptions.ProfilingSessionFactory != null)
        {
            return redisConnectionOptions.ProfilingSessionFactory;
        }

        var profiler = sp.GetService<IRedisProfiler>();

        if (profiler == null)
        {
            return static () => default;
        }

        return () => profiler.GetSession();

    } 
}
