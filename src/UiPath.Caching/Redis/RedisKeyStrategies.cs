namespace UiPath.Caching.Redis;

/// <summary>The strategy of a Redis component: its key format when one is set, otherwise the configured factory or the default one.</summary>
internal static class RedisKeyStrategies
{
    public static IRedisKeyStrategy Resolve(RedisCacheOptions redisOptions, CacheOptions options, Type cacheType, RedisKeyFormat? format = null)
    {
        if (format is null && redisOptions.KeyFormat is null)
        {
            return (redisOptions.RedisKeyStrategyFactory ?? new DefaultRedisKeyStrategyFactory()).Create(options, cacheType);
        }

        if (typeof(ICache).IsAssignableFrom(cacheType))
        {
            return Resolve(redisOptions, options, RedisKeyspaces.String, format);
        }

        if (typeof(IHashCache).IsAssignableFrom(cacheType))
        {
            return Resolve(redisOptions, options, RedisKeyspaces.Hash, format);
        }

        throw new ArgumentException($"Cache type {cacheType} is not supported by {nameof(RedisCacheOptions.KeyFormat)}.", nameof(cacheType));
    }

    public static IRedisKeyStrategy Resolve(RedisCacheOptions redisOptions, CacheOptions options, string keyspace, RedisKeyFormat? format = null)
    {
        format ??= redisOptions.KeyFormat;
        if (format is null)
        {
            return (redisOptions.RedisKeyStrategyFactory ?? new DefaultRedisKeyStrategyFactory()).Create(options, keyspace);
        }

        if (redisOptions.RedisKeyStrategyFactory is not null)
        {
            throw new InvalidOperationException(
                $"{nameof(RedisCacheOptions)}.{nameof(RedisCacheOptions.KeyFormat)} cannot be combined with a custom {nameof(RedisCacheOptions.RedisKeyStrategyFactory)}.");
        }

        return new FormatRedisKeyStrategy(
            format,
            Guard.NotNullOrWhiteSpace(options.AppShortName, nameof(options.AppShortName)),
            Guard.NotNullOrWhiteSpace(keyspace, nameof(keyspace)));
    }
}
