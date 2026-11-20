using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Redis;

internal sealed partial class RedisCache
{
    private bool TryToRedisKey(CacheKey cacheKey, CancellationToken token, out RedisKey redisKey)
    {
        try
        {
            redisKey = ToRedisKey(cacheKey, token);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            redisKey = default;
            return false;
        }
    }

    private RedisKey ToRedisKey(CacheKey cacheKey, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (cacheKey.IsNull)
        {
            throw new ArgumentNullException(nameof(cacheKey));
        }

        return _redisKeyStrategy.GetRedisKey(cacheKey);
    }

    private TelemetryScope StartOperation<T>([CallerMemberName] string methodName = "") =>
        new(Telemetry, Clock, Name, methodName, typeof(T));

    private void TrackPerKey(TelemetryScope operation, (RedisKey Key, bool Hit)[] reads)
    {
        var anyHit = false;
        foreach (var (_, hit) in reads)
        {
            anyHit |= hit;
        }
        operation.Track(anyHit, reads.Length);

        if (KeyReadTelemetryEnabled && operation.IsEnabled)
        {
            var keyed = new (string Key, bool Hit)[reads.Length];
            for (int i = 0; i < reads.Length; i++)
            {
                keyed[i] = (reads[i].Key.ToString(), reads[i].Hit);
            }
            operation.TrackKeyReads(keyed);
        }
    }

    private void AuditKeySize(LoggedKey key, RedisValue value)
    {
        if (!_logger.IsEnabled(LogLevel.Warning))
        {
            return;
        }

        var valueLen = value.Length();
        if (valueLen > _largeValueThreshold)
        {
            LogLargeValueDetected(key, valueLen);
        }
    }
}
