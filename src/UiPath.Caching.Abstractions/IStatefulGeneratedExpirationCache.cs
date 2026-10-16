namespace UiPath.Caching;

/// <summary>An <see cref="ICache"/> whose <c>GetOrAddWithExpirationAsync</c> hands the generator its state, so a static lambda needs no closure; the state member of <see cref="ICache"/> prefers it.</summary>
/// <remarks>Apart from <see cref="IGeneratedExpirationCache"/>, which shipped in 3.0, so an implementation of that one keeps compiling. Implemented by the library's in-memory and multilayer caches.</remarks>
public interface IStatefulGeneratedExpirationCache
{
    /// <summary>Reads, and on a miss runs <paramref name="generator"/> with <paramref name="state"/>, storing the value until the expiration it returns.</summary>
    ValueTask<T?> GetOrAddWithExpirationAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<GeneratedValue<T>>> generator, CachePolicy? policy, CancellationToken token = default);
}
