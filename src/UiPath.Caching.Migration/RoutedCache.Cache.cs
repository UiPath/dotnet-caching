namespace UiPath.Caching.Migration;

// The ICache<T> surface of a routed cache: every abstract member is implemented here, explicitly, and has one rule in the test table CacheMemberRules (Migration tests, Support/SurfaceRules).
// A sealed member of the interface (the span-key forms, the many-key forms) forwards to these, so it needs no rule of its own.
internal sealed partial class RoutedCache<T>
{
    string ICache<T>.Name => _registration.Name;

    ValueTask<T?> ICache<T>.GetAsync(CacheKey cacheKey, CancellationToken token) => TryGetAsync(KeyOf(cacheKey), token);

    ValueTask<KeyValuePair<CacheKey, T?>[]> ICache<T>.GetAsync(CacheKey[] cacheKeys, CancellationToken token) => GetManyAsync(cacheKeys, token);

    ValueTask<bool> ICache<T>.ContainsAsync(CacheKey cacheKey, CancellationToken token) => ContainsAsync(KeyOf(cacheKey), token);

    ValueTask<TimeSpan?> ICache<T>.TimeToLiveAsync(CacheKey cacheKey, CancellationToken token) => TimeToLiveAsync(KeyOf(cacheKey), token);

    ValueTask<DateTimeOffset?> ICache<T>.ExpireTimeAsync(CacheKey cacheKey, CancellationToken token) => ExpireTimeAsync(KeyOf(cacheKey), token);

    ValueTask<FillResult<T>> ICache<T>.TryFillAsync(CacheKey cacheKey, T? value, TimeSpan expiration, CancellationToken token) => FillSurfaceAsync(KeyOf(cacheKey), value, Rel(expiration), token);

    ValueTask<FillResult<T>> ICache<T>.TryFillAsync(CacheKey cacheKey, T? value, DateTimeOffset expiration, CancellationToken token) => FillSurfaceAsync(KeyOf(cacheKey), value, Abs(expiration), token);

    ValueTask<ChangeResult<T>> ICache<T>.SetIfChangedAsync(CacheKey cacheKey, T? value, IEqualityComparer<T> comparer, TimeSpan expiration, CancellationToken token) => ChangeAsync(KeyOf(cacheKey), value, comparer, Rel(expiration), token);

    ValueTask<ChangeResult<T>> ICache<T>.SetIfChangedAsync(CacheKey cacheKey, T? value, IEqualityComparer<T> comparer, DateTimeOffset expiration, CancellationToken token) => ChangeAsync(KeyOf(cacheKey), value, comparer, Abs(expiration), token);

    ValueTask<bool> ICache<T>.RemoveAsync(CacheKey cacheKey, CancellationToken token) => RemovedAsync(KeyOf(cacheKey), token);

    ValueTask<bool> ICache<T>.RemoveAsync(CacheKey[] cacheKeys, CancellationToken token) => RemovedManyAsync(cacheKeys, token);

    ValueTask<bool> ICache<T>.SetAsync(CacheKey cacheKey, T? value, CancellationToken token) => StoredAsync(KeyOf(cacheKey), value, default, token);

    ValueTask<bool> ICache<T>.SetAsync(CacheKey cacheKey, T? value, TimeSpan expiration, CancellationToken token) => StoredAsync(KeyOf(cacheKey), value, Rel(expiration), token);

    ValueTask<bool> ICache<T>.SetAsync(CacheKey cacheKey, T? value, DateTimeOffset expiration, CancellationToken token) => StoredAsync(KeyOf(cacheKey), value, Abs(expiration), token);

    ValueTask<bool> ICache<T>.SetAsync(KeyValuePair<CacheKey, T?>[] keyValues, CancellationToken token) => StoredManyAsync(keyValues, default, token);

    ValueTask<bool> ICache<T>.SetAsync(KeyValuePair<CacheKey, T?>[] keyValues, TimeSpan expiration, CancellationToken token) => StoredManyAsync(keyValues, Rel(expiration), token);

    ValueTask<bool> ICache<T>.SetAsync(KeyValuePair<CacheKey, T?>[] keyValues, DateTimeOffset expiration, CancellationToken token) => StoredManyAsync(keyValues, Abs(expiration), token);

