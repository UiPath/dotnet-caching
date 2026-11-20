namespace UiPath.Caching.Redis;

internal sealed partial class RedisHashCache
{
    private RedisKey ToRedisKey(CacheKey cacheKey, CancellationToken token = default)
    {
        if (cacheKey.IsNull)
        {
            throw new ArgumentNullException(nameof(cacheKey));
        }
        token.ThrowIfCancellationRequested();
        return _redisKeyStrategy.GetRedisKey(cacheKey);
    }

    private void AuditKeySize(LoggedKey key, HashEntry[] entries)
    {
        if (!_logger.IsEnabled(LogLevel.Warning))
        {
            return;
        }

        foreach (var entry in entries)
        {
            AuditValue(key, entry.Name, entry.Value);
        }
    }

    private void AuditKeySize(LoggedKey key, RedisValue field, RedisValue value)
    {
        if (_logger.IsEnabled(LogLevel.Warning))
        {
            AuditValue(key, field, value);
        }
    }

    private void AuditValue(LoggedKey key, RedisValue field, RedisValue value)
    {
        var valueLen = value.Length();
        if (valueLen > _cacheOptions.LargeValueThreshold)
        {
            LogLargeValueDetected(key, field.ToString(), valueLen);
        }
    }

    private ICacheEntry<IDictionary<string, T?>> Default<T>() => _cacheEntryFactory.Create(Empty<T?>(), DateTimeOffset.MinValue);
}
