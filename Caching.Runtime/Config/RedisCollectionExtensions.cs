namespace UiPath.Platform.Caching.Config;

[ExcludeFromCodeCoverage]
public static class RedisCollectionExtensions
{
    private const string DefaultSectionName = "RedisCache";

    public static ICachingBuilder AddRedis(this ICachingBuilder builder, string sectionName = DefaultSectionName) =>
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
            });
        }
        
        return builder
            .AddRedisCache(options, options.IsDefault)
            .AddRedisRegionCache(options, options.IsDefault);
    }

    public static ICachingBuilder AddRedisCache(this ICachingBuilder builder, RedisCacheOptions? options = null, bool isDefault = false)
    {
        builder.AddCache(sp =>
        {
            var config = options ?? sp.GetService<IOptions<RedisCacheOptions>>()?.Value ?? new RedisCacheOptions();
            return builder.Enabled && config.Enabled ? sp.BuildRedisCache(config) : NullCache.Instance;
        });


        builder.Services.TryAddTransient(typeof(IRedisCache<>), typeof(RedisCache<>));

        if (isDefault)
        {
            builder.Services.TryAddTransient<ICache>(sp => sp.GetRequiredService<IRedisCache>());
            builder.Services.TryAddTransient(typeof(ICache<>), typeof(RedisCache<>));
        }
        return builder;
    }

    public static ICachingBuilder AddRedisRegionCache(this ICachingBuilder builder, RedisCacheOptions? options = null, bool isDefault = false)
    {
        builder.AddRegionCache(sp =>
        {
            var config = options ?? sp.GetService<IOptions<RedisCacheOptions>>()?.Value ?? new RedisCacheOptions();
            return builder.Enabled && config.Enabled ? sp.BuildRedisRegionCache(config) : NullRegionCache.Instance;
        });

        builder.Services.TryAddTransient(typeof(IRedisRegionCache<>), typeof(RedisHashSetCache<>));

        if (isDefault)
        {
            builder.Services.TryAddTransient<IRegionCache>(sp => sp.GetRequiredService<IRedisRegionCache>());
            builder.Services.TryAddTransient(typeof(ICache<>), typeof(RedisHashSetCache<>));
        }
        return builder;
    }

    public static ICachingBuilder ConfigureRedis(this ICachingBuilder builder, Action<RedisCacheOptions> configureOptions)
    {
        RedisCacheOptions options = new();
        configureOptions.Invoke(options);
        builder.Services.Configure(configureOptions);
        return builder;
    }

    public static ICachingBuilder AddRedisConnection(this ICachingBuilder builder, Action<RedisConnectionOptions> configureOptions)
    {
        RedisConnectionOptions redisConnectionOptions = new RedisConnectionOptions();
        configureOptions(redisConnectionOptions);
        builder.Services.Configure(configureOptions);

        builder.Services.TryAddTransient<Func<ConfigurationOptions, IConnectionMultiplexer>>(sp => (ConfigurationOptions options) => {
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
