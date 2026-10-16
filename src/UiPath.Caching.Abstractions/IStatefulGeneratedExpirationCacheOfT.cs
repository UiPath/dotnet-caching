namespace UiPath.Caching;

/// <summary>An <see cref="ICache{T}"/> whose <c>GetOrAddWithExpirationAsync</c> hands the generator its state; the state member of <see cref="ICache{T}"/> prefers it.</summary>
public interface IStatefulGeneratedExpirationCache<T>
{
    /// <inheritdoc cref="IStatefulGeneratedExpirationCache.GetOrAddWithExpirationAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{GeneratedValue{T}}}, CachePolicy, CancellationToken)"/>
    ValueTask<T?> GetOrAddWithExpirationAsync<TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<GeneratedValue<T>>> generator, CancellationToken token = default);
}
