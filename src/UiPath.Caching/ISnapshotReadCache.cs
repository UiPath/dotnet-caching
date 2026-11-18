namespace UiPath.Caching;

/// <summary>A cache that reads a value and its remaining lifetime from the primary as one snapshot, throws where a plain read answers a miss, and leaves an unreadable value in place.</summary>
public interface ISnapshotReadCache
{
    ValueTask<ICacheEntry<T?>> ReadSnapshotAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default);
}
