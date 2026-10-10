namespace UiPath.Caching;

/// <summary>Reads and removes by the key's text: reads through <see cref="ISpanKeyCache"/> and its siblings when the cache implements them, otherwise by building the key; a removal always builds it.</summary>
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

    /// <inheritdoc cref="GetAsync{T}(ICache, Span{char}, CachePolicy, CancellationToken)"/>
    public static ValueTask<ICacheEntry<T?>> GetCacheEntryAsync<T>(this ICache cache, Span<char> cacheKey, CachePolicy? policy, CancellationToken token = default) =>
        cache is ISpanKeyCache spanKeyCache
            ? spanKeyCache.GetCacheEntryAsync<T>(cacheKey, policy, token)
            : cache.GetCacheEntryAsync<T>(new CacheKey(cacheKey), policy, token);

    /// <inheritdoc cref="GetAsync{T}(ICache, Span{char}, CachePolicy, CancellationToken)"/>
    public static ValueTask<ICacheEntry<IDictionary<string, T?>>> GetCacheEntryAsync<T>(this IHashCache cache, Span<char> cacheKey, CachePolicy? policy, CancellationToken token = default) =>
        cache is ISpanKeyHashCache spanKeyCache
            ? spanKeyCache.GetCacheEntryAsync<T>(cacheKey, policy, token)
            : cache.GetCacheEntryAsync<T>(new CacheKey(cacheKey), policy, token);

    /// <inheritdoc cref="GetAsync{T}(ICache, Span{char}, CachePolicy, CancellationToken)"/>
    public static ValueTask<ICacheEntry<IDictionary<string, T?>>> GetCacheEntryAsync<T>(this IHashCache<T> cache, Span<char> cacheKey, CancellationToken token = default) =>
        cache is ISpanKeyHashCache<T> spanKeyCache
            ? spanKeyCache.GetCacheEntryAsync(cacheKey, token)
            : cache.GetCacheEntryAsync(new CacheKey(cacheKey), token);

    /// <summary>Reads by the key's text as <see cref="GetAsync{T}(ICache, Span{char}, CachePolicy, CancellationToken)"/> does; a miss builds the key and takes the <see cref="CacheKey"/> path, so the generator runs as it would there.</summary>
    public static ValueTask<T?> GetOrAddAsync<T>(this ICache cache, Span<char> cacheKey, Func<CancellationToken, Task<T?>> generator, CachePolicy? policy, CancellationToken token = default) =>
        cache is ISpanKeyCache spanKeyCache
            ? spanKeyCache.GetOrAddAsync(cacheKey, generator, policy, token)
            : cache.GetOrAddAsync(new CacheKey(cacheKey), generator, policy, token);

    /// <inheritdoc cref="GetOrAddAsync{T}(ICache, Span{char}, Func{CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    public static ValueTask<T?> GetOrAddAsync<T>(this ICache cache, Span<char> cacheKey, Func<CancellationToken, Task<T?>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) =>
        cache is ISpanKeyCache spanKeyCache
            ? spanKeyCache.GetOrAddAsync(cacheKey, generator, expiration, policy, token)
            : cache.GetOrAddAsync(new CacheKey(cacheKey), generator, expiration, policy, token);

    /// <inheritdoc cref="GetOrAddAsync{T}(ICache, Span{char}, Func{CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    public static ValueTask<T?> GetOrAddAsync<T>(this ICache cache, Span<char> cacheKey, Func<CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) =>
        cache is ISpanKeyCache spanKeyCache
            ? spanKeyCache.GetOrAddAsync(cacheKey, generator, expiration, policy, token)
            : cache.GetOrAddAsync(new CacheKey(cacheKey), generator, expiration, policy, token);

    /// <inheritdoc cref="GetOrAddAsync{T}(ICache, Span{char}, Func{CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    public static ValueTask<T?> GetOrAddAsync<T>(this ICache<T> cache, Span<char> cacheKey, Func<CancellationToken, Task<T?>> generator, CancellationToken token = default) =>
        cache is ISpanKeyCache<T> spanKeyCache
            ? spanKeyCache.GetOrAddAsync(cacheKey, generator, token)
            : cache.GetOrAddAsync(new CacheKey(cacheKey), generator, token);

    /// <inheritdoc cref="GetOrAddAsync{T}(ICache, Span{char}, Func{CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    public static ValueTask<T?> GetOrAddAsync<T>(this ICache<T> cache, Span<char> cacheKey, Func<CancellationToken, Task<T?>> generator, TimeSpan expiration, CancellationToken token = default) =>
        cache is ISpanKeyCache<T> spanKeyCache
            ? spanKeyCache.GetOrAddAsync(cacheKey, generator, expiration, token)
            : cache.GetOrAddAsync(new CacheKey(cacheKey), generator, expiration, token);

    /// <inheritdoc cref="GetOrAddAsync{T}(ICache, Span{char}, Func{CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    public static ValueTask<T?> GetOrAddAsync<T>(this ICache<T> cache, Span<char> cacheKey, Func<CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CancellationToken token = default) =>
        cache is ISpanKeyCache<T> spanKeyCache
            ? spanKeyCache.GetOrAddAsync(cacheKey, generator, expiration, token)
            : cache.GetOrAddAsync(new CacheKey(cacheKey), generator, expiration, token);

    /// <inheritdoc cref="ISpanKeyCache.GetOrAddAsync{T, TState}(Span{char}, TState, Func{TState, CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    public static ValueTask<T?> GetOrAddAsync<T, TState>(this ICache cache, Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, CachePolicy? policy, CancellationToken token = default) =>
        cache is ISpanKeyCache spanKeyCache
            ? spanKeyCache.GetOrAddAsync(cacheKey, state, generator, policy, token)
            : cache.GetOrAddAsync(new CacheKey(cacheKey), state, generator, policy, token);

    /// <inheritdoc cref="ISpanKeyCache.GetOrAddAsync{T, TState}(Span{char}, TState, Func{TState, CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    public static ValueTask<T?> GetOrAddAsync<T, TState>(this ICache cache, Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) =>
        cache is ISpanKeyCache spanKeyCache
            ? spanKeyCache.GetOrAddAsync(cacheKey, state, generator, expiration, policy, token)
            : cache.GetOrAddAsync(new CacheKey(cacheKey), state, generator, expiration, policy, token);

    /// <inheritdoc cref="ISpanKeyCache.GetOrAddAsync{T, TState}(Span{char}, TState, Func{TState, CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    public static ValueTask<T?> GetOrAddAsync<T, TState>(this ICache cache, Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) =>
        cache is ISpanKeyCache spanKeyCache
            ? spanKeyCache.GetOrAddAsync(cacheKey, state, generator, expiration, policy, token)
            : cache.GetOrAddAsync(new CacheKey(cacheKey), state, generator, expiration, policy, token);

    /// <inheritdoc cref="ISpanKeyCache.GetOrAddAsync{T, TState}(Span{char}, TState, Func{TState, CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    public static ValueTask<T?> GetOrAddAsync<T, TState>(this ICache<T> cache, Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, CancellationToken token = default) =>
        cache is ISpanKeyCache<T> spanKeyCache
            ? spanKeyCache.GetOrAddAsync(cacheKey, state, generator, token)
            : cache.GetOrAddAsync(new CacheKey(cacheKey), state, generator, token);

    /// <inheritdoc cref="ISpanKeyCache.GetOrAddAsync{T, TState}(Span{char}, TState, Func{TState, CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    public static ValueTask<T?> GetOrAddAsync<T, TState>(this ICache<T> cache, Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, TimeSpan expiration, CancellationToken token = default) =>
        cache is ISpanKeyCache<T> spanKeyCache
            ? spanKeyCache.GetOrAddAsync(cacheKey, state, generator, expiration, token)
            : cache.GetOrAddAsync(new CacheKey(cacheKey), state, generator, expiration, token);

    /// <inheritdoc cref="ISpanKeyCache.GetOrAddAsync{T, TState}(Span{char}, TState, Func{TState, CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    public static ValueTask<T?> GetOrAddAsync<T, TState>(this ICache<T> cache, Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CancellationToken token = default) =>
        cache is ISpanKeyCache<T> spanKeyCache
            ? spanKeyCache.GetOrAddAsync(cacheKey, state, generator, expiration, token)
            : cache.GetOrAddAsync(new CacheKey(cacheKey), state, generator, expiration, token);

    /// <inheritdoc cref="GetOrAddAsync{T}(ICache, Span{char}, Func{CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    public static ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(this IHashCache cache, Span<char> cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, CachePolicy? policy, CancellationToken token = default) =>
        cache is ISpanKeyHashCache spanKeyCache
            ? spanKeyCache.GetOrAddAsync(cacheKey, generator, policy, token)
            : cache.GetOrAddAsync(new CacheKey(cacheKey), generator, policy, token);

    /// <inheritdoc cref="GetOrAddAsync{T}(ICache, Span{char}, Func{CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    public static ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(this IHashCache cache, Span<char> cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) =>
        cache is ISpanKeyHashCache spanKeyCache
            ? spanKeyCache.GetOrAddAsync(cacheKey, generator, expiration, policy, token)
            : cache.GetOrAddAsync(new CacheKey(cacheKey), generator, expiration, policy, token);

    /// <inheritdoc cref="GetOrAddAsync{T}(ICache, Span{char}, Func{CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    public static ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(this IHashCache cache, Span<char> cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) =>
        cache is ISpanKeyHashCache spanKeyCache
            ? spanKeyCache.GetOrAddAsync(cacheKey, generator, expiration, policy, token)
            : cache.GetOrAddAsync(new CacheKey(cacheKey), generator, expiration, policy, token);

    /// <inheritdoc cref="GetOrAddAsync{T}(ICache, Span{char}, Func{CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    public static ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(this IHashCache cache, Span<char> cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, HashCacheSetOption? setOption, CachePolicy? policy, CancellationToken token = default) =>
        cache is ISpanKeyHashCache spanKeyCache
            ? spanKeyCache.GetOrAddAsync(cacheKey, generator, expiration, setOption, policy, token)
            : cache.GetOrAddAsync(new CacheKey(cacheKey), generator, expiration, setOption, policy, token);

    /// <inheritdoc cref="GetOrAddAsync{T}(ICache, Span{char}, Func{CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    public static ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(this IHashCache<T> cache, Span<char> cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, CancellationToken token = default) =>
        cache is ISpanKeyHashCache<T> spanKeyCache
            ? spanKeyCache.GetOrAddAsync(cacheKey, generator, token)
            : cache.GetOrAddAsync(new CacheKey(cacheKey), generator, token);

    /// <inheritdoc cref="GetOrAddAsync{T}(ICache, Span{char}, Func{CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    public static ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(this IHashCache<T> cache, Span<char> cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan expiration, CancellationToken token = default) =>
        cache is ISpanKeyHashCache<T> spanKeyCache
            ? spanKeyCache.GetOrAddAsync(cacheKey, generator, expiration, token)
            : cache.GetOrAddAsync(new CacheKey(cacheKey), generator, expiration, token);

    /// <inheritdoc cref="GetOrAddAsync{T}(ICache, Span{char}, Func{CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    public static ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(this IHashCache<T> cache, Span<char> cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, CancellationToken token = default) =>
        cache is ISpanKeyHashCache<T> spanKeyCache
            ? spanKeyCache.GetOrAddAsync(cacheKey, generator, expiration, token)
            : cache.GetOrAddAsync(new CacheKey(cacheKey), generator, expiration, token);

    /// <inheritdoc cref="GetAsync{T}(ICache, Span{char}, CachePolicy, CancellationToken)"/>
    public static ValueTask<bool> ContainsAsync<T>(this ICache cache, Span<char> cacheKey, CancellationToken token = default) =>
        cache is ISpanKeyCache spanKeyCache
            ? spanKeyCache.ContainsAsync<T>(cacheKey, token)
            : cache.ContainsAsync<T>(new CacheKey(cacheKey), token);

    /// <inheritdoc cref="GetAsync{T}(ICache, Span{char}, CachePolicy, CancellationToken)"/>
    public static ValueTask<bool> ContainsAsync<T>(this ICache<T> cache, Span<char> cacheKey, CancellationToken token = default) =>
        cache is ISpanKeyCache<T> spanKeyCache
            ? spanKeyCache.ContainsAsync(cacheKey, token)
            : cache.ContainsAsync(new CacheKey(cacheKey), token);

    /// <inheritdoc cref="GetAsync{T}(ICache, Span{char}, CachePolicy, CancellationToken)"/>
    public static ValueTask<bool> ContainsAsync<T>(this IHashCache cache, Span<char> cacheKey, CancellationToken token = default) =>
        cache is ISpanKeyHashCache spanKeyCache
            ? spanKeyCache.ContainsAsync<T>(cacheKey, token)
            : cache.ContainsAsync<T>(new CacheKey(cacheKey), token);

    /// <inheritdoc cref="GetAsync{T}(ICache, Span{char}, CachePolicy, CancellationToken)"/>
    public static ValueTask<bool> ContainsAsync<T>(this IHashCache<T> cache, Span<char> cacheKey, CancellationToken token = default) =>
        cache is ISpanKeyHashCache<T> spanKeyCache
            ? spanKeyCache.ContainsAsync(cacheKey, token)
            : cache.ContainsAsync(new CacheKey(cacheKey), token);

    /// <summary>Removes by the key's text, normalized as <c>new CacheKey(text)</c> normalizes it; every tier needs the key as a string, so this builds it.</summary>
    public static ValueTask<bool> RemoveAsync<T>(this ICache cache, Span<char> cacheKey, CancellationToken token = default) =>
        cache.RemoveAsync<T>(new CacheKey(cacheKey), token);

    /// <inheritdoc cref="RemoveAsync{T}(ICache, Span{char}, CancellationToken)"/>
    public static ValueTask<bool> RemoveAsync<T>(this ICache<T> cache, Span<char> cacheKey, CancellationToken token = default) =>
        cache.RemoveAsync(new CacheKey(cacheKey), token);

    /// <inheritdoc cref="RemoveAsync{T}(ICache, Span{char}, CancellationToken)"/>
    public static ValueTask<bool> RemoveAsync<T>(this IHashCache cache, Span<char> cacheKey, CancellationToken token = default) =>
        cache.RemoveAsync<T>(new CacheKey(cacheKey), token);

    /// <inheritdoc cref="RemoveAsync{T}(ICache, Span{char}, CancellationToken)"/>
    public static ValueTask<bool> RemoveAsync<T>(this IHashCache<T> cache, Span<char> cacheKey, CancellationToken token = default) =>
        cache.RemoveAsync(new CacheKey(cacheKey), token);
}
