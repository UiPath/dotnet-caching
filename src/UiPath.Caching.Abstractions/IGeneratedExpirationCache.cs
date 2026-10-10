namespace UiPath.Caching;

/// <summary>An <see cref="ICache"/> that lets the generator say when its value expires; <see cref="GeneratedExpirationExtensions"/> reaches it.</summary>
/// <remarks>Apart from <see cref="ICache"/>, and under another method name: the closure overload of <c>GetOrAddAsync&lt;T&gt;</c> would bind first and cache the <see cref="GeneratedValue{T}"/> itself.</remarks>
public interface IGeneratedExpirationCache
{
    /// <summary>Reads, and on a miss runs <paramref name="generator"/> as <see cref="ICache.GetOrAddAsync{T}(CacheKey, Func{CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/> does, storing the value until the expiration it returns; an expiration that has already passed stores nothing.</summary>
    ValueTask<T?> GetOrAddWithExpirationAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<GeneratedValue<T>>> generator, CachePolicy? policy, CancellationToken token = default);
}
