namespace UiPath.Caching.Redis;

/// <summary>The inner tier's writes that count a hash's writes, so the multilayer cache can tell its peers what a write changed.</summary>
internal interface IVersionedHashCache
{
    ValueTask<VersionedWrite> SetVersionedAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, HashCacheEntryOptions options, CachePolicy? policy, PeerFieldLimits limits, CancellationToken token = default);

    ValueTask<bool> RemoveFieldsVersionedAsync<T>(CacheKey cacheKey, string[] fields, CancellationToken token = default);

    bool TryDecodeFields<T>(IDictionary<string, string?> encoded, [MaybeNullWhen(false)] out Dictionary<string, T?> values);
}
