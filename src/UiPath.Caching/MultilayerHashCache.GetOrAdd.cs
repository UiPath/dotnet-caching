namespace UiPath.Caching;

internal sealed partial class MultilayerHashCache
{
    [OverloadResolutionPriority(1)]
    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        policy ??= _defaultPolicy;
        // One floored resolution for the write and the rehydrate threshold; the miss path draws the jittered write deadline, so a hit never reads the clock.
        return GetOrAddInternalAsync(cacheKey, generator, expiration: null, ResolveDuration(policy), policy.JitterMaxDuration, HashCacheSetOption.KeyReplace, policy, _localMemorySetter.Writes, localMissed: false, token);
    }

    [OverloadResolutionPriority(1)]
    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var (writeExpiration, duration) = CallerWrite(expiration);
        return GetOrAddInternalAsync(cacheKey, generator, writeExpiration, duration, rehydrateJitter: null, HashCacheSetOption.KeyReplace, policy ?? _defaultPolicy, _localMemorySetter.Writes, localMissed: false, token);
    }

    [OverloadResolutionPriority(1)]
    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var (writeExpiration, duration) = CallerWrite(expiration);
        return GetOrAddInternalAsync(cacheKey, generator, writeExpiration, duration, rehydrateJitter: null, HashCacheSetOption.KeyReplace, policy ?? _defaultPolicy, _localMemorySetter.Writes, localMissed: false, token);
    }

    /// <summary>
    /// Never returns <c>null</c>. A cache hit (real data or cached-empty marker) returns the stored
    /// dictionary or an empty one; a cache miss invokes the generator and returns its result. The inner
    /// cache may legally return <c>Found=true</c> with <c>Value=null</c> when only the
    /// <c>_metadata_</c>-as-empty-marker is present; we collapse that to <see cref="Empty{T}"/> for the
    /// caller, who can always iterate the result without a null check.
    /// </summary>
    [OverloadResolutionPriority(1)]
    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, HashCacheSetOption? setOption, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var (writeExpiration, duration) = CallerWrite(expiration);
        return GetOrAddInternalAsync(cacheKey, generator, writeExpiration, duration, rehydrateJitter: null, setOption ?? HashCacheSetOption.KeyReplace, policy ?? _defaultPolicy, _localMemorySetter.Writes, localMissed: false, token);
    }

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(Span<char> cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var seen = _localMemorySetter.Writes;
        var probe = ProbeGetOrAddLocal<T>(cacheKey, policy ?? _defaultPolicy, token, out var values);
        return probe == LocalProbe.Hit
            ? new ValueTask<IDictionary<string, T?>>(values!)
            : GetOrAddResolvedAsync(new CacheKey(cacheKey), generator, policy, seen, probe == LocalProbe.Miss, token);
    }

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(Span<char> cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var (writeExpiration, duration) = CallerWrite(expiration);
        var seen = _localMemorySetter.Writes;
        var probe = ProbeGetOrAddLocal<T>(cacheKey, policy ?? _defaultPolicy, token, out var values);
        return probe == LocalProbe.Hit
            ? new ValueTask<IDictionary<string, T?>>(values!)
            : GetOrAddInternalAsync(new CacheKey(cacheKey), generator, writeExpiration, duration, rehydrateJitter: null, HashCacheSetOption.KeyReplace, policy ?? _defaultPolicy, seen, probe == LocalProbe.Miss, token);
    }

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(Span<char> cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var (writeExpiration, duration) = CallerWrite(expiration);
        var seen = _localMemorySetter.Writes;
        var probe = ProbeGetOrAddLocal<T>(cacheKey, policy ?? _defaultPolicy, token, out var values);
        return probe == LocalProbe.Hit
            ? new ValueTask<IDictionary<string, T?>>(values!)
            : GetOrAddInternalAsync(new CacheKey(cacheKey), generator, writeExpiration, duration, rehydrateJitter: null, HashCacheSetOption.KeyReplace, policy ?? _defaultPolicy, seen, probe == LocalProbe.Miss, token);
    }

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(Span<char> cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, HashCacheSetOption? setOption, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var (writeExpiration, duration) = CallerWrite(expiration);
        var seen = _localMemorySetter.Writes;
        var probe = ProbeGetOrAddLocal<T>(cacheKey, policy ?? _defaultPolicy, token, out var values);
        return probe == LocalProbe.Hit
            ? new ValueTask<IDictionary<string, T?>>(values!)
            : GetOrAddInternalAsync(new CacheKey(cacheKey), generator, writeExpiration, duration, rehydrateJitter: null, setOption ?? HashCacheSetOption.KeyReplace, policy ?? _defaultPolicy, seen, probe == LocalProbe.Miss, token);
    }

    [OverloadResolutionPriority(1)]
    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        policy ??= _defaultPolicy;
        return GetOrAddInternalAsync(cacheKey, state, generator, expiration: null, ResolveDuration(policy), policy.JitterMaxDuration, HashCacheSetOption.KeyReplace, policy, _localMemorySetter.Writes, localMissed: false, token);
    }

    [OverloadResolutionPriority(1)]
    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var (writeExpiration, duration) = CallerWrite(expiration);
        return GetOrAddInternalAsync(cacheKey, state, generator, writeExpiration, duration, rehydrateJitter: null, HashCacheSetOption.KeyReplace, policy ?? _defaultPolicy, _localMemorySetter.Writes, localMissed: false, token);
    }

    [OverloadResolutionPriority(1)]
    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var (writeExpiration, duration) = CallerWrite(expiration);
        return GetOrAddInternalAsync(cacheKey, state, generator, writeExpiration, duration, rehydrateJitter: null, HashCacheSetOption.KeyReplace, policy ?? _defaultPolicy, _localMemorySetter.Writes, localMissed: false, token);
    }

    [OverloadResolutionPriority(1)]
    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, HashCacheSetOption? setOption, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var (writeExpiration, duration) = CallerWrite(expiration);
        return GetOrAddInternalAsync(cacheKey, state, generator, writeExpiration, duration, rehydrateJitter: null, setOption ?? HashCacheSetOption.KeyReplace, policy ?? _defaultPolicy, _localMemorySetter.Writes, localMissed: false, token);
    }

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T, TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var seen = _localMemorySetter.Writes;
        var probe = ProbeGetOrAddLocal<T>(cacheKey, policy ?? _defaultPolicy, token, out var values);
        return probe == LocalProbe.Hit
            ? new ValueTask<IDictionary<string, T?>>(values!)
            : GetOrAddResolvedAsync(new CacheKey(cacheKey), state, generator, policy, seen, probe == LocalProbe.Miss, token);
    }

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T, TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var (writeExpiration, duration) = CallerWrite(expiration);
        var seen = _localMemorySetter.Writes;
        var probe = ProbeGetOrAddLocal<T>(cacheKey, policy ?? _defaultPolicy, token, out var values);
        return probe == LocalProbe.Hit
            ? new ValueTask<IDictionary<string, T?>>(values!)
            : GetOrAddInternalAsync(new CacheKey(cacheKey), state, generator, writeExpiration, duration, rehydrateJitter: null, HashCacheSetOption.KeyReplace, policy ?? _defaultPolicy, seen, probe == LocalProbe.Miss, token);
    }

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T, TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var (writeExpiration, duration) = CallerWrite(expiration);
        var seen = _localMemorySetter.Writes;
        var probe = ProbeGetOrAddLocal<T>(cacheKey, policy ?? _defaultPolicy, token, out var values);
        return probe == LocalProbe.Hit
            ? new ValueTask<IDictionary<string, T?>>(values!)
            : GetOrAddInternalAsync(new CacheKey(cacheKey), state, generator, writeExpiration, duration, rehydrateJitter: null, HashCacheSetOption.KeyReplace, policy ?? _defaultPolicy, seen, probe == LocalProbe.Miss, token);
    }

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T, TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, HashCacheSetOption? setOption, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var (writeExpiration, duration) = CallerWrite(expiration);
        var seen = _localMemorySetter.Writes;
        var probe = ProbeGetOrAddLocal<T>(cacheKey, policy ?? _defaultPolicy, token, out var values);
        return probe == LocalProbe.Hit
            ? new ValueTask<IDictionary<string, T?>>(values!)
            : GetOrAddInternalAsync(new CacheKey(cacheKey), state, generator, writeExpiration, duration, rehydrateJitter: null, setOption ?? HashCacheSetOption.KeyReplace, policy ?? _defaultPolicy, seen, probe == LocalProbe.Miss, token);
    }

    private ValueTask<IDictionary<string, T?>> GetOrAddResolvedAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, CachePolicy? policy, long seen, bool localMissed, CancellationToken token)
    {
        policy ??= _defaultPolicy;
        return GetOrAddInternalAsync(cacheKey, generator, expiration: null, ResolveDuration(policy), policy.JitterMaxDuration, HashCacheSetOption.KeyReplace, policy, seen, localMissed, token);
    }

    private ValueTask<IDictionary<string, T?>> GetOrAddResolvedAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, CachePolicy? policy, long seen, bool localMissed, CancellationToken token)
    {
        policy ??= _defaultPolicy;
        return GetOrAddInternalAsync(cacheKey, state, generator, expiration: null, ResolveDuration(policy), policy.JitterMaxDuration, HashCacheSetOption.KeyReplace, policy, seen, localMissed, token);
    }

    private ValueTask<IDictionary<string, T?>> GetOrAddInternalAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset? expiration, TimeSpan effectiveDuration, TimeSpan? rehydrateJitter, HashCacheSetOption setOption, CachePolicy policy, long seen, bool localMissed, CancellationToken token)
    {
        // Binds the state to a delegate only where one is needed, so a hit builds no closure.
        ArgumentNullException.ThrowIfNull(generator);
        NotCacheableException.ThrowIfNotCacheable<T>();
        if (!localMissed && !token.IsCancellationRequested)
        {
            var probe = ProbeLocal<T>(cacheKey, out var local);
            if (probe == LocalProbe.Hit && local!.Found)
            {
                var layered = Layered<T>();
                if (layered.ShouldRehydrate(local.Value, policy, effectiveDuration))
                {
                    layered.TriggerRehydrate(cacheKey, local.Expiration, StatefulCacheExtensions.BindHash(state, generator), policy, effectiveDuration, rehydrateJitter);
                }

                return new ValueTask<IDictionary<string, T?>>(local.Value ?? Empty<T>());
            }

            localMissed = probe == LocalProbe.Miss;
        }

        return GetOrAddCoreAsync(cacheKey, StatefulCacheExtensions.BindHash(state, generator), expiration, effectiveDuration, rehydrateJitter, setOption, policy, seen, localMissed, token);
    }

    /// <param name="expiration">The caller's write deadline, or null for a policy write, whose jittered deadline the miss path draws.</param>
    /// <param name="seen">The local writes counter, read before the caller's first read of the local tier.</param>
    /// <param name="localMissed">True when that read found nothing under the key, so the miss path need not read it again.</param>
    private ValueTask<IDictionary<string, T?>> GetOrAddInternalAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset? expiration, TimeSpan effectiveDuration, TimeSpan? rehydrateJitter, HashCacheSetOption setOption, CachePolicy policy, long seen, bool localMissed, CancellationToken token)
    {
        // Not async: the miss path's lock delegates capture these parameters, and an async method would build that closure on a hit too.
        NotCacheableException.ThrowIfNotCacheable<T>();
        if (!localMissed && !token.IsCancellationRequested)
        {
            var probe = ProbeLocal<T>(cacheKey, out var local);
            if (probe == LocalProbe.Hit && local!.Found)
            {
                Layered<T>().TryRehydrate(cacheKey, local.Expiration, local.Value, generator, policy, effectiveDuration, rehydrateJitter);
                return new ValueTask<IDictionary<string, T?>>(local.Value ?? Empty<T>());
            }

            localMissed = probe == LocalProbe.Miss;
        }

        return GetOrAddCoreAsync(cacheKey, generator, expiration, effectiveDuration, rehydrateJitter, setOption, policy, seen, localMissed, token);
    }

    private async ValueTask<IDictionary<string, T?>> GetOrAddCoreAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset? expiration, TimeSpan effectiveDuration, TimeSpan? rehydrateJitter, HashCacheSetOption setOption, CachePolicy policy, long seen, bool localMissed, CancellationToken token)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var cacheEntryOptions = _entryBuilder.BuildEntryOptions<T>(cacheKey, expiration ?? _clock.ToDateTimeOffset(ApplyJitter(effectiveDuration, rehydrateJitter)), setOption, token);
        var cacheEntry = await GetCacheEntryAsync<T>(cacheEntryOptions, policy, localMissed).ConfigureAwait(false);
        if (cacheEntry.Found)
        {
            Layered<T>().TryRehydrate(cacheKey, cacheEntry.Expiration, cacheEntry.Value, generator, policy, effectiveDuration, rehydrateJitter);
            return cacheEntry.Value ?? Empty<T>();
        }

        // A field the inner tier could not read would survive a write that only sets fields, and every node would miss on it again once its local copy expired.
        if (cacheEntry is UnreadableEntry<IDictionary<string, T?>> && cacheEntryOptions.SetOption == HashCacheSetOption.HashReplace)
        {
            cacheEntryOptions = cacheEntryOptions with { SetOption = HashCacheSetOption.KeyReplace };
        }

        var result = SharesRuns(policy.Lock)
            ? await RunFirstAsync(cacheEntryOptions, generator, policy, seen, token).ConfigureAwait(false)
            : (ICacheEntry<IDictionary<string, T?>>)(await GenerateGuardedAsync<T>(cacheEntryOptions, generator, policy, new GenerationScope(null, token)).ConfigureAwait(false))!;
        return result.Value ?? Empty<T>();
    }

    /// <summary>The generation itself: under the key's distributed lock when one is enabled, which the caller that runs it takes, holds until the run ends, and follows with a read of both tiers that may find what another process stored; otherwise the generator at once.</summary>
    private ValueTask<object?> GenerateGuardedAsync<T>(InternalHashCacheEntryOptions options, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, CachePolicy policy, GenerationScope scope) =>
        DistributedLockOn(policy.Lock)
            ? GenerateUnderDistributedLockAsync(options, generator, policy, scope)
            : RunHashGeneratorAndStoreEntryAsync(options, generator, policy, scope);

    private async ValueTask<object?> GenerateUnderDistributedLockAsync<T>(InternalHashCacheEntryOptions options, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, CachePolicy policy, GenerationScope scope)
    {
        await using var lease = await AcquireDistributedLockAsync(options.CacheKey, policy.Lock, scope.Token).ConfigureAwait(false);
        // Read on the run's token: the starter may have left while callers that joined still wait for this.
        var current = await GetCacheEntryAsync<T>(options with { Token = scope.Token }, policy, false).ConfigureAwait(false);
        if (current.Found)
        {
            return current;
        }

        // The first read was clean, so a field another policy wrote since is only found here.
        return await RunHashGeneratorAndStoreEntryAsync(current is UnreadableEntry<IDictionary<string, T?>> && options.SetOption == HashCacheSetOption.HashReplace ? options with { SetOption = HashCacheSetOption.KeyReplace } : options, generator, policy, scope).ConfigureAwait(false);
    }

    /// <summary>Takes the key's run before anything else and runs the generator inline in the caller that took it: a caller that arrives meanwhile joins the run at once; only the caller that runs it takes the distributed lock, if one is enabled.</summary>
    private async ValueTask<ICacheEntry<IDictionary<string, T?>>> RunFirstAsync<T>(InternalHashCacheEntryOptions options, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, CachePolicy policy, long seen, CancellationToken token) =>
        (ICacheEntry<IDictionary<string, T?>>)(await _generations.RunAsync(
            new InFlightKey(options.CacheKey.Name, typeof(T), null, null),
            (Cache: this, Options: options, Generator: generator, Policy: policy, Seen: seen),
            static (state, run) =>
                // A write in this process since the caller read the key may have left its value in the local tier, which a run writes before it leaves the table.
                state.Cache._localMemorySetter.Writes != state.Seen && state.Cache.TryGetLocalEntry<T>(state.Options, out var current)
                    ? new ValueTask<object?>(current)
                    : state.Cache.GenerateGuardedAsync<T>(state.Options, state.Generator, state.Policy, new GenerationScope(run, run.Token)),
            token).ConfigureAwait(false))!;

    /// <summary>A local hit only, never the inner tier: for a caller that has just taken a key, where the only thing that can have changed is a run of this process.</summary>
    private bool TryGetLocalEntry<T>(InternalHashCacheEntryOptions options, [MaybeNullWhen(false)] out ICacheEntry<IDictionary<string, T?>> entry)
    {
        if ((_connectionState.IsConnected || _useLocalOnlyWhenDisconnected) && _memoryCache.TryGetValue(options.CacheKey.Name, out entry) && entry!.Found)
        {
            MarkRead(entry);
            return true;
        }

        entry = default;
        return false;
    }

    private async ValueTask<object?> RunHashGeneratorAndStoreEntryAsync<T>(InternalHashCacheEntryOptions cacheEntryOptions, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, CachePolicy policy, GenerationScope scope)
    {
        LogCacheMissed(Logged(cacheEntryOptions, typeof(T)));
        var ret = await InvokeFactoryAsync(cacheEntryOptions.CacheKey, generator, policy.FactoryTimeout, scope.Token).ConfigureAwait(false);

        // A generator that ignores its token can finish after every caller left and a newer run took the key: its value must not replace theirs.
        if ((!IsNullOrEmpty(ret) || _multiLayerCacheOptions.CacheNullValues) && !scope.IsAbandoned)
        {
            var innerCacheDisconnected = GetInnerCacheDisconnected();
            // The starter's token, unless it cancelled while callers that joined still wait for the value: then the run's, which outlives it.
            var store = cacheEntryOptions.Token.IsCancellationRequested && !scope.IsAbandoned ? cacheEntryOptions with { Token = scope.Token } : cacheEntryOptions;
            // A peer that takes the distributed lock next must find the value.
            if (!innerCacheDisconnected && policy.BackgroundDistributedWrite == true && !DistributedLockOn(policy.Lock) && _background.TryReserve())
            {
                FillInBackground(store, ret ?? Empty<T>(), policy);
            }
            else
            {
                await Layered<T>().InternalSetAsync(store, ret ?? Empty<T>(), innerCacheDisconnected, policy, keepRefused: true).ConfigureAwait(false);
            }
        }
        return _cacheEntryFactory.Create<IDictionary<string, T?>>(ret ?? Empty<T>(), cacheEntryOptions.Expiration, cacheEntryOptions.Metadata);
    }
}
