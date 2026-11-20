namespace UiPath.Caching;

internal sealed partial class MultilayerCache
{
    /// <summary>A local hit only, never the inner tier: for a caller that has just taken a key, where the only thing that can have changed is a run of this process.</summary>
    private bool TryGetLocalEntry<T>(CacheEntryOptions options, [MaybeNullWhen(false)] out ICacheEntry<T?> entry)
    {
        if ((_connectionState.IsConnected || _useLocalOnlyWhenDisconnected) && _memoryCache.TryGetValue(options.CacheKey.Name, out entry) && entry!.Found)
        {
            MarkRead(entry);
            return true;
        }

        entry = default;
        return false;
    }

    /// <summary>The generated entry, as an <see cref="ICacheEntry{T}"/> the caller casts in its own frame: with the shared run off every caller generates for itself, under the distributed lock when one is enabled.</summary>
    private ValueTask<object?> GetOrGenerateEntryAsync<T, TGenerator>(
        CacheEntryOptions options,
        TGenerator generator,
        CachePolicy policy,
        Func<MultilayerCache, CacheEntryOptions, TGenerator, CachePolicy, GenerationScope, ValueTask<object?>> generate,
        long seen,
        bool innerMissed,
        CancellationToken token)
    {
        return SharesRuns(policy.Lock)
            ? RunFirstAsync<T, TGenerator>(options, generator, policy, generate, seen, innerMissed, token)
            : GenerateGuardedAsync<T, TGenerator>(options, generator, policy, generate, new GenerationScope(null, token), innerMissed);
    }

    /// <summary>The generation itself: under the key's distributed lock when one is enabled, which is taken by the caller that runs it, held until the run ends, and followed by a read of both tiers that may find what another process stored; otherwise the generator at once. The frame returned ends the shared run, when the scope carries one.</summary>
    /// <param name="innerMissed">The caller has just read the inner tier and missed, so with <c>DistributedLockSkipReread</c> a lock taken at its first attempt needs no second inner read.</param>
    private ValueTask<object?> GenerateGuardedAsync<T, TGenerator>(
        CacheEntryOptions options,
        TGenerator generator,
        CachePolicy policy,
        Func<MultilayerCache, CacheEntryOptions, TGenerator, CachePolicy, GenerationScope, ValueTask<object?>> generate,
        GenerationScope scope,
        bool innerMissed) =>
        DistributedLockOn(policy.Lock)
            ? GenerateUnderDistributedLockAsync<T, TGenerator>(options, generator, policy, generate, scope, innerMissed)
            : generate(this, options, generator, policy, scope);

