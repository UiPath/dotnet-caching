namespace UiPath.Caching;

/// <summary>Reads with a generator that returns its value's expiration; a cache that does not implement <see cref="IGeneratedExpirationCache"/> is read, and on a miss the generator runs and its value is set, without coalescing.</summary>
/// <remarks>The fallback has no clock to tell that an expiration has passed, so it leaves that to the cache: a set that rejects the deadline with <see cref="ArgumentOutOfRangeException"/> leaves the value returned and not stored, as the capable caches do.</remarks>
public static class GeneratedExpirationExtensions
{
    /// <inheritdoc cref="IGeneratedExpirationCache.GetOrAddWithExpirationAsync{T}(CacheKey, Func{CancellationToken, Task{GeneratedValue{T}}}, CachePolicy, CancellationToken)"/>
    public static ValueTask<T?> GetOrAddWithExpirationAsync<T>(this ICache cache, CacheKey cacheKey, Func<CancellationToken, Task<GeneratedValue<T>>> generator, CachePolicy? policy, CancellationToken token = default) =>
        cache is IGeneratedExpirationCache capable
            ? capable.GetOrAddWithExpirationAsync(cacheKey, generator, policy, token)
            : ReadThenSetAsync(cache, cacheKey, generator, policy, token);

    /// <inheritdoc cref="IGeneratedExpirationCache.GetOrAddWithExpirationAsync{T}(CacheKey, Func{CancellationToken, Task{GeneratedValue{T}}}, CachePolicy, CancellationToken)"/>
    public static ValueTask<T?> GetOrAddWithExpirationAsync<T>(this ICache cache, CacheKey cacheKey, Func<CancellationToken, Task<GeneratedValue<T>>> generator, CancellationToken token = default) =>
        cache.GetOrAddWithExpirationAsync(cacheKey, generator, (CachePolicy?)null, token);

    /// <inheritdoc cref="IGeneratedExpirationCache.GetOrAddWithExpirationAsync{T}(CacheKey, Func{CancellationToken, Task{GeneratedValue{T}}}, CachePolicy, CancellationToken)"/>
    public static ValueTask<T?> GetOrAddWithExpirationAsync<T>(this ICache<T> cache, CacheKey cacheKey, Func<CancellationToken, Task<GeneratedValue<T>>> generator, CancellationToken token = default) =>
        cache is IGeneratedExpirationCache<T> capable
            ? capable.GetOrAddWithExpirationAsync(cacheKey, generator, token)
            : ReadThenSetAsync(cache, cacheKey, generator, token);

    private static async ValueTask<T?> ReadThenSetAsync<T>(ICache cache, CacheKey cacheKey, Func<CancellationToken, Task<GeneratedValue<T>>> generator, CachePolicy? policy, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var cached = await cache.GetCacheEntryAsync<T>(cacheKey, policy, token).ConfigureAwait(false);
        if (cached.Found)
        {
            return cached.Value;
        }

        var generated = await generator(token).ConfigureAwait(false);
        try
        {
            _ = generated.Expiration is { } expiration
                ? await cache.SetAsync<T>(cacheKey, generated.Value, expiration, policy, token).ConfigureAwait(false)
                : await cache.SetAsync<T>(cacheKey, generated.Value, policy, token).ConfigureAwait(false);
        }
        catch (ArgumentOutOfRangeException) when (generated.Expiration is not null)
        {
            // An expiration the cache will not take, because it has passed: the value is still the answer.
        }

        return generated.Value;
    }

    private static async ValueTask<T?> ReadThenSetAsync<T>(ICache<T> cache, CacheKey cacheKey, Func<CancellationToken, Task<GeneratedValue<T>>> generator, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var cached = await cache.GetAsync(cacheKey, token).ConfigureAwait(false);

        // The typed façade has no entry read, so a default value is a hit only if the key is there.
        if (!EqualityComparer<T?>.Default.Equals(cached, default) || await cache.ContainsAsync(cacheKey, token).ConfigureAwait(false))
        {
            return cached;
        }

        var generated = await generator(token).ConfigureAwait(false);
        try
        {
            _ = generated.Expiration is { } expiration
                ? await cache.SetAsync(cacheKey, generated.Value, expiration, token).ConfigureAwait(false)
                : await cache.SetAsync(cacheKey, generated.Value, token).ConfigureAwait(false);
        }
        catch (ArgumentOutOfRangeException) when (generated.Expiration is not null)
        {
            // An expiration the cache will not take, because it has passed: the value is still the answer.
        }

        return generated.Value;
    }
}
