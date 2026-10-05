namespace UiPath.Caching;

/// <summary>Reads by the key's text: through <see cref="ISpanKeyCache"/> and its siblings when the cache implements them, otherwise by building the key.</summary>
/// <remarks>
/// <para>Extensions rather than interface members, so a mock of <see cref="ICache"/> serves the read through the <see cref="CacheKey"/>
/// overload it already sets up; a proxy generator cannot emit a method that takes a <c>Span&lt;char&gt;</c>.</para>
/// <para>Takes a <c>Span&lt;char&gt;</c>: a <c>ReadOnlySpan&lt;char&gt;</c> overload on the capability interfaces would make a call with a string
/// ambiguous against the <see cref="CacheKey"/> overload.</para>
/// </remarks>
public static class SpanKeyExtensions
{
    /// <summary>Reads by the key's text, normalized as <c>new CacheKey(text)</c> normalizes it, so a cache that implements <see cref="ISpanKeyCache"/> can serve a local hit without allocating.</summary>
    public static ValueTask<T?> GetAsync<T>(this ICache cache, Span<char> cacheKey, CachePolicy? policy, CancellationToken token = default) =>
        cache is ISpanKeyCache spanKeyCache
            ? spanKeyCache.GetAsync<T>(cacheKey, policy, token)
            : cache.GetAsync<T>(new CacheKey(cacheKey), policy, token);

    /// <inheritdoc cref="GetAsync{T}(ICache, Span{char}, CachePolicy, CancellationToken)"/>
    public static ValueTask<T?> GetAsync<T>(this ICache<T> cache, Span<char> cacheKey, CancellationToken token = default) =>
        cache is ISpanKeyCache<T> spanKeyCache
            ? spanKeyCache.GetAsync(cacheKey, token)
            : cache.GetAsync(new CacheKey(cacheKey), token);

    /// <inheritdoc cref="GetAsync{T}(ICache, Span{char}, CachePolicy, CancellationToken)"/>
    public static ValueTask<IDictionary<string, T?>> GetAsync<T>(this IHashCache cache, Span<char> cacheKey, CachePolicy? policy, CancellationToken token = default) =>
        cache is ISpanKeyHashCache spanKeyCache
            ? spanKeyCache.GetAsync<T>(cacheKey, policy, token)
            : cache.GetAsync<T>(new CacheKey(cacheKey), policy, token);

    /// <inheritdoc cref="GetAsync{T}(ICache, Span{char}, CachePolicy, CancellationToken)"/>
    public static ValueTask<IDictionary<string, T?>> GetAsync<T>(this IHashCache<T> cache, Span<char> cacheKey, CancellationToken token = default) =>
        cache is ISpanKeyHashCache<T> spanKeyCache
            ? spanKeyCache.GetAsync(cacheKey, token)
            : cache.GetAsync(new CacheKey(cacheKey), token);

    /// <inheritdoc cref="GetAsync{T}(ICache, Span{char}, CachePolicy, CancellationToken)"/>
    public static ValueTask<T?> GetItemAsync<T>(this IHashCache cache, Span<char> cacheKey, string field, CachePolicy? policy, CancellationToken token = default) =>
        cache is ISpanKeyHashCache spanKeyCache
            ? spanKeyCache.GetItemAsync<T>(cacheKey, field, policy, token)
            : cache.GetItemAsync<T>(new CacheKey(cacheKey), field, policy, token);

    /// <inheritdoc cref="GetAsync{T}(ICache, Span{char}, CachePolicy, CancellationToken)"/>
    public static ValueTask<T?> GetItemAsync<T>(this IHashCache<T> cache, Span<char> cacheKey, string field, CancellationToken token = default) =>
        cache is ISpanKeyHashCache<T> spanKeyCache
            ? spanKeyCache.GetItemAsync(cacheKey, field, token)
            : cache.GetItemAsync(new CacheKey(cacheKey), field, token);
}
