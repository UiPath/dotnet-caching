#pragma warning disable S3060 // by design: a cache with the capability interface takes its path, any other (a mock) falls back to the CacheKey members
namespace UiPath.Caching;

/// <remarks>
/// <para><b>Expiration.</b> The <c>expiration</c> parameter is not nullable. A caller with nothing to
/// say about lifetime calls the overload that has no <c>expiration</c> parameter and gets
/// <see cref="CachePolicy.DistributedExpiration"/>, then the cache default; a caller that passes one
/// means it, so a duration that is not positive — or a deadline that has already passed — is
/// rejected with <see cref="ArgumentOutOfRangeException"/> rather than silently ignored. See
/// <c>CacheExpiration</c>. The no-op implementations in this package read no argument at all
/// and so enforce nothing.</para>
/// <para><b>Batch GetOrAdd.</b> An implementation outside the library can forward the batch
/// <c>GetOrAddAsync</c> overloads to <c>BatchGetOrAdd.RunAsync</c> in <c>UiPath.Caching</c>.</para>
/// </remarks>
public interface ICache : IDisposable
{
    string Name { get; }

    [OverloadResolutionPriority(1)]
    ValueTask<T?> GetAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default);


    ValueTask<KeyValuePair<CacheKey, T?>[]> GetAsync<T>(CacheKey[] cacheKeys, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.GetAsync{T}(Span{char}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<T?> GetAsync<T>(Span<char> cacheKey, CachePolicy? policy, CancellationToken token = default) =>
        this is ISpanKeyCache spanKeyCache
            ? spanKeyCache.GetAsync<T>(cacheKey, policy, token)
            : GetAsync<T>(new CacheKey(cacheKey), policy, token);

    ValueTask<ICacheEntry<T?>> GetCacheEntryAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.GetAsync{T}(Span{char}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<ICacheEntry<T?>> GetCacheEntryAsync<T>(Span<char> cacheKey, CachePolicy? policy, CancellationToken token = default) =>
        this is ISpanKeyCache spanKeyCache
            ? spanKeyCache.GetCacheEntryAsync<T>(cacheKey, policy, token)
            : GetCacheEntryAsync<T>(new CacheKey(cacheKey), policy, token);

    ValueTask<KeyValuePair<CacheKey, ICacheEntry<T?>>[]> GetCacheEntriesAsync<T>(CacheKey[] cacheKeys, CachePolicy? policy, CancellationToken token = default);

    ValueTask<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, CachePolicy? policy, CancellationToken token = default);

    ValueTask<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default);

    ValueTask<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default);

    ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<T, TState>(KeyValuePair<CacheKey, TState>[] entries, Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, CachePolicy? policy, CancellationToken token = default)
        where TState : notnull;

    ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<T, TState>(KeyValuePair<CacheKey, TState>[] entries, Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default)
        where TState : notnull;

    ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<T, TState>(KeyValuePair<CacheKey, TState>[] entries, Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default)
        where TState : notnull;

    /// <inheritdoc cref="ISpanKeyCache.GetOrAddAsync{T}(Span{char}, Func{CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<T?> GetOrAddAsync<T>(Span<char> cacheKey, Func<CancellationToken, Task<T?>> generator, CachePolicy? policy, CancellationToken token = default) =>
        this is ISpanKeyCache spanKeyCache
            ? spanKeyCache.GetOrAddAsync(cacheKey, generator, policy, token)
            : GetOrAddAsync(new CacheKey(cacheKey), generator, policy, token);

    /// <inheritdoc cref="ISpanKeyCache.GetOrAddAsync{T}(Span{char}, Func{CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<T?> GetOrAddAsync<T>(Span<char> cacheKey, Func<CancellationToken, Task<T?>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) =>
        this is ISpanKeyCache spanKeyCache
            ? spanKeyCache.GetOrAddAsync(cacheKey, generator, expiration, policy, token)
            : GetOrAddAsync(new CacheKey(cacheKey), generator, expiration, policy, token);

    /// <inheritdoc cref="ISpanKeyCache.GetOrAddAsync{T}(Span{char}, Func{CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<T?> GetOrAddAsync<T>(Span<char> cacheKey, Func<CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) =>
        this is ISpanKeyCache spanKeyCache
            ? spanKeyCache.GetOrAddAsync(cacheKey, generator, expiration, policy, token)
            : GetOrAddAsync(new CacheKey(cacheKey), generator, expiration, policy, token);

    /// <inheritdoc cref="ISpanKeyCache.GetOrAddAsync{T, TState}(Span{char}, TState, Func{TState, CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<T?> GetOrAddAsync<T, TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, CachePolicy? policy, CancellationToken token = default) =>
        this is ISpanKeyCache spanKeyCache
            ? spanKeyCache.GetOrAddAsync(cacheKey, state, generator, policy, token)
            : GetOrAddAsync(new CacheKey(cacheKey), state, generator, policy, token);

    /// <inheritdoc cref="ISpanKeyCache.GetOrAddAsync{T, TState}(Span{char}, TState, Func{TState, CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<T?> GetOrAddAsync<T, TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) =>
        this is ISpanKeyCache spanKeyCache
            ? spanKeyCache.GetOrAddAsync(cacheKey, state, generator, expiration, policy, token)
            : GetOrAddAsync(new CacheKey(cacheKey), state, generator, expiration, policy, token);

    /// <inheritdoc cref="ISpanKeyCache.GetOrAddAsync{T, TState}(Span{char}, TState, Func{TState, CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<T?> GetOrAddAsync<T, TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) =>
        this is ISpanKeyCache spanKeyCache
            ? spanKeyCache.GetOrAddAsync(cacheKey, state, generator, expiration, policy, token)
            : GetOrAddAsync(new CacheKey(cacheKey), state, generator, expiration, policy, token);

    /// <summary>Reads, and on a miss runs <paramref name="generator"/> with <paramref name="state"/> as <see cref="GetOrAddAsync{T}(CacheKey, Func{CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/> runs its generator; a hit allocates no closure.</summary>
    ValueTask<T?> GetOrAddAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="GetOrAddAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    ValueTask<T?> GetOrAddAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="GetOrAddAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    ValueTask<T?> GetOrAddAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default);

    /// <summary>Reads the entries, and for the keys that missed runs <paramref name="generator"/> once with <paramref name="argument"/> and their states, as the batch overload does; the argument lets a static lambda need no closure.</summary>
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

    ValueTask<bool> RemoveAsync<T>(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<bool> RemoveAsync<T>(CacheKey[] cacheKey, CancellationToken token = default);

    /// <summary>Removes by the key's text, normalized as <c>new CacheKey(text)</c> normalizes it.</summary>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<bool> RemoveAsync<T>(Span<char> cacheKey, CancellationToken token = default) =>
        RemoveAsync<T>(new CacheKey(cacheKey), token);

    /// <inheritdoc cref="ISpanKeyCache.RemoveManyAsync{T}(IEnumerable{CacheKey}, CancellationToken)"/>
    sealed ValueTask<IReadOnlyList<CacheKey>> RemoveManyAsync<T>(IEnumerable<CacheKey> cacheKeys, CancellationToken token = default) =>
        this is ISpanKeyCache multi
            ? multi.RemoveManyAsync<T>(cacheKeys, token)
            : MultiKeyFallback.RunAsync(cacheKeys, (Cache: this, Token: token), static k => k, static (s, k) => s.Cache.RemoveAsync<T>(k, s.Token));

    /// <inheritdoc cref="ISpanKeyCache.SetManyAsync{T}(IEnumerable{KeyValuePair{CacheKey, T}}, CachePolicy, CancellationToken)"/>
    sealed ValueTask<IReadOnlyList<CacheKey>> SetManyAsync<T>(IEnumerable<KeyValuePair<CacheKey, T?>> keyValues, CachePolicy? policy, CancellationToken token = default) =>
        this is ISpanKeyCache multi
            ? multi.SetManyAsync(keyValues, policy, token)
            : MultiKeyFallback.RunAsync(keyValues, (Cache: this, Policy: policy, Token: token), static kv => kv.Key, static (s, kv) => s.Cache.SetAsync<T>(kv.Key, kv.Value, s.Policy, s.Token));

    /// <inheritdoc cref="ISpanKeyCache.SetManyAsync{T}(IEnumerable{KeyValuePair{CacheKey, T}}, CachePolicy, CancellationToken)"/>
    sealed ValueTask<IReadOnlyList<CacheKey>> SetManyAsync<T>(IEnumerable<KeyValuePair<CacheKey, T?>> keyValues, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) =>
        this is ISpanKeyCache multi
            ? multi.SetManyAsync(keyValues, expiration, policy, token)
            : MultiKeyFallback.RunAsync(keyValues, (Cache: this, Expiration: expiration, Policy: policy, Token: token), static kv => kv.Key, static (s, kv) => s.Cache.SetAsync<T>(kv.Key, kv.Value, s.Expiration, s.Policy, s.Token));

    /// <inheritdoc cref="ISpanKeyCache.SetManyAsync{T}(IEnumerable{KeyValuePair{CacheKey, T}}, CachePolicy, CancellationToken)"/>
    sealed ValueTask<IReadOnlyList<CacheKey>> SetManyAsync<T>(IEnumerable<KeyValuePair<CacheKey, T?>> keyValues, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) =>
        this is ISpanKeyCache multi
            ? multi.SetManyAsync(keyValues, expiration, policy, token)
            : MultiKeyFallback.RunAsync(keyValues, (Cache: this, Expiration: expiration, Policy: policy, Token: token), static kv => kv.Key, static (s, kv) => s.Cache.SetAsync<T>(kv.Key, kv.Value, s.Expiration, s.Policy, s.Token));

    ValueTask<bool> SetAsync<T>(CacheKey cacheKey, T? value, CachePolicy? policy, CancellationToken token = default);

    ValueTask<bool> SetAsync<T>(CacheKey cacheKey, T? value, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default);

    ValueTask<bool> SetAsync<T>(CacheKey cacheKey, T? value, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.SetAsync{T}(Span{char}, T, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<bool> SetAsync<T>(Span<char> cacheKey, T? value, CachePolicy? policy, CancellationToken token = default) =>
        this is ISpanKeyCache spanKeyCache
            ? spanKeyCache.SetAsync(cacheKey, value, policy, token)
            : SetAsync(new CacheKey(cacheKey), value, policy, token);

    /// <inheritdoc cref="ISpanKeyCache.SetAsync{T}(Span{char}, T, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<bool> SetAsync<T>(Span<char> cacheKey, T? value, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) =>
        this is ISpanKeyCache spanKeyCache
            ? spanKeyCache.SetAsync(cacheKey, value, expiration, policy, token)
            : SetAsync(new CacheKey(cacheKey), value, expiration, policy, token);

    /// <inheritdoc cref="ISpanKeyCache.SetAsync{T}(Span{char}, T, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<bool> SetAsync<T>(Span<char> cacheKey, T? value, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) =>
        this is ISpanKeyCache spanKeyCache
            ? spanKeyCache.SetAsync(cacheKey, value, expiration, policy, token)
            : SetAsync(new CacheKey(cacheKey), value, expiration, policy, token);

    ValueTask<bool> SetAsync<T>(KeyValuePair<CacheKey, T?>[] keyValues, CachePolicy? policy, CancellationToken token = default);

    ValueTask<bool> SetAsync<T>(KeyValuePair<CacheKey, T?>[] keyValues, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default);

    ValueTask<bool> SetAsync<T>(KeyValuePair<CacheKey, T?>[] keyValues, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default);

    /// <summary>
    /// Writes <paramref name="value"/> only if <paramref name="cacheKey"/> is absent. Redis
    /// <c>SET … NX</c>, one atomic round-trip.
    /// </summary>
    /// <returns>
    /// <c>true</c> only if this call created the key. <c>false</c> conflates "it existed" with "the
    /// write could not be completed", deliberately and fail-closed. Never deletes; not a lock.
    /// </returns>
    ValueTask<bool> TryAddAsync<T>(CacheKey cacheKey, T? value, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="TryAddAsync{T}(CacheKey, T, CachePolicy, CancellationToken)"/>
    /// <remarks>Builds the key from the text, normalized as <c>new CacheKey(text)</c> normalizes it.</remarks>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<bool> TryAddAsync<T>(Span<char> cacheKey, T? value, CachePolicy? policy, CancellationToken token = default) =>
        TryAddAsync(new CacheKey(cacheKey), value, policy, token);

    /// <inheritdoc cref="TryAddAsync{T}(CacheKey, T, CachePolicy, CancellationToken)"/>
    /// <param name="expiration">
    /// Lifetime of the entry if it is created, applied by the same atomic command. Must be a
    /// positive duration; to inherit <c>CachePolicy.DistributedExpiration</c> and then the cache
    /// default, call the overload without this parameter.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="expiration"/> is not positive.</exception>
    ValueTask<bool> TryAddAsync<T>(CacheKey cacheKey, T? value, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="TryAddAsync{T}(CacheKey, T, TimeSpan, CachePolicy, CancellationToken)"/>
    /// <remarks>Builds the key from the text, normalized as <c>new CacheKey(text)</c> normalizes it.</remarks>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<bool> TryAddAsync<T>(Span<char> cacheKey, T? value, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) =>
        TryAddAsync(new CacheKey(cacheKey), value, expiration, policy, token);

    /// <inheritdoc cref="TryAddAsync{T}(CacheKey, T, TimeSpan, CachePolicy, CancellationToken)"/>
    ValueTask<bool> TryAddAsync<T>(CacheKey cacheKey, T? value, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="TryAddAsync{T}(CacheKey, T, DateTimeOffset, CachePolicy, CancellationToken)"/>
    /// <remarks>Builds the key from the text, normalized as <c>new CacheKey(text)</c> normalizes it.</remarks>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<bool> TryAddAsync<T>(Span<char> cacheKey, T? value, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) =>
        TryAddAsync(new CacheKey(cacheKey), value, expiration, policy, token);

    ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="RefreshAsync{T}(CacheKey, CachePolicy, CancellationToken)"/>
    /// <remarks>Builds the key from the text, normalized as <c>new CacheKey(text)</c> normalizes it.</remarks>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<bool> RefreshAsync<T>(Span<char> cacheKey, CachePolicy? policy, CancellationToken token = default) =>
        RefreshAsync<T>(new CacheKey(cacheKey), policy, token);

    ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="RefreshAsync{T}(CacheKey, TimeSpan, CachePolicy, CancellationToken)"/>
    /// <remarks>Builds the key from the text, normalized as <c>new CacheKey(text)</c> normalizes it.</remarks>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<bool> RefreshAsync<T>(Span<char> cacheKey, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) =>
        RefreshAsync<T>(new CacheKey(cacheKey), expiration, policy, token);

    ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="RefreshAsync{T}(CacheKey, DateTimeOffset, CachePolicy, CancellationToken)"/>
    /// <remarks>Builds the key from the text, normalized as <c>new CacheKey(text)</c> normalizes it.</remarks>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<bool> RefreshAsync<T>(Span<char> cacheKey, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) =>
        RefreshAsync<T>(new CacheKey(cacheKey), expiration, policy, token);

    ValueTask<bool> ContainsAsync<T>(CacheKey cacheKey, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.GetAsync{T}(Span{char}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<bool> ContainsAsync<T>(Span<char> cacheKey, CancellationToken token = default) =>
        this is ISpanKeyCache spanKeyCache
            ? spanKeyCache.ContainsAsync<T>(cacheKey, token)
            : ContainsAsync<T>(new CacheKey(cacheKey), token);

    ValueTask<TimeSpan?> TimeToLiveAsync<T>(CacheKey cacheKey, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.GetAsync{T}(Span{char}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<TimeSpan?> TimeToLiveAsync<T>(Span<char> cacheKey, CancellationToken token = default) =>
        this is ISpanKeyCache spanKeyCache
            ? spanKeyCache.TimeToLiveAsync<T>(cacheKey, token)
            : TimeToLiveAsync<T>(new CacheKey(cacheKey), token);

    ValueTask<DateTimeOffset?> ExpireTimeAsync<T>(CacheKey cacheKey, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.GetAsync{T}(Span{char}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<DateTimeOffset?> ExpireTimeAsync<T>(Span<char> cacheKey, CancellationToken token = default) =>
        this is ISpanKeyCache spanKeyCache
            ? spanKeyCache.ExpireTimeAsync<T>(cacheKey, token)
            : ExpireTimeAsync<T>(new CacheKey(cacheKey), token);

    /// <summary>Reads, and on a miss runs <paramref name="generator"/> as <see cref="GetOrAddAsync{T}(CacheKey, Func{CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/> does, storing the value until the expiration it returns; an expiration that has already passed stores nothing.</summary>
    ValueTask<T?> GetOrAddWithExpirationAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<GeneratedValue<T>>> generator, CachePolicy? policy, CancellationToken token = default);

    /// <summary>Reads, and on a miss runs <paramref name="generator"/> with <paramref name="state"/>, storing the value until the expiration it returns.</summary>
    ValueTask<T?> GetOrAddWithExpirationAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<GeneratedValue<T>>> generator, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.GetOrAddWithExpirationAsync{T}(Span{char}, Func{CancellationToken, Task{GeneratedValue{T}}}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<T?> GetOrAddWithExpirationAsync<T>(Span<char> cacheKey, Func<CancellationToken, Task<GeneratedValue<T>>> generator, CachePolicy? policy, CancellationToken token = default) =>
        this is ISpanKeyCache spanKeyCache
            ? spanKeyCache.GetOrAddWithExpirationAsync(cacheKey, generator, policy, token)
            : GetOrAddWithExpirationAsync(new CacheKey(cacheKey), generator, policy, token);

    /// <inheritdoc cref="ISpanKeyCache.GetOrAddWithExpirationAsync{T}(Span{char}, Func{CancellationToken, Task{GeneratedValue{T}}}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<T?> GetOrAddWithExpirationAsync<T, TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<GeneratedValue<T>>> generator, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return this is ISpanKeyCache spanKeyCache
            ? spanKeyCache.GetOrAddWithExpirationAsync(cacheKey, state, generator, policy, token)
            : GetOrAddWithExpirationAsync(new CacheKey(cacheKey), state, generator, policy, token);
    }
}