    ValueTask<bool> ICache<T>.TryAddAsync(CacheKey cacheKey, T? value, CancellationToken token) => TryAddSurfaceAsync(KeyOf(cacheKey), value, default, token);

    ValueTask<bool> ICache<T>.TryAddAsync(CacheKey cacheKey, T? value, TimeSpan expiration, CancellationToken token) => TryAddSurfaceAsync(KeyOf(cacheKey), value, Rel(expiration), token);

    ValueTask<bool> ICache<T>.TryAddAsync(CacheKey cacheKey, T? value, DateTimeOffset expiration, CancellationToken token) => TryAddSurfaceAsync(KeyOf(cacheKey), value, Abs(expiration), token);

    ValueTask<bool> ICache<T>.RefreshAsync(CacheKey cacheKey, CancellationToken token) => RefreshSurfaceAsync(KeyOf(cacheKey), default, token);

    ValueTask<bool> ICache<T>.RefreshAsync(CacheKey cacheKey, TimeSpan expiration, CancellationToken token) => RefreshSurfaceAsync(KeyOf(cacheKey), Rel(expiration), token);

    ValueTask<bool> ICache<T>.RefreshAsync(CacheKey cacheKey, DateTimeOffset expiration, CancellationToken token) => RefreshSurfaceAsync(KeyOf(cacheKey), Abs(expiration), token);

    ValueTask<T?> ICache<T>.GetOrAddAsync(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, CancellationToken token) => GetOrAddCoreAsync(KeyOf(cacheKey), generator, InvokeFactory, null, default, token);

    ValueTask<T?> ICache<T>.GetOrAddAsync(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, TimeSpan expiration, CancellationToken token) => GetOrAddCoreAsync(KeyOf(cacheKey), generator, InvokeFactory, null, Rel(expiration), token);

    ValueTask<T?> ICache<T>.GetOrAddAsync(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CancellationToken token) => GetOrAddCoreAsync(KeyOf(cacheKey), generator, InvokeFactory, null, Abs(expiration), token);

