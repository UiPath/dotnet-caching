namespace UiPath.Caching;

/// <summary>For code that moves cache data between stores: snapshot reads from the primary that throw on a store fault.</summary>
public interface IMigrationCache
{
    ValueTask<ICacheEntry<T?>> ReadSnapshotAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default);
}
