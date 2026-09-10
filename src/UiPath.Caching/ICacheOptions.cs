namespace UiPath.Caching;

public interface ICacheOptions
{
    bool Enabled { get; }

    TimeSpan? DefaultExpiration { get; }

    TimeSpan Timeout { get; set; }

    ICacheEntryFactory? EntryFactory { get; set; }

    ICacheKeyStrategy? CacheKeyStrategy { get; set; }

    bool? ConnectionMonitorEnabled { get; set; }

    /// <summary>
    /// When true, <c>GetOrAddAsync</c> caches a generator's null / empty result instead of re-invoking the
    /// generator on every call; explicit <c>SetAsync(key, null)</c> and <c>SetAsync(key, empty)</c>
    /// likewise persist the sentinel instead of removing the entry. Default false preserves legacy
    /// behavior for callers that haven't opted in.
    /// </summary>
    bool CacheNullValues { get => false; set => _ = value; }
}
