namespace UiPath.Caching;

internal sealed partial class MultilayerCache
{
    [OverloadResolutionPriority(1)]
    public ValueTask<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        policy ??= _defaultPolicy;
        // One floored resolution for the write and the rehydrate threshold; the miss path draws the jittered write deadline, so a hit never reads the clock.
        return GetOrAddInternalAsync(cacheKey, generator, expiration: null, ResolveDuration(policy), policy.JitterMaxDuration, policy, _localMemorySetter.Writes, localMissed: false, token);
    }

    [OverloadResolutionPriority(1)]
    public ValueTask<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var (writeExpiration, duration) = CallerWrite(expiration);
        return GetOrAddInternalAsync(cacheKey, generator, writeExpiration, duration, rehydrateJitter: null, policy ?? _defaultPolicy, _localMemorySetter.Writes, localMissed: false, token);
    }

    [OverloadResolutionPriority(1)]
    public ValueTask<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var (writeExpiration, duration) = CallerWrite(expiration);
        return GetOrAddInternalAsync(cacheKey, generator, writeExpiration, duration, rehydrateJitter: null, policy ?? _defaultPolicy, _localMemorySetter.Writes, localMissed: false, token);
    }

    public ValueTask<T?> GetOrAddAsync<T>(Span<char> cacheKey, Func<CancellationToken, Task<T?>> generator, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var seen = _localMemorySetter.Writes;
        var probe = ProbeGetOrAddLocal<T>(cacheKey, policy ?? _defaultPolicy, token, out var value);
        return probe == LocalProbe.Hit
            ? new ValueTask<T?>(value)
            : GetOrAddResolvedAsync(new CacheKey(cacheKey), generator, policy, seen, probe == LocalProbe.Miss, token);
    }

    public ValueTask<T?> GetOrAddAsync<T>(Span<char> cacheKey, Func<CancellationToken, Task<T?>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var (writeExpiration, duration) = CallerWrite(expiration);
        var seen = _localMemorySetter.Writes;
        var probe = ProbeGetOrAddLocal<T>(cacheKey, policy ?? _defaultPolicy, token, out var value);
        return probe == LocalProbe.Hit
            ? new ValueTask<T?>(value)
            : GetOrAddInternalAsync(new CacheKey(cacheKey), generator, writeExpiration, duration, rehydrateJitter: null, policy ?? _defaultPolicy, seen, probe == LocalProbe.Miss, token);
    }

    public ValueTask<T?> GetOrAddAsync<T>(Span<char> cacheKey, Func<CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var (writeExpiration, duration) = CallerWrite(expiration);
        var seen = _localMemorySetter.Writes;
        var probe = ProbeGetOrAddLocal<T>(cacheKey, policy ?? _defaultPolicy, token, out var value);
        return probe == LocalProbe.Hit
            ? new ValueTask<T?>(value)
            : GetOrAddInternalAsync(new CacheKey(cacheKey), generator, writeExpiration, duration, rehydrateJitter: null, policy ?? _defaultPolicy, seen, probe == LocalProbe.Miss, token);
    }

    public ValueTask<T?> GetOrAddAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        policy ??= _defaultPolicy;
        return GetOrAddInternalAsync(cacheKey, state, generator, expiration: null, ResolveDuration(policy), policy.JitterMaxDuration, policy, _localMemorySetter.Writes, localMissed: false, token);
    }

    public ValueTask<T?> GetOrAddAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var (writeExpiration, duration) = CallerWrite(expiration);
        return GetOrAddInternalAsync(cacheKey, state, generator, writeExpiration, duration, rehydrateJitter: null, policy ?? _defaultPolicy, _localMemorySetter.Writes, localMissed: false, token);
    }

    public ValueTask<T?> GetOrAddAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var (writeExpiration, duration) = CallerWrite(expiration);
        return GetOrAddInternalAsync(cacheKey, state, generator, writeExpiration, duration, rehydrateJitter: null, policy ?? _defaultPolicy, _localMemorySetter.Writes, localMissed: false, token);
    }

    public ValueTask<T?> GetOrAddAsync<T, TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var seen = _localMemorySetter.Writes;
        var probe = ProbeGetOrAddLocal<T>(cacheKey, policy ?? _defaultPolicy, token, out var value);
        return probe == LocalProbe.Hit
            ? new ValueTask<T?>(value)
            : GetOrAddResolvedAsync(new CacheKey(cacheKey), state, generator, policy, seen, probe == LocalProbe.Miss, token);
    }

    public ValueTask<T?> GetOrAddAsync<T, TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var (writeExpiration, duration) = CallerWrite(expiration);
        var seen = _localMemorySetter.Writes;
        var probe = ProbeGetOrAddLocal<T>(cacheKey, policy ?? _defaultPolicy, token, out var value);
        return probe == LocalProbe.Hit
            ? new ValueTask<T?>(value)
            : GetOrAddInternalAsync(new CacheKey(cacheKey), state, generator, writeExpiration, duration, rehydrateJitter: null, policy ?? _defaultPolicy, seen, probe == LocalProbe.Miss, token);
    }

    public ValueTask<T?> GetOrAddAsync<T, TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var (writeExpiration, duration) = CallerWrite(expiration);
        var seen = _localMemorySetter.Writes;
        var probe = ProbeGetOrAddLocal<T>(cacheKey, policy ?? _defaultPolicy, token, out var value);
        return probe == LocalProbe.Hit
            ? new ValueTask<T?>(value)
            : GetOrAddInternalAsync(new CacheKey(cacheKey), state, generator, writeExpiration, duration, rehydrateJitter: null, policy ?? _defaultPolicy, seen, probe == LocalProbe.Miss, token);
    }

    public ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<T, TState>(KeyValuePair<CacheKey, TState>[] entries, Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, CachePolicy? policy, CancellationToken token = default)
        where TState : notnull
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(generator);
        policy ??= _defaultPolicy;
        // One floored resolution for the write and the rehydrate threshold; only the write is jittered.
        var duration = ResolveDuration(policy);
        var writeExpiration = _clock.ToDateTimeOffset(ApplyJitter(duration, policy.JitterMaxDuration));
        return GetOrAddBatchInternalAsync<T, TState, Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>>>(entries, generator, DirectBatch<T, TState>.Invoke, writeExpiration, duration, policy.JitterMaxDuration, policy, token);
    }

    public ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<T, TState>(KeyValuePair<CacheKey, TState>[] entries, Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default)
        where TState : notnull
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(generator);
        var (writeExpiration, duration) = CallerWrite(expiration);
        return GetOrAddBatchInternalAsync<T, TState, Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>>>(entries, generator, DirectBatch<T, TState>.Invoke, writeExpiration, duration, rehydrateJitter: null, policy ?? _defaultPolicy, token);
    }

    public ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<T, TState>(KeyValuePair<CacheKey, TState>[] entries, Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default)
        where TState : notnull
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(generator);
        var (writeExpiration, duration) = CallerWrite(expiration);
        return GetOrAddBatchInternalAsync<T, TState, Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>>>(entries, generator, DirectBatch<T, TState>.Invoke, writeExpiration, duration, rehydrateJitter: null, policy ?? _defaultPolicy, token);
    }

    public ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<T, TState, TArg>(KeyValuePair<CacheKey, TState>[] entries, TArg argument, Func<TArg, TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, CachePolicy? policy, CancellationToken token = default)
        where TState : notnull
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(generator);
        policy ??= _defaultPolicy;
        // One floored resolution for the write and the rehydrate threshold; only the write is jittered.
        var duration = ResolveDuration(policy);
        var writeExpiration = _clock.ToDateTimeOffset(ApplyJitter(duration, policy.JitterMaxDuration));
        return GetOrAddBatchInternalAsync<T, TState, TArg>(entries, argument, generator, writeExpiration, duration, policy.JitterMaxDuration, policy, token);
    }

    public ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<T, TState, TArg>(KeyValuePair<CacheKey, TState>[] entries, TArg argument, Func<TArg, TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default)
        where TState : notnull
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(generator);
        var (writeExpiration, duration) = CallerWrite(expiration);
        return GetOrAddBatchInternalAsync<T, TState, TArg>(entries, argument, generator, writeExpiration, duration, rehydrateJitter: null, policy ?? _defaultPolicy, token);
    }

    public ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<T, TState, TArg>(KeyValuePair<CacheKey, TState>[] entries, TArg argument, Func<TArg, TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default)
        where TState : notnull
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(generator);
        var (writeExpiration, duration) = CallerWrite(expiration);
        return GetOrAddBatchInternalAsync<T, TState, TArg>(entries, argument, generator, writeExpiration, duration, rehydrateJitter: null, policy ?? _defaultPolicy, token);
    }

    public ValueTask<T?> GetOrAddWithExpirationAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<GeneratedValue<T>>> generator, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return GetOrAddGeneratedAsync(cacheKey, generator, policy ?? _defaultPolicy, _localMemorySetter.Writes, localMissed: false, token);
    }

    public ValueTask<T?> GetOrAddWithExpirationAsync<T>(Span<char> cacheKey, Func<CancellationToken, Task<GeneratedValue<T>>> generator, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        policy ??= _defaultPolicy;
        var seen = _localMemorySetter.Writes;
        var probe = ProbeGetOrAddLocal<T>(cacheKey, policy, token, out var value);
        return probe == LocalProbe.Hit
            ? new ValueTask<T?>(value)
            : GetOrAddGeneratedAsync(new CacheKey(cacheKey), generator, policy, seen, probe == LocalProbe.Miss, token);
    }

    public ValueTask<T?> GetOrAddWithExpirationAsync<T, TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<GeneratedValue<T>>> generator, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        policy ??= _defaultPolicy;
        var seen = _localMemorySetter.Writes;
        var probe = ProbeGetOrAddLocal<T>(cacheKey, policy, token, out var value);
        return probe == LocalProbe.Hit
            ? new ValueTask<T?>(value)
            : GetOrAddGeneratedAsync(new CacheKey(cacheKey), state, generator, policy, seen, probe == LocalProbe.Miss, token);
    }

    public ValueTask<T?> GetOrAddWithExpirationAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<GeneratedValue<T>>> generator, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return GetOrAddGeneratedAsync(cacheKey, state, generator, policy ?? _defaultPolicy, _localMemorySetter.Writes, localMissed: false, token);
    }

    private static Func<CancellationToken, Task<T?>> Bind<T, TState>(TState state, Func<TState, CancellationToken, Task<T?>> generator) =>
        token => generator(state, token);

    private ValueTask<T?> GetOrAddResolvedAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, CachePolicy? policy, long seen, bool localMissed, CancellationToken token)
    {
        policy ??= _defaultPolicy;
        return GetOrAddInternalAsync(cacheKey, generator, expiration: null, ResolveDuration(policy), policy.JitterMaxDuration, policy, seen, localMissed, token);
    }

    private ValueTask<T?> GetOrAddResolvedAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, CachePolicy? policy, long seen, bool localMissed, CancellationToken token)
    {
        policy ??= _defaultPolicy;
        return GetOrAddInternalAsync(cacheKey, state, generator, expiration: null, ResolveDuration(policy), policy.JitterMaxDuration, policy, seen, localMissed, token);
    }

    /// <param name="expiration">The caller's write deadline, or null for a policy write, whose jittered deadline the miss path draws.</param>
    /// <param name="seen">The local writes counter, read before the caller's first read of the local tier.</param>
    /// <param name="localMissed">True when that read found nothing under the key, so the miss path need not read it again.</param>
    private ValueTask<T?> GetOrAddInternalAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, DateTimeOffset? expiration, TimeSpan effectiveDuration, TimeSpan? rehydrateJitter, CachePolicy policy, long seen, bool localMissed, CancellationToken token)
    {
        // Not async: the miss path's lock delegates capture these parameters, and an async method would build that closure on a hit too.
        NotCacheableException.ThrowIfNotCacheable<T>();
        if (!localMissed && !token.IsCancellationRequested)
        {
            var probe = ProbeLocal<T?>(cacheKey, token, out var local);
            if (probe == LocalProbe.Hit && local!.Found)
            {
                Layered<T>().TryRehydrate(cacheKey, local.Expiration, local.Value, generator, policy, effectiveDuration, rehydrateJitter);
                return new ValueTask<T?>(local.Value);
            }

            localMissed = probe == LocalProbe.Miss;
        }

        if (localMissed && !_sharedInner)
        {
            return GenerateLocalOnlyAsync<T, Func<CancellationToken, Task<T?>>>(cacheKey, generator, static (cache, options, g, p, scope) => cache.RunGeneratorAndStoreEntryAsync(options, g, static (f, ct) => f(ct), p, scope, localOnly: true), expiration, effectiveDuration, rehydrateJitter, policy, seen, token);
        }

        return GetOrAddCoreAsync(cacheKey, generator, expiration, effectiveDuration, rehydrateJitter, policy, seen, localMissed, token);
    }

    private ValueTask<T?> GetOrAddInternalAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, DateTimeOffset? expiration, TimeSpan effectiveDuration, TimeSpan? rehydrateJitter, CachePolicy policy, long seen, bool localMissed, CancellationToken token)
    {
        // Binds the state to a delegate only where one is needed, so a hit builds no closure.
        NotCacheableException.ThrowIfNotCacheable<T>();
        if (!localMissed && !token.IsCancellationRequested)
        {
            var probe = ProbeLocal<T?>(cacheKey, token, out var local);
            if (probe == LocalProbe.Hit && local!.Found)
            {
                var layered = Layered<T>();
                if (layered.ShouldRehydrate(local.Value, policy, effectiveDuration))
                {
                    layered.TriggerRehydrate(cacheKey, local.Expiration, Bind(state, generator), policy, effectiveDuration, rehydrateJitter);
                }
                return new ValueTask<T?>(local.Value);
            }

            localMissed = probe == LocalProbe.Miss;
        }

        if (localMissed && !_sharedInner)
        {
            return GenerateLocalOnlyAsync<T, (TState State, Func<TState, CancellationToken, Task<T?>> Generator)>(cacheKey, (state, generator), static (cache, options, g, p, scope) => cache.RunGeneratorAndStoreEntryAsync(options, g.State, g.Generator, p, scope, localOnly: true), expiration, effectiveDuration, rehydrateJitter, policy, seen, token);
        }

        return GetOrAddCoreAsync(cacheKey, Bind(state, generator), expiration, effectiveDuration, rehydrateJitter, policy, seen, localMissed, token);
    }

    private ValueTask<T?> GetOrAddCoreAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, DateTimeOffset? expiration, TimeSpan effectiveDuration, TimeSpan? rehydrateJitter, CachePolicy policy, long seen, bool localMissed, CancellationToken token)
    {
        // Not async: a miss that never waits builds no state machine, and one that waits builds one, where an async method here would build one for the wait at every step.
        try
        {
            var options = _entryBuilder.BuildEntryOptions<T>(cacheKey, expiration ?? _clock.ToDateTimeOffset(ApplyJitter(effectiveDuration, rehydrateJitter)), token);
            if (TryGetCooling<T>(options.CacheKey.Name, policy, out var cooling))
            {
                return new ValueTask<T?>(cooling.Value);
            }

            var fetch = localMissed ? Layered<T>().FetchInnerAsync(options, policy) : GetCacheEntryInnerAsync<T>(options, policy);
            var miss = new Miss<T>(cacheKey, options, generator, effectiveDuration, rehydrateJitter, policy, seen, localMissed);
            if (fetch.IsCompletedSuccessfully)
            {
                return GenerateOnMissAsync(miss, fetch.Result, token);
            }

            return AwaitFetchAsync(miss, fetch, token);
        }
        catch (Exception ex)
        {
            return FailedAsync<T>(ex);
        }
    }

    private ValueTask<T?> GenerateOnMissAsync<T>(Miss<T> miss, ICacheEntry<T?> entry, CancellationToken token)
    {
        if (entry.Found)
        {
            Layered<T>().TryRehydrate(miss.Key, entry.Expiration, entry.Value, miss.Generator, miss.Policy, miss.Duration, miss.RehydrateJitter);
            return new ValueTask<T?>(entry.Value);
        }

        var generated = GetOrGenerateEntryAsync<T, Func<CancellationToken, Task<T?>>>(miss.Options, miss.Generator, miss.Policy, static (cache, options, g, p, scope) => cache.RunGeneratorAndStoreEntryAsync(options, g, p, scope), miss.Seen, miss.InnerMissed, token);
        if (generated.IsCompletedSuccessfully)
        {
            return new ValueTask<T?>(((ICacheEntry<T?>)generated.Result!).Value);
        }

        return ValueOfAsync<T>(generated);
    }

    private async ValueTask<T?> AwaitFetchAsync<T>(Miss<T> miss, ValueTask<ICacheEntry<T?>> fetch, CancellationToken token)
    {
        var entry = await fetch.ConfigureAwait(false);
        return await GenerateOnMissAsync(miss, entry, token).ConfigureAwait(false);
    }

    /// <summary>A local miss with no inner tier: nothing to read, so the key's run generates at once and keeps the value locally. The state rides in <typeparamref name="TGenerator"/>, so no closure binds it.</summary>
    private ValueTask<T?> GenerateLocalOnlyAsync<T, TGenerator>(
        CacheKey cacheKey,
        TGenerator generator,
        Func<MultilayerCache, CacheEntryOptions, TGenerator, CachePolicy, GenerationScope, ValueTask<object?>> generate,
        DateTimeOffset? expiration,
        TimeSpan effectiveDuration,
        TimeSpan? rehydrateJitter,
        CachePolicy policy,
        long seen,
        CancellationToken token)
    {
        try
        {
            var options = _entryBuilder.BuildEntryOptions<T>(cacheKey, expiration ?? _clock.ToDateTimeOffset(ApplyJitter(effectiveDuration, rehydrateJitter)), token);
            if (TryGetCooling<T>(options.CacheKey.Name, policy, out var cooling))
            {
                return new ValueTask<T?>(cooling.Value);
            }

            var generated = GetOrGenerateEntryAsync<T, TGenerator>(options, generator, policy, generate, seen, innerMissed: true, token);
            if (generated.IsCompletedSuccessfully)
            {
                return new ValueTask<T?>(((ICacheEntry<T?>)generated.Result!).Value);
            }

            return ValueOfAsync<T>(generated);
        }
        catch (Exception ex)
        {
            return FailedAsync<T>(ex);
        }
    }
}