    private async ValueTask<object?> GenerateUnderDistributedLockAsync<T, TGenerator>(
        CacheEntryOptions options,
        TGenerator generator,
        CachePolicy policy,
        Func<MultilayerCache, CacheEntryOptions, TGenerator, CachePolicy, GenerationScope, ValueTask<object?>> generate,
        GenerationScope scope,
        bool innerMissed)
    {
        object? result;
        try
        {
            var maySkip = innerMissed && SkipsRereadUnderLock(policy.Lock);
            var (lease, contended) = maySkip
                ? await AcquireDistributedLeaseAsync(options.CacheKey, policy.Lock, scope.Token).ConfigureAwait(false)
                : (await AcquireDistributedLockAsync(options.CacheKey, policy.Lock, scope.Token).ConfigureAwait(false), true);
            await using (lease)
            {
                // Opted in and uncontended after the caller's miss: only a peer that stored and released in the round trip since that read is missed, and generates twice.
                // Otherwise read on the run's token: the starter may have left while callers that joined still wait for this.
                ICacheEntry<T?>? current;
                if (maySkip && !contended)
                {
                    current = TryGetLocalEntry<T>(options, out var local) ? local : null;
                }
                else
                {
                    current = await GetCacheEntryInnerAsync<T>(options with { Token = scope.Token }, policy).ConfigureAwait(false);
                }

                // This frame ends the run once the lease is released, so a peer that takes the lock next finds the value; the generation inside does not end it.
                result = current is { Found: true } ? current : await generate(this, options, generator, policy, scope.Inner).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            scope.Fail(ex);
            throw;
        }

        scope.Complete(result);
        return result;
    }

    /// <summary>Takes the key's run before anything else, and runs the generator inline in the caller that took it: a caller that arrives meanwhile joins the run at once, with no lock of its own to wait for or time out of; only the caller that runs it takes the distributed lock, if one is enabled.</summary>
    private ValueTask<object?> RunFirstAsync<T, TGenerator>(
        CacheEntryOptions options,
        TGenerator generator,
        CachePolicy policy,
        Func<MultilayerCache, CacheEntryOptions, TGenerator, CachePolicy, GenerationScope, ValueTask<object?>> generate,
        long seen,
        bool innerMissed,
        CancellationToken token)
    {
        var pending = StartRun<T, TGenerator>(options, generator, policy, generate, seen, innerMissed, token, out var started);
        // The caller that started the run awaits its own work, which is never given up; only one that joined a batch's or TryAdd's reservation can see it abandoned.
        if (started)
        {
            return pending;
        }

        if (!pending.IsCompletedSuccessfully)
        {
            return AwaitJoinedRunAsync<T, TGenerator>(pending, options, generator, policy, generate, seen, token);
        }

        // Read once: a ValueTask can be consumed only once.
        var done = pending.Result;
        return done is IStaleEntry ? AwaitJoinedRunAsync<T, TGenerator>(new(done), options, generator, policy, generate, seen, token) : new(done);
    }

    private async ValueTask<object?> AwaitJoinedRunAsync<T, TGenerator>(
        ValueTask<object?> pending,
        CacheEntryOptions options,
        TGenerator generator,
        CachePolicy policy,
        Func<MultilayerCache, CacheEntryOptions, TGenerator, CachePolicy, GenerationScope, ValueTask<object?>> generate,
        long seen,
        CancellationToken token)
    {
        while (true)
        {
            try
            {
                var shared = await pending.ConfigureAwait(false);
                if (shared is not IStaleEntry stale)
                {
                    return shared;
                }

                var serves = ServesJoined<T>(options.CacheKey.Name, policy, stale.Kept);
                if (serves == true)
                {
                    return shared;
                }

                if (serves == false)
                {
                    return await stale.GeneratedAsync(token).ConfigureAwait(false);
                }

                // The value went while the run was ending, perhaps before this caller came: it is answered by a generation of its own.
                pending = StartRun<T, TGenerator>(options, generator, policy, generate, seen, innerMissed: false, token, out _);
            }
            catch (InFlightAbandonedException)
            {
                // The batch or TryAdd that held the key gave it up: it is up for taking again, after a wait that makes the caller's miss stale.
                pending = StartRun<T, TGenerator>(options, generator, policy, generate, seen, innerMissed: false, token, out _);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && token.IsCancellationRequested) && TryGetStale<T>(options.CacheKey.Name, policy, out var current, out var kept))
            {
                // The run's starter may not have opted in to fail-safe: this caller did, and falls back as a starter would.
                return ServeStale(Logged(options, typeof(T)), current, kept, policy, ReasonError, ex);
            }
        }
    }

    private ValueTask<object?> StartRun<T, TGenerator>(
        CacheEntryOptions options,
        TGenerator generator,
        CachePolicy policy,
        Func<MultilayerCache, CacheEntryOptions, TGenerator, CachePolicy, GenerationScope, ValueTask<object?>> generate,
        long seen,
        bool innerMissed,
        CancellationToken token,
        out bool started) =>
        _generations.StartOrJoinAsync(
            new InFlightKey(options.CacheKey.Name, typeof(T), null, null),
            (Cache: this, Options: options, Generator: generator, Policy: policy, Generate: generate, Seen: seen, InnerMissed: innerMissed),
            static (state, run) =>
                // A write in this process since the caller read the key may have left its value in the local tier, which a run writes before it leaves the table.
                state.Cache._localMemorySetter.Writes != state.Seen && state.Cache.TryGetLocalEntry<T>(state.Options, out var current)
                    ? new ValueTask<object?>(current)
                    : state.Cache.GenerateGuardedAsync<T, TGenerator>(state.Options, state.Generator, state.Policy, state.Generate, new GenerationScope(run, run.Token, run), state.InnerMissed),
            token,
            out started);

    private ValueTask<object?> RunGeneratorAndStoreEntryAsync<T>(CacheEntryOptions cacheEntryOptions, Func<CancellationToken, Task<T?>> generator, CachePolicy policy, GenerationScope scope) =>
        RunGeneratorAndStoreEntryAsync(cacheEntryOptions, generator, static (f, ct) => f(ct), policy, scope, localOnly: false);

