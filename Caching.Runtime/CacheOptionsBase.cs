using UiPath.Platform.Caching.Memory;

namespace UiPath.Platform.Caching;

public abstract class CacheOptionsBase : ICacheOptions
{
    public bool Enabled { get; set; }

    public TimeSpan? DefaultExpiration { get; set; } = TimeSpan.FromMinutes(15);

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(1);

    public ISystemClock? Clock { get; set; }

    public ICacheEntryFactory? EntryFactory { get; set; }
}
