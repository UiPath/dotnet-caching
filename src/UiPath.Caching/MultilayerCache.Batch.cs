namespace UiPath.Caching;

internal sealed partial class MultilayerCache
{
    // A closure-form batch passes its generator as the argument with DirectBatch.Invoke, and gets it back unwrapped.
    private static Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> BindBatch<T, TState, TArg>(TArg argument, Func<TArg, TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator)
        where TState : notnull =>
        argument is Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> direct && ReferenceEquals(generator, DirectBatch<T, TState>.Invoke)
            ? direct
            : StatefulCacheExtensions.BindBatch(argument, generator);

    /// <summary>Stops every join counting as a waiter, so a shared run no caller still wants is cancelled, and observes the failure of one nobody awaits.</summary>
    private static void WithdrawAll<T>(List<BatchJoin<T>> joined)
    {
        foreach (var join in joined)
        {
            join.Join.Withdraw();
            join.Run.Forget();
        }
    }

    /// <summary>Waits for the runs this call joined; the indexes whose run was given up come back for another try.</summary>
    private static async ValueTask<List<int>> AwaitJoinedAsync<T>(List<BatchJoin<T>> joined, CacheEntryOptions[] missOptions, KeyValuePair<CacheKey, ICacheEntry<T?>>[] result)
    {
        var abandoned = new List<int>();
        try
        {
            foreach (var (index, _, run) in joined)
            {
                try
                {
                    result[index] = new KeyValuePair<CacheKey, ICacheEntry<T?>>(missOptions[index].CacheKey, await run.ConfigureAwait(false));
                }
                catch (InFlightAbandonedException)
                {
                    abandoned.Add(index);
                }
            }
        }
        catch (Exception)
        {
            WithdrawAll(joined);
            throw;
        }

        return abandoned;
    }

    private static void EndOwned(List<BatchRun> owned, Exception failure)
    {
        foreach (var run in owned)
        {
            run.Run.Fail(failure);
        }
    }

    /// <summary>A joined run's entry; a batch call does not opt in to fail-safe, so a value it served is replaced by what the run would have answered without it.</summary>
    private static async Task<ICacheEntry<T?>> AsEntryAsync<T>(Task<object?> run, CancellationToken token)
    {
        var shared = await run.ConfigureAwait(false);
        return (ICacheEntry<T?>)(shared is IStaleEntry stale ? await stale.GeneratedAsync(token).ConfigureAwait(false) : shared)!;
    }

    /// <summary>Restricts the generator's output to what we asked for; the first value wins per state.</summary>
    private static Dictionary<TState, T?> SelectRequestedProduced<T, TState>(
        KeyValuePair<TState, T?>[]? produced,
        TState[] requestStates)
        where TState : notnull
    {
        var producedByState = new Dictionary<TState, T?>(requestStates.Length);
        var requested = new HashSet<TState>(requestStates);
        foreach (var pair in (produced ?? []).Where(pair => requested.Contains(pair.Key)))
        {
            _ = producedByState.TryAdd(pair.Key, pair.Value);
        }
        return producedByState;
    }

    private static KeyValuePair<TState, T?>[] Project<T, TState>(TState[] states, int[] keyIndexOfState, T?[] values)
        where TState : notnull
    {
        var results = new KeyValuePair<TState, T?>[states.Length];
        for (var i = 0; i < states.Length; i++)
        {
            results[i] = new KeyValuePair<TState, T?>(states[i], values[keyIndexOfState[i]]);
        }
        return results;
    }

