namespace UiPath.Caching;

/// <summary>An inner tier that can read entries for a peer refresh as <see cref="DeferredEntry{T}"/>: found payloads stay serialized until a local read asks for the value.</summary>
internal interface IDeferredEntryReader
{
    ValueTask<ICacheEntry<T?>> GetDeferredEntryAsync<T>(CacheKey cacheKey, CancellationToken token);

    ValueTask<KeyValuePair<CacheKey, ICacheEntry<T?>>[]> GetDeferredEntriesAsync<T>(CacheKey[] cacheKeys, CancellationToken token);
}
