namespace UiPath.Caching.Migration;

/// <summary>The library side of the router.</summary>
internal sealed class CacheLibraryStore(ICache cache) : ILibraryStore
{
    private readonly IMigrationCache _migration = cache.AsMigrationCache();

    public ValueTask<ICacheEntry<T?>> ReadSnapshotAsync<T>(CacheKey key, CancellationToken cancellationToken) =>
        _migration.ReadSnapshotAsync<T>(key, null, cancellationToken);

    public ValueTask<bool> SetAsync<T>(CacheKey key, T value, DateTimeOffset expiration, CancellationToken cancellationToken) =>
        cache.SetAsync(key, value, expiration, null, cancellationToken);

    public ValueTask<bool> RemoveAsync<T>(CacheKey key, CancellationToken cancellationToken) =>
        cache.RemoveAsync<T>(key, cancellationToken);
}