    [SuppressMessage("Major Code Smell", "S3267:Loops should be simplified with LINQ expressions", Justification = "A closure per call on the batch hit path.")]
    private static bool HasRepeatedState<TState>(KeyValuePair<CacheKey, TState>[] entries)
        where TState : notnull
    {
        if (entries.Length <= 16)
        {
            for (var i = 1; i < entries.Length; i++)
            {
                for (var j = 0; j < i; j++)
                {
                    if (EqualityComparer<TState>.Default.Equals(entries[i].Value, entries[j].Value))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        var seen = new HashSet<TState>(entries.Length);
        foreach (var entry in entries)
        {
            if (!seen.Add(entry.Value))
            {
                return true;
            }
        }

        return false;
    }

    private async ValueTask<KeyValuePair<TState, T?>[]> GetOrAddBatchInternalAsync<T, TState, TArg>(
        KeyValuePair<CacheKey, TState>[] entries,
        TArg argument,
        Func<TArg, TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator,
        DateTimeOffset? expiration,
        TimeSpan effectiveDuration,
        TimeSpan? rehydrateJitter,
        CachePolicy policy,
        CancellationToken token)
        where TState : notnull
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        ArgumentNullException.ThrowIfNull(entries);

        if (TryGetAllLocal<T, TState>(entries, policy, token, out var local))
        {
            return local;
        }

        var (states, keyIndexOfState, callerKeys, options, firstStateOfKey) = DistinctEntries<T, TState>(entries, expiration, token);
        if (states.Length == 0)
        {
            return [];
        }

        var seen = _localMemorySetter.Writes;
        var probe = await GetCacheEntriesInnerAsync<T>(options, policy, token).ConfigureAwait(false);

        var values = new T?[options.Length];
        var missIndices = new List<int>();
        var hits = new List<(CacheKey CallerKey, TState State, CacheEntryOptions Options, DateTimeOffset Expiration, T? Value)>();
        for (var i = 0; i < probe.Length; i++)
        {
            var entry = probe[i].Value;
            if (entry.Found)
            {
                values[i] = entry.Value;
                hits.Add((callerKeys[i], states[firstStateOfKey[i]], options[i], entry.Expiration, entry.Value));
                continue;
            }
            missIndices.Add(i);
        }

        if (hits.Count > 0 && policy.RehydrateEnabled == true && policy.Rehydrate is not null)
        {
            TryRehydrateBatch(hits, BindBatch(argument, generator), policy, effectiveDuration, rehydrateJitter);
        }

        if (missIndices.Count == 0)
        {
            return Project(states, keyIndexOfState, values);
        }

        var missOptions = new CacheEntryOptions[missIndices.Count];
        var missStates = new TState[missIndices.Count];
        var missProbe = new KeyValuePair<CacheKey, ICacheEntry<T?>>[missIndices.Count];
        for (var i = 0; i < missIndices.Count; i++)
        {
            var index = missIndices[i];
            missOptions[i] = options[index];
            missStates[i] = states[firstStateOfKey[index]];
            missProbe[i] = probe[index];
        }

        var bound = BindBatch(argument, generator);
        var resolved = SharesRuns(policy.Lock)
            ? await RunBatchSharedAsync(missOptions, missStates, missProbe, bound, policy, seen, token).ConfigureAwait(false)
            : await RunBatchGuardedAsync(missOptions, missStates, missProbe, bound, policy, token).ConfigureAwait(false);

        for (var i = 0; i < missIndices.Count; i++)
        {
            values[missIndices[i]] = resolved[i].Value.Value;
        }

        return Project(states, keyIndexOfState, values);
    }

    /// <summary>One batch generator for the keys given, under the distributed lock over them when one is enabled: the lock is taken, the keys read again in both tiers, and the generator run for those still missing.</summary>
    private async ValueTask<KeyValuePair<CacheKey, ICacheEntry<T?>>[]> RunBatchGuardedAsync<T, TState>(
        CacheEntryOptions[] options,
        TState[] states,
        KeyValuePair<CacheKey, ICacheEntry<T?>>[] probe,
        Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator,
        CachePolicy policy,
        CancellationToken token)
        where TState : notnull
    {
        if (!DistributedLockOn(policy.Lock))
        {
            return await RunBatchGeneratorAndStoreAsync(options, states, probe, generator, policy, token).ConfigureAwait(false);
        }

        await using var lease = await AcquireDistributedLockAsync(CompositeCacheKey.For(Array.ConvertAll(options, o => o.CacheKey)), policy.Lock, token).ConfigureAwait(false);
        var latest = await GetCacheEntriesInnerAsync<T>(options, policy, token).ConfigureAwait(false);
        return await RunBatchGeneratorAndStoreAsync(options, states, latest, generator, policy, token).ConfigureAwait(false);
    }

    /// <summary>Joins the keys whose generator run is in flight, a single-key run or another batch, and runs one batch generator for the rest, which others in turn can join.</summary>
    private async ValueTask<KeyValuePair<CacheKey, ICacheEntry<T?>>[]> RunBatchSharedAsync<T, TState>(
        CacheEntryOptions[] missOptions,
        TState[] missStates,
        KeyValuePair<CacheKey, ICacheEntry<T?>>[] probe,
        Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator,
        CachePolicy policy,
        long seen,
        CancellationToken token)
        where TState : notnull
    {
        var result = (KeyValuePair<CacheKey, ICacheEntry<T?>>[])probe.Clone();
        var owned = new List<BatchRun>();
        var joined = new List<BatchJoin<T>>();
        try
        {
            ReserveBatchKeys(missOptions, probe, owned, joined, token);
            if (owned.Count > 0)
            {
                await RunOwnedAsync(owned, missOptions, missStates, probe, generator, policy, seen, result, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            EndOwned(owned, new InFlightAbandonedException());
            WithdrawAll(joined);
            throw;
        }
        catch (Exception ex)
        {
            EndOwned(owned, ex);
            WithdrawAll(joined);
            throw;
        }

        var abandoned = await AwaitJoinedAsync(joined, missOptions, result).ConfigureAwait(false);
        if (abandoned.Count > 0)
        {
            await RetryAbandonedAsync(abandoned, missOptions, missStates, generator, policy, result, token).ConfigureAwait(false);
        }

        return result;
    }

    private void ReserveBatchKeys<T>(CacheEntryOptions[] missOptions, KeyValuePair<CacheKey, ICacheEntry<T?>>[] probe, List<BatchRun> owned, List<BatchJoin<T>> joined, CancellationToken token)
    {
        // Under one gate, so two batches over the same keys cannot each take some of them: the first takes every free key, the next joins them all. Nothing in it waits.
        lock (_batchReserveGate)
        {
            for (var i = 0; i < missOptions.Length; i++)
            {
                if (!probe[i].Value.Found)
                {
                    ReserveOrJoin(i, new InFlightKey(missOptions[i].CacheKey.Name, typeof(T), null, null), owned, joined, token);
                }
            }
        }
    }

    /// <summary>Runs one batch generator for the keys this call took, and ends each reservation with its entry.</summary>
    private async ValueTask RunOwnedAsync<T, TState>(
        List<BatchRun> owned,
        CacheEntryOptions[] missOptions,
        TState[] missStates,
        KeyValuePair<CacheKey, ICacheEntry<T?>>[] probe,
        Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator,
        CachePolicy policy,
        long seen,
        KeyValuePair<CacheKey, ICacheEntry<T?>>[] result,
        CancellationToken token)
        where TState : notnull
    {
        // A write in this process since the caller probed may have left values in the local tier, which a run writes before it leaves the table.
        var rewritten = _localMemorySetter.Writes != seen;

        // Every key is ours, none joined, so the arrays already are the owned set and need no copy.
        var all = owned.Count == missOptions.Length;
        var ownedOptions = all ? missOptions : owned.Select(o => missOptions[o.Index]).ToArray();
        var ownedStates = all ? missStates : owned.Select(o => missStates[o.Index]).ToArray();
        var current = all && !rewritten
            ? probe
            : owned.Select(o => rewritten && TryGetLocalEntry<T>(missOptions[o.Index], out var local) ? new KeyValuePair<CacheKey, ICacheEntry<T?>>(missOptions[o.Index].CacheKey, local) : probe[o.Index]).ToArray();
        var produced = await RunBatchGuardedAsync(ownedOptions, ownedStates, current, generator, policy, token).ConfigureAwait(false);
        for (var i = 0; i < owned.Count; i++)
        {
            result[owned[i].Index] = produced[i];
            owned[i].Run.Complete(produced[i].Value);
        }
    }

    private async ValueTask RetryAbandonedAsync<T, TState>(
        List<int> abandoned,
        CacheEntryOptions[] missOptions,
        TState[] missStates,
        Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator,
        CachePolicy policy,
        KeyValuePair<CacheKey, ICacheEntry<T?>>[] result,
        CancellationToken token)
        where TState : notnull
    {
        var retryOptions = abandoned.Select(i => missOptions[i]).ToArray();
        var freshSeen = _localMemorySetter.Writes;
        var fresh = await GetCacheEntriesInnerAsync<T>(retryOptions, policy, token).ConfigureAwait(false);
        var again = await RunBatchSharedAsync(retryOptions, abandoned.Select(i => missStates[i]).ToArray(), fresh, generator, policy, freshSeen, token).ConfigureAwait(false);
        for (var i = 0; i < abandoned.Count; i++)
        {
            result[abandoned[i]] = again[i];
        }
    }

    private void ReserveOrJoin<T>(int index, InFlightKey key, List<BatchRun> owned, List<BatchJoin<T>> joined, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (_generations.TryReserve(key, out var reservation))
            {
                owned.Add(new BatchRun(index, reservation));
                return;
            }

            if (_generations.TryJoin(key, token, out var shared))
            {
                joined.Add(new BatchJoin<T>(index, shared, AsEntryAsync<T>(shared.Result, token)));
                return;
            }

            // The run ended between the two, so the key is free again.
        }
    }

    private async ValueTask<KeyValuePair<CacheKey, ICacheEntry<T?>>[]> RunBatchGeneratorAndStoreAsync<T, TState>(
        CacheEntryOptions[] missOptions,
        TState[] missStates,
        KeyValuePair<CacheKey, ICacheEntry<T?>>[] probe,
        Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator,
        CachePolicy policy,
        CancellationToken token)
        where TState : notnull
    {
        var stillMissing = new List<int>();
        for (var i = 0; i < missOptions.Length; i++)
        {
            if (!probe[i].Value.Found)
            {
                stillMissing.Add(i);
            }
        }
        if (stillMissing.Count == 0)
        {
            return probe;
        }

        var requestStates = new TState[stillMissing.Count];
        var mappedKeys = new CacheKey[stillMissing.Count];
        // Log-only, so it is not built for a disabled level.
        var missedOptions = _logger.IsEnabled(LogLevel.Debug) ? new CacheEntryOptions[stillMissing.Count] : null;
        for (var i = 0; i < stillMissing.Count; i++)
        {
            requestStates[i] = missStates[stillMissing[i]];
            mappedKeys[i] = missOptions[stillMissing[i]].CacheKey;
            if (missedOptions is not null)
            {
                missedOptions[i] = missOptions[stillMissing[i]];
            }
        }

        var telemetryKey = CompositeCacheKey.For(mappedKeys);
        if (missedOptions is not null)
        {
            LogBatchCacheMissed(Logged(missedOptions, typeof(T)), stillMissing.Count);
        }

        var watched = BeginBatchFill(missOptions, stillMissing);
        try
        {
            var produced = await InvokeFactoryAsync(telemetryKey, ct => generator(requestStates, ct), policy.FactoryTimeout, token).ConfigureAwait(false);
            var producedByState = SelectRequestedProduced(produced, requestStates);

            var toStore = SelectEntriesToStore<T, TState>(missOptions, missStates, stillMissing, producedByState);
            Dictionary<string, ICacheEntry<T?>>? adopted = null;
            if (toStore.Count > 0)
            {
                var innerCacheDisconnected = GetInnerCacheDisconnected();
                if (innerCacheDisconnected)
                {
                    await InternalSetAsync<T>(toStore.ToArray(), innerCacheDisconnected, policy, keepRefused: true, token).ConfigureAwait(false);
                }
                else
                {
                    adopted = await FillBatchAsync(toStore.ToArray(), watched, policy, token).ConfigureAwait(false);
                }
            }

            return BuildBatchEntries<T, TState>(missOptions, missStates, probe, producedByState, adopted);
        }
        finally
        {
            EndBatchFill(watched);
        }
    }

    /// <summary>The write set: the answered still-missing slots, minus the nulls this cache does not store.</summary>
    private List<CacheEntryValue<T>> SelectEntriesToStore<T, TState>(
        CacheEntryOptions[] missOptions,
        TState[] missStates,
        List<int> stillMissing,
        Dictionary<TState, T?> producedByState)
        where TState : notnull
    {
        var toStore = new List<CacheEntryValue<T>>(producedByState.Count);
        foreach (var index in stillMissing)
        {
            // A key the generator answered with nothing this cache stores: that answer is newer than its fail-safe value.
            if (!producedByState.TryGetValue(missStates[index], out var value) || (value is null && !_multiLayerCacheOptions.CacheNullValues))
            {
                DropKeptUnlessLive(missOptions[index].CacheKey.Name);
                continue;
            }
            toStore.Add(new CacheEntryValue<T>(missOptions[index], value));
        }
        return toStore;
    }

    /// <summary>One entry per miss-set slot: the post-lock hit if there was one, otherwise the generated value.</summary>
    private KeyValuePair<CacheKey, ICacheEntry<T?>>[] BuildBatchEntries<T, TState>(
        CacheEntryOptions[] missOptions,
        TState[] missStates,
        KeyValuePair<CacheKey, ICacheEntry<T?>>[] probe,
        Dictionary<TState, T?> producedByState,
        Dictionary<string, ICacheEntry<T?>>? adopted = null)
        where TState : notnull
    {
        var results = new KeyValuePair<CacheKey, ICacheEntry<T?>>[missOptions.Length];
        for (var i = 0; i < missOptions.Length; i++)
        {
            if (probe[i].Value.Found)
            {
                results[i] = probe[i];
                continue;
            }

            // A peer stored the key first: its value is the answer, not the one this call generated.
            if (adopted is not null && adopted.TryGetValue(missOptions[i].CacheKey.Name, out var stored))
            {
                results[i] = new KeyValuePair<CacheKey, ICacheEntry<T?>>(missOptions[i].CacheKey, stored);
                continue;
            }

            var wasProduced = producedByState.TryGetValue(missStates[i], out var value);
            results[i] = new KeyValuePair<CacheKey, ICacheEntry<T?>>(
                missOptions[i].CacheKey,
                _cacheEntryFactory.Create<T?>(value, wasProduced ? missOptions[i].Expiration : DateTimeOffset.MinValue));
        }
        return results;
    }

    /// <summary>A batch whose keys are all local hits, answered as the full path would without building its options, lists and lookups; a miss, a rehydrating policy or a repeated state takes the full path.</summary>
    private bool TryGetAllLocal<T, TState>(KeyValuePair<CacheKey, TState>[] entries, CachePolicy policy, CancellationToken token, [NotNullWhen(true)] out KeyValuePair<TState, T?>[]? results)
        where TState : notnull
    {
        results = null;
        if (entries.Length == 0 || token.IsCancellationRequested || (policy.RehydrateEnabled == true && policy.Rehydrate is not null))
        {
            return false;
        }

        // The first key probed before the array is allocated, so a cold batch discards nothing.
        if (ProbeLocal<T>(entries[0].Key, token, out var first) != LocalProbe.Hit || !first!.Found)
        {
            return false;
        }

        var found = new KeyValuePair<TState, T?>[entries.Length];
        found[0] = new KeyValuePair<TState, T?>(entries[0].Value, first.Value);
        for (var i = 1; i < entries.Length; i++)
        {
            if (ProbeLocal<T>(entries[i].Key, token, out var entry) != LocalProbe.Hit || !entry!.Found)
            {
                return false;
            }

            found[i] = new KeyValuePair<TState, T?>(entries[i].Value, entry.Value);
        }

        if (HasRepeatedState(entries))
        {
            return false;
        }

        results = found;
        return true;
    }

    /// <summary>Splits entries into state space and mapped-key space, both in first-occurrence order.</summary>
    private (TState[] States, int[] KeyIndexOfState, CacheKey[] CallerKeys, CacheEntryOptions[] Options, int[] FirstStateOfKey) DistinctEntries<T, TState>(
        KeyValuePair<CacheKey, TState>[] entries,
        DateTimeOffset? expiration,
        CancellationToken token)
        where TState : notnull
    {
        var states = new List<TState>(entries.Length);
        var keyIndexOfState = new List<int>(entries.Length);
        var callerKeys = new List<CacheKey>(entries.Length);
        var options = new List<CacheEntryOptions>(entries.Length);
        var firstStateOfKey = new List<int>(entries.Length);
        var seenStates = new HashSet<TState>(entries.Length);
        var keySlot = new Dictionary<CacheKey, int>(entries.Length);

        foreach (var entry in entries)
        {
            if (!seenStates.Add(entry.Value))
            {
                continue;
            }
            var entryOptions = _entryBuilder.BuildEntryOptions<T>(entry.Key, expiration, token);
            if (!keySlot.TryGetValue(entryOptions.CacheKey, out var slot))
            {
                slot = options.Count;
                options.Add(entryOptions);
                callerKeys.Add(entry.Key);
                keySlot[entryOptions.CacheKey] = slot;
                firstStateOfKey.Add(states.Count);
            }
            states.Add(entry.Value);
            keyIndexOfState.Add(slot);
        }

        return (states.ToArray(), keyIndexOfState.ToArray(), callerKeys.ToArray(), options.ToArray(), firstStateOfKey.ToArray());
    }
}
