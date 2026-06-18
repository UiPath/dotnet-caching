namespace UiPath.Caching;

public interface ICacheOptions
{
    public bool Enabled { get; }

    public TimeSpan? DefaultExpiration { get; }

    public TimeSpan Timeout { get; set; }

    public ISystemClock? Clock { get; set; }

    public ICacheEntryFactory? EntryFactory { get; set; }

    public ICacheKeyStrategy? CacheKeyStrategy { get; set; }

    public bool? ConnectionMonitorEnabled { get; set; }

    /// <summary>
    /// When true, <c>GetOrAddAsync</c> caches a generator's null / empty result instead of re-invoking the
    /// generator on every call; explicit <c>SetAsync(key, null)</c> and <c>SetAsync(key, empty)</c>
    /// likewise persist the sentinel instead of removing the entry. Default false preserves legacy
    /// behavior for callers that haven't opted in.
    /// </summary>
    public bool CacheNullValues { get => false; set => _ = value; }
}
