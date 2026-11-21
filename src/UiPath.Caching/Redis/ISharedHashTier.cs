namespace UiPath.Caching.Redis;

internal interface ISharedHashTier : ISharedTier
{
    ValueTask<VersionedWrite> SetVersionedAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, HashCacheEntryOptions options, CachePolicy? policy, PeerFieldLimits limits, CancellationToken token = default);

    /// <summary>As <see cref="SetVersionedAsync{T}"/>, writing only when the key is absent; <see cref="VersionedWrite.Existed"/> when it was not.</summary>
    ValueTask<VersionedWrite> FillVersionedAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, HashCacheEntryOptions options, CachePolicy? policy, PeerFieldLimits limits, CancellationToken token = default);

    /// <summary>Removes the key only while it still holds the unreadable fields just read; false when it changed meanwhile, was readable, or was absent.</summary>
    ValueTask<bool> RemoveIfUnreadableAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token);

    ValueTask<bool> RemoveFieldsVersionedAsync<T>(CacheKey cacheKey, string[] fields, CancellationToken token = default);

    bool TryDecodeFields<T>(IReadOnlyDictionary<string, byte[]?> encoded, CachePolicy? policy, [MaybeNullWhen(false)] out Dictionary<string, T?> values);

    /// <summary>Reads the fields and their remaining lifetime as one snapshot; fails by throwing where a plain read answers a miss.</summary>
    ValueTask<ICacheEntry<IDictionary<string, T?>>> ReadForAdoptionAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token);
}
