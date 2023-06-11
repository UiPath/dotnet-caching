using Microsoft.Extensions.Internal;

namespace UiPath.Platform.Caching.Memory;

public interface ICacheOptions
{
    public bool Enabled { get;  }

    public TimeSpan? DefaultExpiration { get; }

    public TimeSpan Timeout { get; set; }

    public ISystemClock? Clock { get; set; }

    public ICacheEntryFactory? EntryFactory { get; set; }
}
