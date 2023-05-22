namespace UiPath.Platform.Caching.Redis;

public abstract class CacheOptions
{
    public bool Enabled { get; set; }

    public bool IsDefault { get; set; }

    public TimeSpan? DefaultExpiration { get; set; } = TimeSpan.FromMinutes(15);

    public ISystemClock? Clock { get; set; }

    public ICacheEntryFactory? EntryFactory { get; set; }
}
