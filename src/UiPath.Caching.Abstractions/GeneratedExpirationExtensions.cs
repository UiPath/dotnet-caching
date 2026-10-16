namespace UiPath.Caching;

/// <summary>The policy-less <c>GetOrAddWithExpirationAsync</c> of <see cref="ICache"/>.</summary>
/// <remarks>A set that rejects a deadline that has passed, by the system clock, with <see cref="ArgumentOutOfRangeException"/> leaves the value returned and not stored, as the capable caches do; any other rejection reaches the caller.</remarks>
public static class GeneratedExpirationExtensions
{
    /// <inheritdoc cref="IGeneratedExpirationCache.GetOrAddWithExpirationAsync{T}(CacheKey, Func{CancellationToken, Task{GeneratedValue{T}}}, CachePolicy, CancellationToken)"/>
    public static ValueTask<T?> GetOrAddWithExpirationAsync<T>(this ICache cache, CacheKey cacheKey, Func<CancellationToken, Task<GeneratedValue<T>>> generator, CancellationToken token = default) =>
        cache.GetOrAddWithExpirationAsync(cacheKey, generator, (CachePolicy?)null, token);

    internal static async ValueTask<T?> ReadThenSetAsync<T>(ICache cache, CacheKey cacheKey, Func<CancellationToken, Task<GeneratedValue<T>>> generator, CachePolicy? policy, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var cached = await cache.GetCacheEntryAsync<T>(cacheKey, policy, token).ConfigureAwait(false);
        // A null entry is a miss: no cache returns one, but a mock answers a read it was not set up for with null.
        if (cached is { Found: true })
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
        catch (ArgumentOutOfRangeException) when (generated.Expiration <= DateTimeOffset.UtcNow)
        {
            // An expiration the cache will not take because it has passed: the value is still the answer. Any other rejection would leave the key unstored and the generator running on every call, unseen.
        }

        return generated.Value;
    }

    internal static async ValueTask<T?> ReadThenSetAsync<T>(ICache<T> cache, CacheKey cacheKey, Func<CancellationToken, Task<GeneratedValue<T>>> generator, CancellationToken token)
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
        catch (ArgumentOutOfRangeException) when (generated.Expiration <= DateTimeOffset.UtcNow)
        {
            // An expiration the cache will not take because it has passed: the value is still the answer. Any other rejection would leave the key unstored and the generator running on every call, unseen.
        }

        return generated.Value;
    }
}
