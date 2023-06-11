namespace UiPath.Platform.Caching.Config;

[ExcludeFromCodeCoverage]
public static class RedisCollectionExtensions
{
    public static ICachingBuilder AddRedis(this ICachingBuilder builder, string sectionName = KnownCacheProviderNames.Redis) =>
        builder.AddRedis(opt => builder.Configuration.GetSection(sectionName).Bind(opt));

    public static ICachingBuilder AddRedis(this ICachingBuilder builder,  Action<RedisCacheOptions> configure)
    {
        var options = new RedisCacheOptions();
        configure(options);
        builder.Services.Configure(configure);
        if (builder.Enabled && options.Enabled && !string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            builder.AddRedisConnection(opt =>
            {
                opt.ConnectionString = options.ConnectionString;
                opt.ProfilerEnabled = options.ProfilerEnabled;
                opt.HeartbeatInterval = options.HeartbeatInterval;
                opt.BackOffMilliseconds = options.BackOffMilliseconds;
            })
           .AddRedisPubSub()
           .AddRedisStreams();
        }

        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<ICacheProvider, RedisCacheProvider>());

        return builder;
    }

    public static ICachingBuilder AddRedisConnection(this ICachingBuilder builder, Action<RedisConnectionOptions> configureOptions)
    {
        RedisConnectionOptions redisConnectionOptions = new RedisConnectionOptions();
        configureOptions(redisConnectionOptions);
        builder.Services.Configure(configureOptions);

        builder.Services.TryAddTransient(sp =>
        {
            var options = sp.GetRequiredService<IOptions<RedisConnectionOptions>>().Value;
            var config = ConfigurationOptions.Parse(options.ConnectionString);
            config.AbortOnConnectFail = false; // if the connection fails, the multiplexer will silently retry in the background
            config.ChannelPrefix = default;
            if (options.BackOffMilliseconds.HasValue)
            {
                config.ReconnectRetryPolicy = new ExponentialRetry(options.BackOffMilliseconds.Value);
            }

            if (options.HeartbeatInterval.HasValue)
            {
                config.HeartbeatInterval = options.HeartbeatInterval.Value;
            }

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

        if(redisConnectionOptions.ProfilerEnabled)
        {
            builder.Services.TryAddSingleton<IRedisProfiler, RedisProfiler>();
        }

        builder.Services.TryAddSingleton<IRedisConnection, RedisConnection>();
        builder.Services.TryAddTransient<Func<IDatabase>>(sp => () => sp.GetRequiredService<IRedisConnection>().Connection.GetDatabase());
        return builder;
    }

}
