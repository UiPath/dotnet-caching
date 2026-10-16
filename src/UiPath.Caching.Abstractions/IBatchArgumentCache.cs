namespace UiPath.Caching;

/// <summary>An <see cref="ICache"/> whose batch <c>GetOrAddAsync</c> hands the generator one argument shared by the whole call, so a static lambda needs no closure; the argument members of <see cref="ICache"/> prefer it.</summary>
/// <remarks>The argument is the one value the whole call shares, not a per-entry state. Implemented by the library's in-memory and multilayer caches.</remarks>
public interface IBatchArgumentCache
{
    /// <summary>Reads the entries, and for the keys that missed runs <paramref name="generator"/> once with <paramref name="argument"/> and their states.</summary>
    /// <param name="entries">The keys, each with its own state, as in the batch overload.</param>
    /// <param name="argument">One value for the whole call, passed to <paramref name="generator"/> before the states; not a per-entry state.</param>
    /// <param name="generator">Receives <paramref name="argument"/> and the states of the keys that missed.</param>
    /// <param name="policy">The policy, as in the batch overload.</param>
    /// <param name="token">The cancellation token.</param>
    ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<T, TState, TArgument>(KeyValuePair<CacheKey, TState>[] entries, TArgument argument, Func<TArgument, TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, CachePolicy? policy, CancellationToken token = default)
        where TState : notnull;

    /// <inheritdoc cref="GetOrAddAsync{T, TState, TArgument}(KeyValuePair{CacheKey, TState}[], TArgument, Func{TArgument, TState[], CancellationToken, Task{KeyValuePair{TState, T}[]}}, CachePolicy, CancellationToken)"/>
    ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<T, TState, TArgument>(KeyValuePair<CacheKey, TState>[] entries, TArgument argument, Func<TArgument, TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default)
        where TState : notnull;

    /// <inheritdoc cref="GetOrAddAsync{T, TState, TArgument}(KeyValuePair{CacheKey, TState}[], TArgument, Func{TArgument, TState[], CancellationToken, Task{KeyValuePair{TState, T}[]}}, CachePolicy, CancellationToken)"/>
    ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<T, TState, TArgument>(KeyValuePair<CacheKey, TState>[] entries, TArgument argument, Func<TArgument, TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default)
        where TState : notnull;
}
