namespace UiPath.Platform.Caching.Redis;

public class RedisCacheOptions : ICacheOptions
{
    public bool Enabled { get; set; } = true;

    public TimeSpan? DefaultExpiration { get; set; } = TimeSpan.FromHours(1);

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(1);

    public ISystemClock? Clock { get; set; }

    public ICacheEntryFactory? EntryFactory { get; set; }

    public ICacheKeyStrategy? CacheKeyStrategy { get; set; }

    public IRedisKeyStrategyFactory? RedisKeyStrategyFactory { get; set; }

    public bool? ConnectionMonitorEnabled { get; set; }
}