    /// <param name="localOnly">The cache has no inner tier: the value is kept locally at once, with no inner write to order it behind.</param>
    private ValueTask<object?> RunGeneratorAndStoreEntryAsync<T, TState>(CacheEntryOptions cacheEntryOptions, TState state, Func<TState, CancellationToken, Task<T?>> generator, CachePolicy policy, GenerationScope scope, bool localOnly) =>
        policy.FailSafeEnabled == true
            ? RunOrStaleAsync<T, TState, T?>(cacheEntryOptions, state, generator, policy, scope, localOnly, static (c, v, expiration) => c._cacheEntryFactory.Create<T?>(v, expiration), static (c, o, v, p, s, l) => c.StoreGeneratedEntryAsync(o, v, p, s, l))
            : RunOrdinaryGeneratorAsync(cacheEntryOptions, state, generator, policy, scope, localOnly);

    /// <summary>The run without fail-safe, in a method of its own so that its frame holds nothing of fail-safe.</summary>
    private async ValueTask<object?> RunOrdinaryGeneratorAsync<T, TState>(CacheEntryOptions cacheEntryOptions, TState state, Func<TState, CancellationToken, Task<T?>> generator, CachePolicy policy, GenerationScope scope, bool localOnly)
    {
        ICacheEntry<T?> entry;
        try
        {
            LogCacheMissed(Logged(cacheEntryOptions, typeof(T)));
            var ret = await FactoryTimeout.RunAsync(state, generator, policy.FactoryTimeout, cacheEntryOptions.CacheKey, Name, Telemetry, scope.Token).ConfigureAwait(false);
            entry = await StoreGeneratedEntryAsync(cacheEntryOptions, ret, policy, scope, localOnly).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            scope.Fail(ex);
            throw;
        }

        scope.Complete(entry);
        return entry;
    }

    private async ValueTask<ICacheEntry<T?>> StoreGeneratedEntryAsync<T>(CacheEntryOptions cacheEntryOptions, T? ret, CachePolicy policy, GenerationScope scope, bool localOnly = false)
    {
        // One entry for the local tier and the caller: both see the same value and deadline.
        var entry = _cacheEntryFactory.Create<T?>(ret, cacheEntryOptions.Expiration);

        // A generator that ignores its token can finish after every caller left and a newer run took the key: its value must not replace theirs.
        if ((ret is not null || _multiLayerCacheOptions.CacheNullValues) && !scope.IsAbandoned)
        {
            var innerCacheDisconnected = GetInnerCacheDisconnected();
            if (localOnly && !innerCacheDisconnected)
            {
                KeepGeneratedLocally(cacheEntryOptions, ret, policy, entry);
                return entry;
            }

            // The starter's token, unless it cancelled while callers that joined still wait for the value: then the run's, which outlives it.
            var store = cacheEntryOptions.Token.IsCancellationRequested && !scope.IsAbandoned ? cacheEntryOptions with { Token = scope.Token } : cacheEntryOptions;
            // A peer that takes the distributed lock next must find the value; a late fail-safe value checks in its foreground turn that it is still current.
            var detached = !innerCacheDisconnected && policy.BackgroundDistributedWrite == true && !DistributedLockOn(policy.Lock) && !InLateStore && _background.TryReserve(cacheEntryOptions.CacheKey.Name, false) && FillInBackground(store, ret, policy, entry);
            if (!detached)
            {
                await StoreGeneratedForegroundAsync(store, ret, innerCacheDisconnected, policy, entry).ConfigureAwait(false);
            }
        }
        else if (!scope.IsAbandoned)
        {
            // The source's answer is newer than the value fail-safe kept, though it is not stored.
            DropKeptForUnstored(cacheEntryOptions.CacheKey.Name);
        }

        return entry;
    }

    // Not async, so the late store adds no state to its caller's frame.
    private ValueTask<bool> StoreGeneratedForegroundAsync<T>(CacheEntryOptions store, T? value, bool disconnected, CachePolicy policy, ICacheEntry<T?> entry) =>
        InLateStore
            ? StoreLateAndAnnounceAsync(store, value, disconnected, policy, entry)
            : Layered<T>().InternalSetAsync(store, value, disconnected, policy, keepRefused: true, entry);

