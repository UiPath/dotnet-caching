namespace UiPath.Caching;

/// <summary>For code that moves hashes between stores: snapshot reads, writes, deletes and refreshes against the primary that report what happened instead of throwing or answering a miss. <see cref="StoreOutcome.Failed"/> is an answer that applied nothing (a condition that did not hold, or a transaction the server refused), so the key is as it was; a transaction that ran while a command in it failed may have changed the key and is <see cref="StoreOutcome.Unknown"/>.</summary>
internal interface IMigrationHashCache
{
    /// <summary>The fields, the metadata and the remaining lifetime as one snapshot of the primary, bypassing local copies; an unreadable hash is reported and left in place.</summary>
    ValueTask<HashSnapshotRead<T>> ReadSnapshotAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default);

    /// <summary>Announces like <c>SetAsync</c> with the same options (a replace of the key or a merge of the fields); differs only in telling a write that was never sent from one whose result is unknown.</summary>
    ValueTask<StoreOutcome> WriteAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, HashCacheEntryOptions options, CachePolicy? policy, CancellationToken token = default);

    /// <summary>Announces like <c>RemoveAsync</c>; differs only in telling a delete that was never sent from one whose result is unknown.</summary>
    ValueTask<StoreOutcome> DeleteAsync<T>(CacheKey cacheKey, CancellationToken token = default);

    /// <summary>Announces like <c>RemoveFieldsAsync</c>; differs only in telling a delete that was never sent from one whose result is unknown.</summary>
    ValueTask<StoreOutcome> DeleteFieldsAsync<T>(CacheKey cacheKey, string[] fields, CancellationToken token = default);

    /// <summary>Announces like <c>RefreshAsync</c> with the same options, with the same effect on a hash that is there: the lifetime is set or removed, and the metadata is replaced or, when the options carry none, cleared. It is <see cref="StoreOutcome.Committed"/> for a hash that was there and <see cref="StoreOutcome.Failed"/> for one that was not, which it leaves absent whatever the null setting.</summary>
    ValueTask<StoreOutcome> RenewAsync<T>(CacheKey cacheKey, HashCacheEntryOptions options, CachePolicy? policy, CancellationToken token = default);

    /// <summary>The expiry-only refresh, as the library's <c>RefreshAsync</c> with an expiration does it, with the outcome reported whether or not the library awaits its refresh: the lifetime is set or removed and the metadata stays. The metadata of <paramref name="options"/> is ignored. <see cref="StoreOutcome.Committed"/> for a hash that was there; <see cref="StoreOutcome.Failed"/> for a key that holds no hash, which is left as it is; <see cref="StoreOutcome.Unknown"/> for a send whose answer was lost; <see cref="StoreOutcome.NotSent"/> when nothing was sent.</summary>
    ValueTask<StoreOutcome> RenewExpiryAsync<T>(CacheKey cacheKey, HashCacheEntryOptions options, CachePolicy? policy, CancellationToken token = default);
}
