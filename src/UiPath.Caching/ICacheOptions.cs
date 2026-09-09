namespace UiPath.Caching;

public interface ICacheOptions
{
    public bool Enabled { get; }

    public TimeSpan? DefaultExpiration { get; }

    public TimeSpan Timeout { get; set; }

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

    /// <summary>
    /// Mask keys in log lines. Off, keys are logged in full. On, a key that is a number or a GUID is still
    /// logged in full; any other key that starts with one of <see cref="MaskedKeyPrefixes"/> keeps that prefix
    /// and shows only its first three characters (<c>myapp:s:session:cos****</c>). <c>AddDistributedCache</c>
    /// turns it on for its own provider, with its own prefix listed.
    /// </summary>
    public bool MaskKeys { get => false; set => _ = value; }

    /// <summary>Key prefixes under which keys are secrets, compared case-insensitively; an empty entry matches every key.</summary>
    public IReadOnlyList<string> MaskedKeyPrefixes { get => []; }
}
