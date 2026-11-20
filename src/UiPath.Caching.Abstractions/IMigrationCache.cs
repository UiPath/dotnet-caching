namespace UiPath.Caching;

/// <summary>For code that moves cache data between stores: snapshot reads, writes and deletes against the primary that report what happened instead of throwing or answering a miss.</summary>
public interface IMigrationCache
{
    /// <summary>Value and remaining lifetime as one snapshot of the primary, bypassing local copies; an unreadable value is reported and left in place.</summary>
    ValueTask<SnapshotRead<T>> ReadSnapshotAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default);

    /// <summary>Announces like <c>SetAsync</c>; differs only in telling a write that was never sent from one whose result is unknown.</summary>
    ValueTask<StoreOutcome> WriteAsync<T>(CacheKey cacheKey, T? value, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default);

    /// <summary>Announces like <c>RemoveAsync</c>; differs only in telling a delete that was never sent from one whose result is unknown.</summary>
    ValueTask<StoreOutcome> DeleteAsync<T>(CacheKey cacheKey, CancellationToken token = default);
}
