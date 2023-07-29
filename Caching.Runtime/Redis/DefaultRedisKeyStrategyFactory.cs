namespace UiPath.Platform.Caching.Redis;

public sealed class DefaultRedisKeyStrategyFactory : IRedisKeyStrategyFactory
{
    public IRedisKeyStrategy Create(CacheOptions options, Type cacheType)
    {
        string typePrefix;

        if (typeof(ICache).IsAssignableFrom(cacheType))
        {
            typePrefix = RedisTypePrefixes.String;
        }
        else if (typeof(IHashCache).IsAssignableFrom(cacheType))
        {
            typePrefix = RedisTypePrefixes.Hash;
        }
        else
        {
            throw new ArgumentException($"Cache type {cacheType} is not supported by {nameof(DefaultRedisKeyStrategyFactory)}");
        }

        var separator = Guard.NotWhiteSpace(options.Separator, nameof(options.Separator));
        var prefix = string.Join(separator, Guard.NotNullOrWhiteSpace(options.AppShortName, nameof(options.AppShortName)), typePrefix);
        return new PrefixRedisKeyStrategy(prefix, separator);
    }
}
