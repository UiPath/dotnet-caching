namespace UiPath.Caching;

internal sealed partial class MultilayerCache
{
    /// <summary>Translates the reserved caller keys back into the generator's states.</summary>
    private static TState[] MapReservedKeysToStates<TState>(CacheKey[] reservedKeys, Dictionary<CacheKey, TState> stateByCallerKey)
        where TState : notnull
    {
        var rehydrateStates = new TState[reservedKeys.Length];
        for (var i = 0; i < reservedKeys.Length; i++)
        {
            rehydrateStates[i] = stateByCallerKey[reservedKeys[i]];
        }
        return rehydrateStates;
    }

    /// <summary>One snapshot read per key, concurrently.</summary>
    private static async Task<ICacheEntry<T?>[]> ReadEachForAdoptionAsync<T>(IMigrationCache snapshot, CacheKey[] keys, CachePolicy policy, CancellationToken token)
    {
        var reads = new Task<ICacheEntry<T?>>[keys.Length];
        for (var i = 0; i < keys.Length; i++)
        {
            reads[i] = AdoptionEntryAsync(snapshot.ReadSnapshotAsync<T>(keys[i], policy, token)).AsTask();
        }

        try
        {
            return await Task.WhenAll(reads).ConfigureAwait(false);
        }
        catch
        {
            foreach (var read in reads)
            {
                read.Observe();
            }

            throw;
        }
    }

    private static CacheEntryOptions[] AdoptionTargets<TState>(TState[] states, Dictionary<TState, RehydrateTarget> byState, CancellationToken token)
        where TState : notnull
    {
        var targets = new CacheEntryOptions[states.Length];
        for (var i = 0; i < states.Length; i++)
        {
            targets[i] = byState[states[i]].Options with { Token = token };
        }

        return targets;
    }

    private void TryRehydrateBatch<T, TState>(
        List<(CacheKey CallerKey, TState State, CacheEntryOptions Options, DateTimeOffset Expiration, T? Value)> hits,
        Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator,
        CachePolicy policy,
        TimeSpan duration,
        TimeSpan? rehydrateJitter)
        where TState : notnull
    {
        if (policy.RehydrateEnabled != true || policy.Rehydrate is null)
        {
            return;
        }
        if (duration <= TimeSpan.Zero || duration == TimeSpan.MaxValue)
        {
            return;
        }

        var (candidates, stateByCallerKey, byState) = SelectRehydrateCandidates(hits);
        if (candidates.Count == 0)
        {
            return;
        }

        _rehydrator.TryTriggerBatch(
            candidates,
            policy,
            duration,
            kind: "cache",
            rehydrateAsync: (rehydrateKeys, run, ct) =>
                RehydrateReservedAsync<T, TState>(rehydrateKeys, stateByCallerKey, byState, generator, policy, duration, rehydrateJitter, run, ct),
            entryType: typeof(T));
    }

    /// <summary>The hits worth rehydrating, plus the two lookups the background callback needs.</summary>
    private (List<(CacheKey Key, DateTimeOffset Expiration)> Candidates, Dictionary<CacheKey, TState> StateByCallerKey, Dictionary<TState, RehydrateTarget> ByState) SelectRehydrateCandidates<T, TState>(
        List<(CacheKey CallerKey, TState State, CacheEntryOptions Options, DateTimeOffset Expiration, T? Value)> hits)
        where TState : notnull
    {
        var candidates = new List<(CacheKey Key, DateTimeOffset Expiration)>(hits.Count);
        var stateByCallerKey = new Dictionary<CacheKey, TState>(hits.Count);
        var byState = new Dictionary<TState, RehydrateTarget>(hits.Count);
        foreach (var (callerKey, state, options, expiration, value) in hits)
        {
            if (value is null && _multiLayerCacheOptions.CacheNullValues)
            {
                continue;
            }
            candidates.Add((callerKey, expiration));
            stateByCallerKey[callerKey] = state;
            byState[state] = new RehydrateTarget(options, expiration, callerKey);
        }
        return (candidates, stateByCallerKey, byState);
    }

