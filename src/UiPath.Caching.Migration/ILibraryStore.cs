namespace UiPath.Caching.Migration;

/// <summary>The library cache as the library path uses it: snapshot reads that throw on failure, and writes that answer a bool.</summary>
internal interface ILibraryStore
{
    ValueTask<ICacheEntry<T?>> ReadSnapshotAsync<T>(CacheKey key, CancellationToken cancellationToken);

    ValueTask<bool> SetAsync<T>(CacheKey key, T value, DateTimeOffset expiration, CancellationToken cancellationToken);

    ValueTask<bool> RemoveAsync<T>(CacheKey key, CancellationToken cancellationToken);
}
