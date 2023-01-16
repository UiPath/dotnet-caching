using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using UiPath.Platform.Caching.Redis;

namespace UiPath.Platform.Caching.Config;

public static class RedisCollectionExtensions
{
    public static ICachingBuilder UseRedis(this ICachingBuilder builder, Action<RedisConnectionOptions> configureOptions)
    {
        RedisConnectionOptions redisConnectionOptions = new RedisConnectionOptions();
        configureOptions(redisConnectionOptions);
        builder.Services.Configure(configureOptions);
        builder.Services.TryAddTransient<Func<ConfigurationOptions, IConnectionMultiplexer>>(sp => (ConfigurationOptions options) => ConnectionMultiplexer.Connect(options));
        builder.Services.TryAddSingleton<IRedisConnection, RedisConnection>();
        builder.Services.TryAddTransient<Func<IDatabase>>(sp => () => sp.GetRequiredService<IRedisConnection>().Connection.GetDatabase());
        return builder;
    }

    public static ICachingBuilder UseRedis(this ICachingBuilder builder, Func<IServiceProvider, IDatabase> databaseFactory)
    {
        if (builder.Enabled)
        {
            builder.Services.TryAddTransient<Func<IDatabase>>(sp => () => databaseFactory(sp));
        }

        return builder;
    }

    public static ICachingBuilder AddRedisCache(this ICachingBuilder builder, RedisCacheOptions? options = null, bool isDefault = false)
    {
        if (builder.Enabled)
        {
            builder.AddCache(sp => sp.BuildRedisCache(options));
        }
        else
        {
            builder.AddCache(sp => (IRedisCache)NullCache.Instance);
        }

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
        if (builder.Enabled)
        {
            builder.AddRegionCache(sp => sp.BuildRedisRegionCache(options));
        }
        else
        {
            builder.AddRegionCache(sp => (IRedisRegionCache)NullRegionCache.Instance);
        }

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
}