    /// <summary>Rehydrates the subset of caller keys the coordinator reserved.</summary>
    private async ValueTask RehydrateReservedAsync<T, TState>(
        CacheKey[] reservedKeys,
        Dictionary<CacheKey, TState> stateByCallerKey,
        Dictionary<TState, RehydrateTarget> byState,
        Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator,
        CachePolicy policy,
        TimeSpan duration,
        TimeSpan? rehydrateJitter,
        RehydrateRun run,
        CancellationToken token)
        where TState : notnull
    {
        var rehydrateStates = MapReservedKeysToStates(reservedKeys, stateByCallerKey);
        rehydrateStates = await AdoptFreshInnerAsync<T, TState>(rehydrateStates, byState, policy, duration, run, token).ConfigureAwait(false);
        if (rehydrateStates.Length == 0)
        {
            return;
        }

        var produced = await generator(rehydrateStates, token).ConfigureAwait(false);

        var groups = GroupRehydratedByExpiration<T, TState>(produced, rehydrateStates, byState, duration, rehydrateJitter);
        if (groups.Count == 0)
        {
            return;
        }

        await WriteRehydrateGroupsAsync(groups, policy, token).ConfigureAwait(false);
    }

    /// <summary>Keeps the inner copies another node already refreshed and returns the states still to generate: those not fresh, not kept, or all of them when the read fails.</summary>
    private async ValueTask<TState[]> AdoptFreshInnerAsync<T, TState>(
        TState[] states,
        Dictionary<TState, RehydrateTarget> byState,
        CachePolicy policy,
        TimeSpan duration,
        RehydrateRun run,
        CancellationToken token)
        where TState : notnull
    {
        if (states.Length == 0 || !_sharedInner || _innerCache is not IMigrationCache snapshot || GetInnerCacheDisconnected())
        {
            return states;
        }

        var rehydrate = policy.Rehydrate!;
        var targets = AdoptionTargets(states, byState, token);
        var keys = Array.ConvertAll(targets, static o => o.CacheKey);
        var budget = RehydrationCoordinator.AdoptReadTimeout(rehydrate, duration);
        using var timeout = new CancellationTokenSource(budget, _clock);
        using var read = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        var marks = MarkInnerReads(keys, targets[0].TopicKey);
        Task<ICacheEntry<T?>[]>? reading = null;
        ICacheEntry<T?>[] fetched;
        try
        {
            reading = ReadEachForAdoptionAsync<T>(snapshot, keys, policy, read.Token);
            fetched = await reading.WaitAsync(budget, _clock, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && token.IsCancellationRequested))
        {
            await read.CancelAsync().ConfigureAwait(false);
            reading?.Observe();
            ReportAdoptReadFailure<T>(ex, rehydrate, CompositeCacheKey.For(Array.ConvertAll(states, state => byState[state].CallerKey)));
            return states;
        }

        token.ThrowIfCancellationRequested();
        return KeepFreshCopies<T, TState>(states, fetched, targets, marks, byState, policy, duration, run, token);
    }

    private void ReportAdoptReadFailure<T>(Exception ex, RehydrateOptions rehydrate, CacheKey groupKey)
    {
        var timedOut = ex is TimeoutException or OperationCanceledException;
        if (timedOut)
        {
            LogRehydrateInnerReadTimedOut(LoggedComposed(groupKey, typeof(T)));
        }
        else
        {
            LogRehydrateInnerReadFailed(ex, LoggedComposed(groupKey, typeof(T)));
        }

        _rehydrator.TrackAdoptReadFailed(rehydrate, "cache", groupKey, timedOut ? "timeout" : "error");
    }

