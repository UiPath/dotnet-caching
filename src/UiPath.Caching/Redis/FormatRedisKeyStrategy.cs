namespace UiPath.Caching.Redis;

internal sealed class FormatRedisKeyStrategy(RedisKeyFormat format, string app, string keyspace) : IRedisKeyStrategy
{
    private readonly RedisKey _prefix = format.Prefix(app, keyspace);

    public RedisKeyFormat Format { get; } = format;

    public RedisKey GetRedisKey(CacheKey key) =>
        Format.BuildName(_prefix, key.Name);
}
