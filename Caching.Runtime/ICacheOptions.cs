namespace UiPath.Platform.Caching;

public interface ICacheOptions
{
    public bool Enabled { get; }

    public TimeSpan? DefaultExpiration { get; }

    public TimeSpan Timeout { get; set; }

    public ISystemClock? Clock { get; set; }

    public ICacheEntryFactory? EntryFactory { get; set; }

    public ICacheKeyStrategy? CacheKeyStrategy { get; set; }
}