    ValueTask<T?> ICache<T>.GetOrAddAsync<TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, CancellationToken token) => GetOrAddCoreAsync(KeyOf(cacheKey), state, generator, null, default, token);

    ValueTask<T?> ICache<T>.GetOrAddAsync<TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, TimeSpan expiration, CancellationToken token) => GetOrAddCoreAsync(KeyOf(cacheKey), state, generator, null, Rel(expiration), token);

    ValueTask<T?> ICache<T>.GetOrAddAsync<TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CancellationToken token) => GetOrAddCoreAsync(KeyOf(cacheKey), state, generator, null, Abs(expiration), token);

    ValueTask<T?> ICache<T>.GetOrAddWithExpirationAsync(CacheKey cacheKey, Func<CancellationToken, Task<GeneratedValue<T>>> generator, CancellationToken token) => GetOrAddCoreAsync(KeyOf(cacheKey), generator, null, InvokeExpiringFactory, default, token);

    ValueTask<T?> ICache<T>.GetOrAddWithExpirationAsync<TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<GeneratedValue<T>>> generator, CancellationToken token) => GetOrAddCoreAsync(KeyOf(cacheKey), state, null, generator, default, token);

    ValueTask<KeyValuePair<TState, T?>[]> ICache<T>.GetOrAddAsync<TState>(KeyValuePair<CacheKey, TState>[] entries, Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, CancellationToken token) => GetOrAddManyAsync(entries, generator, default, token);

    ValueTask<KeyValuePair<TState, T?>[]> ICache<T>.GetOrAddAsync<TState>(KeyValuePair<CacheKey, TState>[] entries, Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, TimeSpan expiration, CancellationToken token) => GetOrAddManyAsync(entries, generator, Rel(expiration), token);

    ValueTask<KeyValuePair<TState, T?>[]> ICache<T>.GetOrAddAsync<TState>(KeyValuePair<CacheKey, TState>[] entries, Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, DateTimeOffset expiration, CancellationToken token) => GetOrAddManyAsync(entries, generator, Abs(expiration), token);

    ValueTask<KeyValuePair<TState, T?>[]> ICache<T>.GetOrAddAsync<TState, TArg>(KeyValuePair<CacheKey, TState>[] entries, TArg argument, Func<TArg, TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, CancellationToken token) => GetOrAddManyAsync(entries, (states, t) => generator(argument, states, t), default, token);

    ValueTask<KeyValuePair<TState, T?>[]> ICache<T>.GetOrAddAsync<TState, TArg>(KeyValuePair<CacheKey, TState>[] entries, TArg argument, Func<TArg, TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, TimeSpan expiration, CancellationToken token) => GetOrAddManyAsync(entries, (states, t) => generator(argument, states, t), Rel(expiration), token);

    ValueTask<KeyValuePair<TState, T?>[]> ICache<T>.GetOrAddAsync<TState, TArg>(KeyValuePair<CacheKey, TState>[] entries, TArg argument, Func<TArg, TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, DateTimeOffset expiration, CancellationToken token) => GetOrAddManyAsync(entries, (states, t) => generator(argument, states, t), Abs(expiration), token);

    private static Task<T?> InvokeFactory(Func<CancellationToken, Task<T?>> factory, CancellationToken token) => factory(token);

    private static Task<GeneratedValue<T>> InvokeExpiringFactory(Func<CancellationToken, Task<GeneratedValue<T>>> generator, CancellationToken token) => generator(token);

    private static KeyValuePair<string, TState>[] KeysAndStates<TState>(KeyValuePair<CacheKey, TState>[] entries)
    {
        var keyed = new KeyValuePair<string, TState>[entries.Length];
        for (var i = 0; i < keyed.Length; i++)
        {
            keyed[i] = new KeyValuePair<string, TState>(KeyOf(entries[i].Key), entries[i].Value);
        }
        return keyed;
    }

    private static (KeyValuePair<TState, T?>[] Result, List<int> Missing) BatchReads<TState>(KeyValuePair<string, TState>[] keyed, T?[] reads)
    {
        var result = new KeyValuePair<TState, T?>[keyed.Length];
        var missing = new List<int>();
        for (var i = 0; i < keyed.Length; i++)
        {
            if (reads[i] is null)
            {
                missing.Add(i);
            }
            else
            {
                result[i] = new KeyValuePair<TState, T?>(keyed[i].Value, reads[i]);
            }
        }
        return (result, missing);
    }

    private static string KeyOf(CacheKey cacheKey) => cacheKey.IsNull ? throw new ArgumentException("A cache key is required.", nameof(cacheKey)) : cacheKey.Name;

    private static bool AllTrue(bool[] results) => results.All(r => r);

    private static async ValueTask<(bool Stored, Exception? Failure)> SendPlainBatchAsync(ILibraryBatch<T> batch, Func<ILibraryBatch<T>, ValueTask<bool>> call)
    {
        try
        {
            return (await call(batch).ConfigureAwait(false), null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The commands may have landed before the failure surfaced.
            return (false, ex);
        }
    }

    private static CacheExpiry Rel(TimeSpan expiration) => CacheExpiry.After(CacheExpiration.ThrowIfNotPositive(expiration));

    private CacheExpiry Abs(DateTimeOffset expiration) => CacheExpiry.At(CacheExpiration.ThrowIfNotFuture(expiration, _core.Clock.GetUtcNow()));

    // No expiry given: the registration's default, as a single write applies it.
    private CacheExpiry Resolve(CacheExpiry expiry) => expiry.Relative is null && expiry.Deadline is null && _registration.DefaultExpiry is { } life ? CacheExpiry.After(life) : expiry;

    private async ValueTask<KeyValuePair<CacheKey, T?>[]> GetManyAsync(CacheKey[] cacheKeys, CancellationToken token)
    {
        var keys = new string[cacheKeys.Length];
        for (var i = 0; i < keys.Length; i++)
        {
            keys[i] = KeyOf(cacheKeys[i]);
        }

        if (await TryPlainBatchAsync(keys, static (batch, names, ct) => batch.GetAsync(names, ct), token).ConfigureAwait(false) is { } plain)
        {
            var found = new KeyValuePair<CacheKey, T?>[cacheKeys.Length];
            for (var i = 0; i < found.Length; i++)
            {
                found[i] = new KeyValuePair<CacheKey, T?>(cacheKeys[i], plain[i].Value);
            }

            return found;
        }

        var reads = await Task.WhenAll(keys.Select(k => TryGetAsync(k, token).AsTask())).ConfigureAwait(false);
        return [.. cacheKeys.Select((k, i) => new KeyValuePair<CacheKey, T?>(k, reads[i]))];
    }

    private async ValueTask<bool> ContainsAsync(string key, CancellationToken token)
    {
        if (CacheNullValues)
        {
            // A stored null is a presence the read cannot show, so the library says whether the key is there.
            using var admission = Admit(out var route);
            try
            {
                if (!route.Hold && route.Path == RoutePath.Library && await Client(route).ContainsAsync(key, token).ConfigureAwait(false) is { } present)
                {
                    return present;
                }
            }
            catch (CacheUnavailableException)
            {
                return false;
            }
        }

        return await TryGetAsync(key, token).ConfigureAwait(false) is not null;
    }

    // A null removes unless the library stores it and the keys do not move; a failed or unknown write is false, as the library's.
    private async ValueTask<bool> StoredAsync(string key, T? value, CacheExpiry expiry, CancellationToken token)
    {
        if (value is null && CacheNullValues)
        {
            var deadline = Deadline(expiry);
            var stored = await TryPlainBatchWriteAsync([key], [], deadline, batch => batch.SetAsync([new KeyValuePair<string, T?>(key, null)], deadline, token), token).ConfigureAwait(false);
            if (stored is { } done)
            {
                return done;
            }
        }

        try
        {
            return (value is null ? await RemoveAsync(key, token).ConfigureAwait(false) : await SetAsync(key, value, expiry, token).ConfigureAwait(false)) == WriteOutcome.Committed;
        }
        catch (CacheUnavailableException)
        {
            return false;
        }
    }

    private async ValueTask<bool> RemovedAsync(string key, CancellationToken token)
    {
        try
        {
            return await RemoveAsync(key, token).ConfigureAwait(false) == WriteOutcome.Committed;
        }
        catch (CacheUnavailableException)
        {
            return false;
        }
    }

    private async ValueTask<bool> TryAddSurfaceAsync(string key, T? value, CacheExpiry expiry, CancellationToken token)
    {
        if (value is null && (!CacheNullValues || _library.Moving))
        {
            return false;
        }

        try
        {
            return await TryAddAsync(key, value, expiry, token).ConfigureAwait(false);
        }
        catch (CacheUnavailableException)
        {
            return false;
        }
    }

    private async ValueTask<bool> RefreshSurfaceAsync(string key, CacheExpiry expiry, CancellationToken token)
    {
        try
        {
            return await RefreshAsync(key, expiry, token).ConfigureAwait(false);
        }
        catch (CacheUnavailableException)
        {
            return false;
        }
    }

    private async ValueTask<TimeSpan?> TimeToLiveAsync(string key, CancellationToken token)
    {
        using var admission = Admit(out var route);
        try
        {
            var left = await Client(route).TimeToLiveAsync(key, token).ConfigureAwait(false);
            return left is { } span && span < TimeSpan.Zero ? TimeSpan.Zero : left;
        }
        catch (CacheUnavailableException)
        {
            return null;
        }
    }

    private async ValueTask<DateTimeOffset?> ExpireTimeAsync(string key, CancellationToken token)
    {
        using var admission = Admit(out var route);
        try
        {
            return await Client(route).ExpireTimeAsync(key, token).ConfigureAwait(false);
        }
        catch (CacheUnavailableException)
        {
            return null;
        }
    }

    // The library's fill: written only when absent, nothing announced, the winner's value and expiration returned when it lost.
    private async ValueTask<FillResult<T>> FillSurfaceAsync(string key, T? value, CacheExpiry expiry, CancellationToken token)
    {
        if (value is null && (!CacheNullValues || _library.Moving))
        {
            return default;
        }

        using var admission = Admit(out var route);
        try
        {
            return await Client(route).TryFillAsync(key, value, Deadline(expiry), token).ConfigureAwait(false);
        }
        catch (CacheUnavailableException)
        {
            return default;
        }
    }

    // The swap is the library's own atomic one; a key move state and the source refuse it.
    private async ValueTask<ChangeResult<T>> ChangeAsync(string key, T? value, IEqualityComparer<T> comparer, CacheExpiry expiry, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(comparer);
        using var admission = Admit(out var route);
        var deadline = Deadline(expiry);
        ChangeResult<T> result = default;
        _ = await MutateAsync(
            key,
            route with { NoFallback = true },
            async (client, ct) =>
            {
                var swap = await client.SetIfChangedAsync(key, value, comparer, deadline, ct).ConfigureAwait(false);
                result = swap.Change;
                return swap.Write;
            },
            deadline ?? DateTimeOffset.MaxValue,
            null,
            fill: null,
            token).ConfigureAwait(false);
        return result;
    }

    private async ValueTask<bool> StoredManyAsync(KeyValuePair<CacheKey, T?>[] keyValues, CacheExpiry expiry, CancellationToken token)
    {
        var deadline = Deadline(expiry);
        var sets = new List<KeyValuePair<string, T?>>(keyValues.Length);
        var removes = new List<string>();
        foreach (var (cacheKey, value) in keyValues)
        {
            var key = KeyOf(cacheKey);
            if (value is null && !CacheNullValues)
            {
                removes.Add(key);
            }
            else
            {
                sets.Add(new KeyValuePair<string, T?>(key, value));
            }
        }

        var written = await TryPlainBatchWriteAsync(
            [.. sets.Select(s => s.Key)],
            [.. removes],
            deadline,
            async batch => (sets.Count == 0 || await batch.SetAsync(sets, deadline, token).ConfigureAwait(false)) && (removes.Count == 0 || await batch.RemoveAsync(removes, token).ConfigureAwait(false)),
            token).ConfigureAwait(false);
        return written ?? AllTrue(await Task.WhenAll(keyValues.Select(kv => StoredAsync(KeyOf(kv.Key), kv.Value, expiry, token).AsTask())).ConfigureAwait(false));
    }

    private async ValueTask<bool> RemovedManyAsync(CacheKey[] cacheKeys, CancellationToken token)
    {
        var keys = new string[cacheKeys.Length];
        for (var i = 0; i < keys.Length; i++)
        {
            keys[i] = KeyOf(cacheKeys[i]);
        }

        var written = await TryPlainBatchWriteAsync([], keys, null, batch => batch.RemoveAsync(keys, token), token).ConfigureAwait(false);
        return written ?? AllTrue(await Task.WhenAll(keys.Select(k => RemovedAsync(k, token).AsTask())).ConfigureAwait(false));
    }

    // One generator call for the keys that missed; each made value is stored by a single get-or-add fill.
    private async ValueTask<KeyValuePair<TState, T?>[]> GetOrAddManyAsync<TState>(KeyValuePair<CacheKey, TState>[] entries, Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, CacheExpiry expiry, CancellationToken token)
        where TState : notnull
    {
        var keyed = KeysAndStates(entries);
        if ((_pin is null || _core.WriteFormats is null) && await TryPlainBatchAsync(keyed, (batch, pairs, ct) => batch.GetOrAddAsync(pairs, generator, Resolve(expiry), ct), token).ConfigureAwait(false) is { } plain)
        {
            return plain;
        }

        var reads = await Task.WhenAll(keyed.Select(e => TryGetAsync(e.Key, token).AsTask())).ConfigureAwait(false);
        var (result, missing) = BatchReads(keyed, reads);
        if (missing.Count == 0)
        {
            return result;
        }

        var made = await generator([.. missing.Select(i => keyed[i].Value)], token).ConfigureAwait(false);
        var byState = new Dictionary<TState, T?>();
        foreach (var pair in made)
        {
            byState[pair.Key] = pair.Value;
        }

        foreach (var i in missing)
        {
            byState.TryGetValue(keyed[i].Value, out var value);
            var stored = value is null ? null : await GetOrAddAsync(keyed[i].Key, _ => Task.FromResult<T?>(value), expiry, token).ConfigureAwait(false);
            result[i] = new KeyValuePair<TState, T?>(keyed[i].Value, stored);
        }

        return result;
    }

    // A read-only batch call on the plain library path goes to the library's batch.
    private async ValueTask<TResult?> TryPlainBatchAsync<TInput, TResult>(TInput input, Func<ILibraryBatch<T>, TInput, CancellationToken, ValueTask<TResult>> call, CancellationToken token)
        where TResult : class
    {
        var admission = Admit(out var route);
        try
        {
            if (PlainBatchOf(route) is not { } batch)
            {
                return null;
            }

            token.ThrowIfCancellationRequested();
            return await call(batch, input, token).ConfigureAwait(false);
        }
        finally
        {
            admission.Dispose();
        }
    }

    // Lock keys in sorted order and keep each key pending if its batch result is uncertain.
    private async ValueTask<bool?> TryPlainBatchWriteAsync(string[] setKeys, string[] removeKeys, DateTimeOffset? deadline, Func<ILibraryBatch<T>, ValueTask<bool>> call, CancellationToken token)
    {
        var admission = Admit(out var route);
        try
        {
            if (PlainBatchOf(route) is not { } batch)
            {
                return null;
            }

            var keys = setKeys.Concat(removeKeys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var (stored, failure) = await WritePlainBatchAsync(keys, batch, call, route, token).ConfigureAwait(false);
            var setNames = setKeys.ToHashSet(StringComparer.Ordinal);
            await Task.WhenAll(keys.Select(k => PublishKeyAsync(k, setNames.Contains(k) ? deadline ?? DateTimeOffset.MaxValue : null, route).AsTask())).ConfigureAwait(false);
            if (failure is OperationCanceledException)
            {
                throw failure;
            }

            return stored;
        }
        finally
        {
            admission.Dispose();
        }
    }

    private async ValueTask<(bool Stored, Exception? Failure)> WritePlainBatchAsync(string[] keys, ILibraryBatch<T> batch, Func<ILibraryBatch<T>, ValueTask<bool>> call, Route route, CancellationToken token)
    {
        await _fence.EnterWriteAsync(token).ConfigureAwait(false);
        var states = new KeyState[keys.Length];
        var held = 0;
        try
        {
            for (; held < keys.Length; held++)
            {
                states[held] = _keys.Acquire(keys[held]);
                await states[held].Lock.WaitAsync(token).ConfigureAwait(false);
            }

            token.ThrowIfCancellationRequested();
            foreach (var state in states)
            {
                state.Advance();
            }

            var (stored, failure) = await SendPlainBatchAsync(batch, call).ConfigureAwait(false);
            for (var i = 0; i < keys.Length; i++)
            {
                RetireBatchKey(keys[i], states[i], !stored, route);
            }

            return (stored, failure);
        }
        finally
        {
            for (var i = 0; i < held; i++)
            {
                states[i].Lock.Release();
            }

            for (var i = 0; i < keys.Length && states[i] is not null; i++)
            {
                _keys.Release(keys[i], states[i]);
            }

            _fence.ExitWrite();
        }
    }

    private void RetireBatchKey(string key, KeyState state, bool uncertain, Route route)
    {
        if (uncertain)
        {
            var settle = Client(route).SettleWithin;
            var window = settle is { } bound && bound > _registration.PendingWindow ? bound : _registration.PendingWindow;
            var settlement = Client(route).SettledAsync(key);
            _pending.Begin(key, window, ReferenceEquals(settlement, PathClientTasks.NoPendingRetry) ? null : settlement);
            RouterTelemetry.CountPending(_registration.Name, _registration.Group, route);
        }

        _core.Tier.Evict(new LocalKey(_registration.Name, key));
        _library.KeyChanged(key);
        state.Advance();
        _flights.TryRemove(key, out _);
    }

    private ILibraryBatch<T>? PlainBatchOf(Route route) => route.Path == RoutePath.Library && !route.Hold && !route.Bypass ? Client(route).PlainBatch : null;
}
