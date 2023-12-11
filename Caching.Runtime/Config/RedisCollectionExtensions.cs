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


    public static ICachingBuilder AddRedisConnection(this ICachingBuilder builder, string sectionName = Connections)
    {
        return builder.AddRedisConnection(opt => builder.Configuration.GetSection(sectionName).Bind(opt));
    }

    public static ICachingBuilder AddRedisConnection(this ICachingBuilder builder, Action<RedisConnectionOptions> configureOptions)
    {
        RedisConnectionOptions redisConnectionOptions = new RedisConnectionOptions();
        configureOptions(redisConnectionOptions);
        builder.Services.Configure(configureOptions);

        builder.Services.TryAddTransient(sp =>
        {
            var options = sp.GetRequiredService<IOptions<RedisConnectionOptions>>().Value;
            if(string.IsNullOrWhiteSpace(options.ConnectionString))
            {
                return new ConfigurationOptions();
            }

            var config = ConfigurationOptions.Parse(options.ConnectionString);
            config.AbortOnConnectFail = false; // if the connection fails, the multiplexer will silently retry in the background
            config.ChannelPrefix = default;
            if (options.BackOffMilliseconds > 0)
            {
                config.ReconnectRetryPolicy = new ExponentialRetry(options.BackOffMilliseconds);
            }

            if (options.HeartbeatInterval.HasValue)
            {
                config.HeartbeatInterval = options.HeartbeatInterval.Value;
            }

            config.LoggerFactory = sp.GetService<ILoggerFactory>();

            return config;
        });

        builder.Services.TryAddTransient<Func<IConnectionMultiplexer>>(sp => () => {
            var options = sp.GetRequiredService<ConfigurationOptions>();
            var cnn = ConnectionMultiplexer.Connect(options);
            if (redisConnectionOptions.ProfilerEnabled)
            {
                var profiler = sp.GetService<IRedisProfiler>();
                if (profiler != null)
                {
                    cnn.RegisterProfiler(profiler.GetSession);
                }
            }

            return cnn;
        });

        if (redisConnectionOptions.ProfilerEnabled)
        {
            builder.Services.TryAddSingleton<IRedisProfiler, RedisProfiler>();
        }

        if (redisConnectionOptions.PlannedMaintenanceEnabled)
        {
            builder.Services.TryAddSingleton<IRedisPlannedMaintenance, RedisPlannedMaintenance>();
        }

        builder.Services.TryAddSingleton<IRedisConnector, RedisConnector>();
        return builder;
    }
}
