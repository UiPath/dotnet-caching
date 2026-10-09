namespace UiPath.Caching;

/// <summary>An <see cref="ICache{T}"/> that lets the generator say when its value expires; <see cref="GeneratedExpirationExtensions"/> reaches it.</summary>
public interface IGeneratedExpirationCache<T>
{
    /// <inheritdoc cref="IGeneratedExpirationCache.GetOrAddWithExpirationAsync{T}(CacheKey, Func{CancellationToken, Task{GeneratedValue{T}}}, CachePolicy, CancellationToken)"/>
    ValueTask<T?> GetOrAddWithExpirationAsync(CacheKey cacheKey, Func<CancellationToken, Task<GeneratedValue<T>>> generator, CancellationToken token = default);
}
