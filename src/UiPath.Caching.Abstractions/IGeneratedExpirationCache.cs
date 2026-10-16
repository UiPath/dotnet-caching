namespace UiPath.Caching;

/// <summary>An <see cref="ICache"/> that lets the generator say when its value expires; <c>GetOrAddWithExpirationAsync</c> on <see cref="ICache"/> reaches it.</summary>
/// <remarks>Apart from <see cref="ICache"/>, and under another method name, so a generator that only throws or returns <c>default</c> is not ambiguous against the closure overload. Implemented by the library's in-memory, multilayer and Redis caches.</remarks>
public interface IGeneratedExpirationCache
{
    /// <summary>Reads, and on a miss runs <paramref name="generator"/> as <see cref="ICache.GetOrAddAsync{T}(CacheKey, Func{CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/> does, storing the value until the expiration it returns; an expiration that has already passed stores nothing.</summary>
    ValueTask<T?> GetOrAddWithExpirationAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<GeneratedValue<T>>> generator, CachePolicy? policy, CancellationToken token = default);
}
