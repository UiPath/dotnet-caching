namespace UiPath.Caching.Redis;

public sealed class DefaultRedisKeyStrategyFactory : IRedisKeyStrategyFactory
{
    public IRedisKeyStrategy Create(CacheOptions options, Type cacheType)
    {
        string keyspace;

        if (typeof(ICache).IsAssignableFrom(cacheType))
        {
            keyspace = RedisKeyspaces.String;
        }
        else if (typeof(IHashCache).IsAssignableFrom(cacheType))
        {
            keyspace = RedisKeyspaces.Hash;
        }
        else
        {
            throw new ArgumentException($"Cache type {cacheType} is not supported by {nameof(DefaultRedisKeyStrategyFactory)}");
        }

        return Create(options, keyspace);
    }

    public IRedisKeyStrategy Create(CacheOptions options, string differentiator)
    {
        var keyspace = Guard.NotNullOrWhiteSpace(differentiator, nameof(differentiator));
        var separator = Guard.NotWhiteSpace(options.Separator, nameof(options.Separator));
        var prefix = string.Join(separator, Guard.NotNullOrWhiteSpace(options.AppShortName, nameof(options.AppShortName)), keyspace);
#pragma warning disable CS0618 // Still honored; see CacheOptions.ShardKeyEnabled.
        return options.ShardKeyEnabled ? new ShardPrefixRedisKeyStrategy(prefix, separator) : new PrefixRedisKeyStrategy(prefix, separator);
#pragma warning restore CS0618
    }
}
