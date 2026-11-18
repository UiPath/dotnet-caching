namespace UiPath.Caching.Migration;

internal sealed class CacheLibraryStore(ICache cache, ISnapshotReadCache snapshot) : ILibraryStore
{
    public ValueTask<ICacheEntry<T?>> ReadSnapshotAsync<T>(CacheKey key, CancellationToken cancellationToken) =>
        snapshot.ReadSnapshotAsync<T>(key, null, cancellationToken);

    public ValueTask<bool> SetAsync<T>(CacheKey key, T value, DateTimeOffset expiration, CancellationToken cancellationToken) =>
        cache.SetAsync(key, value, expiration, null, cancellationToken);

    public ValueTask<bool> RemoveAsync<T>(CacheKey key, CancellationToken cancellationToken) =>
        cache.RemoveAsync<T>(key, cancellationToken);
}
