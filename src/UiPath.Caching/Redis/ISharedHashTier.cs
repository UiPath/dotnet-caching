namespace UiPath.Caching.Redis;

internal interface ISharedHashTier : ISharedTier
{
    ValueTask<VersionedWrite> SetVersionedAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, HashCacheEntryOptions options, CachePolicy? policy, PeerFieldLimits limits, CancellationToken token = default);

    ValueTask<bool> RemoveFieldsVersionedAsync<T>(CacheKey cacheKey, string[] fields, CancellationToken token = default);

    bool TryDecodeFields<T>(IReadOnlyDictionary<string, byte[]?> encoded, CachePolicy? policy, [MaybeNullWhen(false)] out Dictionary<string, T?> values);

    /// <summary>Reads the fields and their remaining lifetime as one snapshot; fails by throwing where a plain read answers a miss.</summary>
    ValueTask<ICacheEntry<IDictionary<string, T?>>> ReadForAdoptionAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token);
}