    /// <summary>Keeps a generated value as a written one is kept, for the connected lifetime; a failure is logged and the caller still gets the value.</summary>
    private void KeepGeneratedLocally<T>(CacheEntryOptions options, T? value, CachePolicy policy, ICacheEntry<T?> entry)
    {
        try
        {
            ApplyLocal(options.CacheKey.Name, (Cache: this, Options: options, Value: value, Policy: policy, Entry: entry), static s => s.Cache.MemorySet(s.Options, s.Value, s.Cache.ConnectedLifetime(s.Policy), s.Entry, s.Policy));
        }
        catch (Exception ex)
        {
            LogInnerCacheSetError(ex, Logged(options, typeof(T)));
        }
    }

    /// <summary>The probe a key overload, or a span key the local tier could not look up, has not made yet; true on a found entry.</summary>
    private bool ProbeMemoryOnly<T>(CacheKey cacheKey, CancellationToken token, out T? value, out bool missed)
    {
        var probe = ProbeLocal<T?>(cacheKey, token, out var local);
        var found = probe == LocalProbe.Hit && local!.Found;
        missed = probe == LocalProbe.Miss;
        value = found ? local!.Value : default;
        return found;
    }

    private async ValueTask<T?> GetOrAddGeneratedAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<GeneratedValue<T>>> generator, CachePolicy policy, long seen, bool localMissed, CancellationToken token)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        if (!localMissed && !_sharedInner && !token.IsCancellationRequested && ProbeMemoryOnly<T>(cacheKey, token, out var hit, out localMissed))
        {
            return hit;
        }

        var cacheEntryOptions = _entryBuilder.BuildEntryOptions<T>(cacheKey, GetExpiration(policy), token);
        if (TryGetCooling<T>(cacheEntryOptions.CacheKey.Name, policy, out var cooling))
        {
            return cooling.Value;
        }

        if (localMissed && !_sharedInner)
        {
            var kept = (ICacheEntry<T?>)(await GetOrGenerateEntryAsync<T, Func<CancellationToken, Task<GeneratedValue<T>>>>(cacheEntryOptions, generator, policy, static (cache, options, g, p, scope) => cache.RunGeneratedAndStoreEntryAsync(options, g, static (f, ct) => f(ct), p, scope, localOnly: true), seen, localMissed, token).ConfigureAwait(false))!;
            return kept.Value;
        }

        var entry = localMissed
            ? await Layered<T>().FetchInnerAsync(cacheEntryOptions, policy).ConfigureAwait(false)
            : await GetCacheEntryInnerAsync<T>(cacheEntryOptions, policy).ConfigureAwait(false);
        if (entry.Found)
        {
            return entry.Value;
        }

        var result = (ICacheEntry<T?>)(await GetOrGenerateEntryAsync<T, Func<CancellationToken, Task<GeneratedValue<T>>>>(cacheEntryOptions, generator, policy, static (cache, options, g, p, scope) => cache.RunGeneratedAndStoreEntryAsync(options, g, p, scope), seen, localMissed, token).ConfigureAwait(false))!;
        return result.Value;
    }

    private async ValueTask<T?> GetOrAddGeneratedAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<GeneratedValue<T>>> generator, CachePolicy policy, long seen, bool localMissed, CancellationToken token)
    {
        // As the closure form above; the state is bound to a delegate only for the miss, so a hit builds no closure.
        NotCacheableException.ThrowIfNotCacheable<T>();
        if (!localMissed && !_sharedInner && !token.IsCancellationRequested && ProbeMemoryOnly<T>(cacheKey, token, out var hit, out localMissed))
        {
            return hit;
        }

        var cacheEntryOptions = _entryBuilder.BuildEntryOptions<T>(cacheKey, GetExpiration(policy), token);
        if (TryGetCooling<T>(cacheEntryOptions.CacheKey.Name, policy, out var cooling))
        {
            return cooling.Value;
        }

        if (localMissed && !_sharedInner)
        {
            var kept = (ICacheEntry<T?>)(await GetOrGenerateEntryAsync<T, (TState State, Func<TState, CancellationToken, Task<GeneratedValue<T>>> Generator)>(cacheEntryOptions, (state, generator), policy, static (cache, options, g, p, scope) => cache.RunGeneratedAndStoreEntryAsync(options, g.State, g.Generator, p, scope, localOnly: true), seen, localMissed, token).ConfigureAwait(false))!;
            return kept.Value;
        }

        var entry = localMissed
            ? await Layered<T>().FetchInnerAsync(cacheEntryOptions, policy).ConfigureAwait(false)
            : await GetCacheEntryInnerAsync<T>(cacheEntryOptions, policy).ConfigureAwait(false);
        if (entry.Found)
        {
            return entry.Value;
        }

        var result = (ICacheEntry<T?>)(await GetOrGenerateEntryAsync<T, Func<CancellationToken, Task<GeneratedValue<T>>>>(cacheEntryOptions, StatefulCacheExtensions.BindGenerated(state, generator), policy, static (cache, options, g, p, scope) => cache.RunGeneratedAndStoreEntryAsync(options, g, p, scope), seen, localMissed, token).ConfigureAwait(false))!;
        return result.Value;
    }

    private ValueTask<object?> RunGeneratedAndStoreEntryAsync<T>(CacheEntryOptions cacheEntryOptions, Func<CancellationToken, Task<GeneratedValue<T>>> generator, CachePolicy policy, GenerationScope scope) =>
        RunGeneratedAndStoreEntryAsync(cacheEntryOptions, generator, static (f, ct) => f(ct), policy, scope, localOnly: false);

    /// <param name="localOnly">As on <see cref="RunGeneratorAndStoreEntryAsync{T, TState}"/>.</param>
    private ValueTask<object?> RunGeneratedAndStoreEntryAsync<T, TState>(CacheEntryOptions cacheEntryOptions, TState state, Func<TState, CancellationToken, Task<GeneratedValue<T>>> generator, CachePolicy policy, GenerationScope scope, bool localOnly) =>
        policy.FailSafeEnabled == true
            ? RunOrStaleAsync<T, TState, GeneratedValue<T>>(cacheEntryOptions, state, generator, policy, scope, localOnly, static (c, g, expiration) => c._cacheEntryFactory.Create<T?>(g.Value, g.Expiration ?? expiration), static (c, o, v, p, s, l) => c.StoreGeneratedValueAsync(o, v, p, s, l))
            : RunOrdinaryGeneratedAsync(cacheEntryOptions, state, generator, policy, scope, localOnly);

    /// <summary>As <see cref="RunOrdinaryGeneratorAsync{T, TState}"/>, for a generator that names its expiration.</summary>
    private async ValueTask<object?> RunOrdinaryGeneratedAsync<T, TState>(CacheEntryOptions cacheEntryOptions, TState state, Func<TState, CancellationToken, Task<GeneratedValue<T>>> generator, CachePolicy policy, GenerationScope scope, bool localOnly)
    {
        ICacheEntry<T?> entry;
        try
        {
            LogCacheMissed(Logged(cacheEntryOptions, typeof(T)));
            var generated = await FactoryTimeout.RunAsync(state, generator, policy.FactoryTimeout, cacheEntryOptions.CacheKey, Name, Telemetry, scope.Token).ConfigureAwait(false);
            entry = await StoreGeneratedValueAsync(cacheEntryOptions, generated, policy, scope, localOnly).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            scope.Fail(ex);
            throw;
        }

        scope.Complete(entry);
        return entry;
    }

    /// <summary>Stores a value that names its own expiration: one already past is returned and not stored, and one that skips fail-safe is written under the policy with fail-safe off, so no write of it, a detached one included, keeps it.</summary>
    private ValueTask<ICacheEntry<T?>> StoreGeneratedValueAsync<T>(CacheEntryOptions cacheEntryOptions, GeneratedValue<T> generated, CachePolicy policy, GenerationScope scope, bool localOnly)
    {
        if (generated.Expiration is { } expiration)
        {
            cacheEntryOptions.Expiration = expiration;
        }

        if (generated.Expiration is { } past && past <= _clock.GetUtcNow())
        {
            // As a value that is stored: a generator every caller left answers for nobody, and a newer run may have kept its own value.
            if (!scope.IsAbandoned)
            {
                DropKeptForUnstored(cacheEntryOptions.CacheKey.Name);
            }

            return new(_cacheEntryFactory.Create<T?>(generated.Value, past));
        }

        // Its task as it is: a frame here would cost a state machine whenever the inner write is not complete.
        return StoreGeneratedEntryAsync(cacheEntryOptions, generated.Value, generated.SkipFailSafe ? WithoutFailSafe(policy) : policy, scope, localOnly);
    }
}