    /// <summary>Keeps each fresh copy locally, records it on the run, and returns the states whose copy was not fresh or could not be kept.</summary>
    private TState[] KeepFreshCopies<T, TState>(
        TState[] states,
        ICacheEntry<T?>[] fetched,
        CacheEntryOptions[] targets,
        long[]? marks,
        Dictionary<TState, RehydrateTarget> byState,
        CachePolicy policy,
        TimeSpan duration,
        RehydrateRun run,
        CancellationToken token)
        where TState : notnull
    {
        var threshold = policy.Rehydrate!.Threshold;
        var remaining = new List<TState>(states.Length);
        for (var i = 0; i < states.Length; i++)
        {
            var entry = fetched[i];
            var adopted = entry.Found
                && InnerCopyFreshEnough(entry.Expiration, _clock.GetUtcNow(), threshold, duration)
                && TryKeepAdopted(targets[i], marks?[i] ?? 0, entry, policy, token);
            if (adopted)
            {
                run.Adopt(byState[states[i]].CallerKey);
            }
            else
            {
                remaining.Add(states[i]);
            }
        }

        return remaining.ToArray();
    }

    private bool TryKeepAdopted<T>(CacheEntryOptions target, long mark, ICacheEntry<T?> entry, CachePolicy policy, CancellationToken token)
    {
        try
        {
            LogFoundInnerCacheCopy(LoggedComposed(target.CacheKey, typeof(T)));
            target.Expiration = entry.Expiration;
            return Layered<T>().FillFromInnerRead(target, mark, entry.Value, policy);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && token.IsCancellationRequested))
        {
            LogRehydrateAdoptFailed(ex, LoggedComposed(target.CacheKey, typeof(T)));
            return false;
        }
    }

    /// <summary>Groups the produced pairs by target expiration, which <c>InternalSetAsync</c> applies per write.</summary>
    private Dictionary<DateTimeOffset, List<(CacheEntryValue<T> Entry, CacheKey CallerKey)>> GroupRehydratedByExpiration<T, TState>(
        KeyValuePair<TState, T?>[]? produced,
        TState[] rehydrateStates,
        Dictionary<TState, RehydrateTarget> byState,
        TimeSpan duration,
        TimeSpan? rehydrateJitter)
        where TState : notnull
    {
        var requested = new HashSet<TState>(rehydrateStates);
        var seen = new HashSet<TState>(rehydrateStates.Length);

        var freshExpiration = _clock.ToDateTimeOffset(ApplyJitter(duration, rehydrateJitter));
        var groups = new Dictionary<DateTimeOffset, List<(CacheEntryValue<T> Entry, CacheKey CallerKey)>>();
        foreach (var pair in produced ?? [])
        {
            if (!requested.Contains(pair.Key) || !seen.Add(pair.Key))
            {
                continue;
            }
            if (pair.Value is null && !_multiLayerCacheOptions.CacheNullValues)
            {
                continue;
            }
            var (options, originalExpiration, callerKey) = byState[pair.Key];
            var target = pair.Value is null ? originalExpiration : freshExpiration;
            options.Expiration = target;
            if (!groups.TryGetValue(target, out var group))
            {
                group = [];
                groups[target] = group;
            }
            group.Add((new CacheEntryValue<T>(options, pair.Value), callerKey));
        }
        return groups;
    }

    /// <summary>Writes then announces one expiration group at a time, reporting every caller key that failed.</summary>
    private async ValueTask WriteRehydrateGroupsAsync<T>(
        Dictionary<DateTimeOffset, List<(CacheEntryValue<T> Entry, CacheKey CallerKey)>> groups,
        CachePolicy policy,
        CancellationToken token)
    {
        var failed = new List<string>();
        var innerCacheDisconnected = GetInnerCacheDisconnected();
        foreach (var group in groups.Values)
        {
            var entries = group.ConvertAll(e => e.Entry);
            var (written, kept) = await SetInnerAsync<T>(entries.ToArray(), innerCacheDisconnected, policy, keepRefused: false, token).ConfigureAwait(false);

            // Written before the events, so a node they wake reads the new value; announced even when the write reports
            // failure, since across Redis Cluster slots it can have written some keys.
            var published = innerCacheDisconnected || await PublishSetsAsync(entries).ConfigureAwait(false);
            if (!kept || !published || (!innerCacheDisconnected && !written))
            {
                failed.AddRange(group.Select(e => e.CallerKey.Name));
            }
        }

        if (failed.Count > 0)
        {
            // A run timeout that ended the write is the coordinator's to report, not a write failure.
            token.ThrowIfCancellationRequested();
            throw new RehydrateWriteFailedException(string.Join(",", failed));
        }
    }
}
