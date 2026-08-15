using UiPath.Caching.Locking;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching;

internal sealed partial class MultilayerCache : MultilayerCacheBase, ICache
{
    private readonly ICache _innerCache;
    private readonly CacheEntryBuilder _entryBuilder;
    private readonly LocalMemorySetter _localMemorySetter;

    public MultilayerCache(
        string cacheName,
        ICache innerCache,
        IMemoryCacheFactory memoryCacheFactory,
        IChangeTokenFactory changeTokenFactory,
        ITopicFactory topicFactory,
        ICacheEventFactory cacheEventFactory,
        ICachingTelemetryProvider telemetryProvider,
        IMultilayerCacheOptions multiLayerCacheOptions,
        IMemoryCacheOptions memoryCacheOptions,
        CacheOptions cacheOptions,
        ILocalLock localLock,
        IDistributedLock distributedLock,
        ICachePolicyFactory policyFactory,
        ILogger logger)
        : base(cacheName, innerCache, memoryCacheFactory, topicFactory, cacheEventFactory, telemetryProvider, multiLayerCacheOptions, memoryCacheOptions, cacheOptions, localLock, distributedLock, policyFactory, logger)
    {
        _innerCache = innerCache;
        var cacheKeyStrategy = _multiLayerCacheOptions.CacheKeyStrategy ?? new DefaultCacheKeyStrategy();
        var topicKeyStrategy = _multiLayerCacheOptions.TopicKeyStrategy ?? new DefaultTopicKeyStrategy(cacheOptions.Separator);
        _entryBuilder = new CacheEntryBuilder(cacheKeyStrategy, topicKeyStrategy, _clock);
        _localMemorySetter = new LocalMemorySetter(cacheName, changeTokenFactory, _topicProvider, _memoryCache, logger, _clock, _multiLayerCacheOptions, memoryCacheOptions, telemetryProvider);
    }

    public ValueTask<T?> GetAsync<T>(CacheKey cacheKey, CachePolicy? policy = null, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        policy ??= _defaultPolicy;
        return GetInnerAsync<T>(_entryBuilder.BuildEntryOptions<T>(cacheKey, _clock.ToDateTimeOffset(_multiLayerCacheOptions.DefaultExpiration), token), policy);
    }

    public ValueTask<KeyValuePair<CacheKey, T?>[]> GetAsync<T>(CacheKey[] cacheKeys, CachePolicy? policy = null, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        policy ??= _defaultPolicy;
        var options = cacheKeys.Select(k => _entryBuilder.BuildEntryOptions<T>(k, default, token)).ToArray();
        return GetInnerAsync<T>(options, policy, token);
    }

    public async ValueTask<ICacheEntry<T?>> GetCacheEntryAsync<T>(CacheKey cacheKey, CachePolicy? policy = null, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        policy ??= _defaultPolicy;
        var options = _entryBuilder.BuildEntryOptions<T>(cacheKey, _clock.ToDateTimeOffset(_multiLayerCacheOptions.DefaultExpiration), token);
        return await GetCacheEntryInnerAsync<T>(options, policy).ConfigureAwait(false);
    }

    public async ValueTask<KeyValuePair<CacheKey, ICacheEntry<T?>>[]> GetCacheEntriesAsync<T>(CacheKey[] cacheKeys, CachePolicy? policy = null, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        policy ??= _defaultPolicy;
        var options = cacheKeys.Select(k => _entryBuilder.BuildEntryOptions<T>(k, default, token)).ToArray();
        return await GetCacheEntriesInnerAsync<T>(options, policy, token).ConfigureAwait(false);
    }

    public ValueTask<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, CachePolicy? policy = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        policy ??= _defaultPolicy;
        var duration = policy.DistributedExpiration;
        var writeExpiration = _clock.ToDateTimeOffset(ResolveWriteDuration(policy));
        return GetOrAddInternalAsync(cacheKey, generator, writeExpiration, duration, policy, token);
    }

    public ValueTask<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, TimeSpan? expiration = null, CachePolicy? policy = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        policy ??= _defaultPolicy;
        var duration = expiration ?? policy.DistributedExpiration;
        return GetOrAddInternalAsync(cacheKey, generator, _clock.ToDateTimeOffset(ResolveWriteDuration(policy, expiration)), duration, policy, token);
    }

    public ValueTask<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, DateTimeOffset? expiration = null, CachePolicy? policy = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        policy ??= _defaultPolicy;
        TimeSpan? duration;
        if (expiration.HasValue)
        {
            duration = expiration.Value - _clock.UtcNow;
            if (duration is { } d && d <= TimeSpan.Zero) { duration = null; }
        }
        else
        {
            duration = policy.DistributedExpiration;
            expiration = _clock.ToDateTimeOffset(ResolveWriteDuration(policy));
        }
        return GetOrAddInternalAsync(cacheKey, generator, expiration, duration, policy, token);
    }

    public ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<T, TState>(KeyValuePair<CacheKey, TState>[] entries, Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, CachePolicy? policy = null, CancellationToken token = default)
        where TState : notnull
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(generator);
        policy ??= _defaultPolicy;
        var duration = policy.DistributedExpiration;
        var writeExpiration = _clock.ToDateTimeOffset(ResolveWriteDuration(policy));
        return GetOrAddBatchInternalAsync<T, TState>(entries, generator, writeExpiration, duration, policy, token);
    }

    public ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<T, TState>(KeyValuePair<CacheKey, TState>[] entries, Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, TimeSpan? expiration = null, CachePolicy? policy = null, CancellationToken token = default)
        where TState : notnull
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(generator);
        policy ??= _defaultPolicy;
        var duration = expiration ?? policy.DistributedExpiration;
        return GetOrAddBatchInternalAsync<T, TState>(entries, generator, _clock.ToDateTimeOffset(ResolveWriteDuration(policy, expiration)), duration, policy, token);
    }

    public ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<T, TState>(KeyValuePair<CacheKey, TState>[] entries, Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, DateTimeOffset? expiration = null, CachePolicy? policy = null, CancellationToken token = default)
        where TState : notnull
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(generator);
        policy ??= _defaultPolicy;
        TimeSpan? duration;
        if (expiration.HasValue)
        {
            duration = expiration.Value - _clock.UtcNow;
            if (duration is { } d && d <= TimeSpan.Zero) { duration = null; }
        }
        else
        {
            duration = policy.DistributedExpiration;
            expiration = _clock.ToDateTimeOffset(ResolveWriteDuration(policy));
        }
        return GetOrAddBatchInternalAsync<T, TState>(entries, generator, expiration, duration, policy, token);
    }

    private async ValueTask<T?> GetOrAddInternalAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, DateTimeOffset? expiration, TimeSpan? effectiveDuration, CachePolicy policy, CancellationToken token)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var cacheEntryOptions = _entryBuilder.BuildEntryOptions<T>(cacheKey, expiration, token);

        var entry = await GetCacheEntryInnerAsync<T>(cacheEntryOptions, policy).ConfigureAwait(false);
        if (entry.Found)
        {
            TryRehydrate(cacheKey, entry.Expiration, entry.Value, generator, policy, effectiveDuration);
            return entry.Value;
        }

        var result = await RunUnderLocksAsync<ICacheEntry<T?>>(
            cacheEntryOptions.CacheKey,
            () => GetCacheEntryInnerAsync<T>(cacheEntryOptions, policy),
            e => e.Found,
            ct => RunGeneratorAndStoreEntryAsync(cacheEntryOptions, generator, policy, ct),
            token,
            policyLock: policy.Lock).ConfigureAwait(false);
        return result.Value;
    }

    private void TryRehydrate<T>(CacheKey originalCacheKey, DateTimeOffset entryExpiration, T? currentValue, Func<CancellationToken, Task<T?>> generator, CachePolicy policy, TimeSpan? effectiveDuration)
    {
        if (policy.RehydrateEnabled != true || policy.Rehydrate is null)
        {
            return;
        }
        if (currentValue is null && _multiLayerCacheOptions.CacheNullValues)
        {
            return;
        }
        var resolvedDuration = effectiveDuration ?? policy.DistributedExpiration ?? _multiLayerCacheOptions.DefaultExpiration;
        if (resolvedDuration is not { } duration || duration <= TimeSpan.Zero)
        {
            return;
        }
        _rehydrator.TryTrigger(
            originalCacheKey,
            entryExpiration,
            policy,
            duration,
            kind: "cache",
            rehydrateAsync: async ct =>
            {
                var newValue = await generator(ct).ConfigureAwait(false);
                if (newValue is null && !_multiLayerCacheOptions.CacheNullValues)
                {
                    return;
                }
                // Factory transitions to null: preserve the original deadline so the null doesn't get a fresh TTL window.
                var rehydrateExpiration = newValue is null
                    ? entryExpiration
                    : _clock.UtcNow.Add(duration);
                var rehydrateOptions = _entryBuilder.BuildEntryOptions<T>(originalCacheKey, rehydrateExpiration, ct);
                var innerCacheDisconnected = GetInnerCacheDisconnected();
                var fired = innerCacheDisconnected || await _eventPublisher.CacheSetAsync(rehydrateOptions).ConfigureAwait(false);
                var written = fired && await InternalSetAsync(rehydrateOptions, newValue, innerCacheDisconnected, policy).ConfigureAwait(false);
                if (!written)
                {
                    throw new RehydrateWriteFailedException(originalCacheKey.Name);
                }
            });
    }

    /// <summary>What the batch rehydrate write needs to know about one state.</summary>
    private readonly record struct RehydrateTarget(CacheEntryOptions Options, DateTimeOffset Expiration, CacheKey CallerKey);

    /// <summary>Coalesces the rehydration of every hit past its threshold into one background generator call.</summary>
    private void TryRehydrateBatch<T, TState>(
        List<(CacheKey CallerKey, TState State, CacheEntryOptions Options, DateTimeOffset Expiration, T? Value)> hits,
        Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator,
        CachePolicy policy,
        TimeSpan? effectiveDuration)
        where TState : notnull
    {
        if (policy.RehydrateEnabled != true || policy.Rehydrate is null)
        {
            return;
        }
        var resolvedDuration = effectiveDuration ?? policy.DistributedExpiration ?? _multiLayerCacheOptions.DefaultExpiration;
        if (resolvedDuration is not { } duration || duration <= TimeSpan.Zero)
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
            rehydrateAsync: (rehydrateKeys, ct) =>
                RehydrateReservedAsync<T, TState>(rehydrateKeys, stateByCallerKey, byState, generator, policy, duration, ct));
    }

    /// <summary>The hits worth rehydrating, plus the two lookups the background callback needs.</summary>
    private (List<(CacheKey Key, DateTimeOffset Expiration)> Candidates, Dictionary<CacheKey, TState> StateByCallerKey, Dictionary<TState, RehydrateTarget> ByState) SelectRehydrateCandidates<T, TState>(
        List<(CacheKey CallerKey, TState State, CacheEntryOptions Options, DateTimeOffset Expiration, T? Value)> hits)
        where TState : notnull
    {
        var candidates = new List<(CacheKey Key, DateTimeOffset Expiration)>(hits.Count);
        var stateByCallerKey = new Dictionary<CacheKey, TState>(hits.Count);
        var byState = new Dictionary<TState, RehydrateTarget>(hits.Count);
        foreach (var hit in hits)
        {
            if (hit.Value is null && _multiLayerCacheOptions.CacheNullValues)
            {
                continue;
            }
            candidates.Add((hit.CallerKey, hit.Expiration));
            stateByCallerKey[hit.CallerKey] = hit.State;
            byState[hit.State] = new RehydrateTarget(hit.Options, hit.Expiration, hit.CallerKey);
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
        CancellationToken token)
        where TState : notnull
    {
        var rehydrateStates = MapReservedKeysToStates(reservedKeys, stateByCallerKey);
        var produced = await generator(rehydrateStates, token).ConfigureAwait(false);

        var groups = GroupRehydratedByExpiration<T, TState>(produced, rehydrateStates, byState, duration);
        if (groups.Count == 0)
        {
            return;
        }

        await WriteRehydrateGroupsAsync(groups, policy, token).ConfigureAwait(false);
    }

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

    /// <summary>Groups the produced pairs by target expiration, which <c>InternalSetAsync</c> applies per write.</summary>
    private Dictionary<DateTimeOffset, List<(CacheEntryValue<T> Entry, CacheKey CallerKey)>> GroupRehydratedByExpiration<T, TState>(
        KeyValuePair<TState, T?>[]? produced,
        TState[] rehydrateStates,
        Dictionary<TState, RehydrateTarget> byState,
        TimeSpan duration)
        where TState : notnull
    {
        var requested = new HashSet<TState>(rehydrateStates);
        var seen = new HashSet<TState>(rehydrateStates.Length);

        var freshExpiration = _clock.UtcNow.Add(duration);
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

    /// <summary>Publishes then writes one expiration group at a time, reporting every caller key that failed.</summary>
    private async ValueTask WriteRehydrateGroupsAsync<T>(
        Dictionary<DateTimeOffset, List<(CacheEntryValue<T> Entry, CacheKey CallerKey)>> groups,
        CachePolicy policy,
        CancellationToken token)
    {
        var failed = new List<string>();
        var innerCacheDisconnected = GetInnerCacheDisconnected();
        foreach (var group in groups.Values)
        {
            var fired = innerCacheDisconnected || await PublishCacheSetEventsAsync(group).ConfigureAwait(false);
            var written = fired && await InternalSetAsync<T>(group.Select(e => e.Entry).ToArray(), innerCacheDisconnected, policy, token).ConfigureAwait(false);
            if (!written)
            {
                failed.AddRange(group.Select(e => e.CallerKey.Name));
            }
        }

        if (failed.Count > 0)
        {
            throw new RehydrateWriteFailedException(string.Join(",", failed));
        }
    }

    /// <summary>Publishes a <c>CacheSet</c> event per entry, stopping at the first one that does not fire.</summary>
    private async ValueTask<bool> PublishCacheSetEventsAsync<T>(List<(CacheEntryValue<T> Entry, CacheKey CallerKey)> group)
    {
        foreach (var (entry, _) in group)
        {
            if (!await _eventPublisher.CacheSetAsync(entry.CacheEntry).ConfigureAwait(false))
            {
                return false;
            }
        }
        return true;
    }

    private async ValueTask<ICacheEntry<T?>> RunGeneratorAndStoreEntryAsync<T>(CacheEntryOptions cacheEntryOptions, Func<CancellationToken, Task<T?>> generator, CachePolicy policy, CancellationToken token)
    {
        LogCacheMissed(cacheEntryOptions.CacheKey);
        var ret = await InvokeFactoryAsync(cacheEntryOptions.CacheKey, generator, policy.FactoryTimeout, token).ConfigureAwait(false);

        if (ret is not null || _multiLayerCacheOptions.CacheNullValues)
        {
            var innerCacheDisconnected = GetInnerCacheDisconnected();
            await InternalSetAsync(cacheEntryOptions, ret, innerCacheDisconnected, policy).ConfigureAwait(false);
        }
        return _cacheEntryFactory.Create<T?>(ret, cacheEntryOptions.Expiration);
    }

    private async ValueTask<KeyValuePair<TState, T?>[]> GetOrAddBatchInternalAsync<T, TState>(
        KeyValuePair<CacheKey, TState>[] entries,
        Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator,
        DateTimeOffset? expiration,
        TimeSpan? effectiveDuration,
        CachePolicy policy,
        CancellationToken token)
        where TState : notnull
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        ArgumentNullException.ThrowIfNull(entries);

        var (states, keyIndexOfState, callerKeys, options, firstStateOfKey) = DistinctEntries<T, TState>(entries, expiration, token);
        if (states.Length == 0)
        {
            return [];
        }

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

        if (hits.Count > 0)
        {
            TryRehydrateBatch(hits, generator, policy, effectiveDuration);
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

        var lockKey = CompositeCacheKey.For(Array.ConvertAll(missOptions, o => o.CacheKey));

        var latest = missProbe;
        var resolved = await RunUnderLocksAsync(
            lockKey,
            async () =>
            {
                latest = await GetCacheEntriesInnerAsync<T>(missOptions, policy, token).ConfigureAwait(false);
                return latest;
            },
            probed => Array.TrueForAll(probed, e => e.Value.Found),
            ct => RunBatchGeneratorAndStoreAsync(missOptions, missStates, latest, generator, policy, ct),
            token,
            policyLock: policy.Lock).ConfigureAwait(false);

        for (var i = 0; i < missIndices.Count; i++)
        {
            values[missIndices[i]] = resolved[i].Value.Value;
        }

        return Project(states, keyIndexOfState, values);
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
        for (var i = 0; i < stillMissing.Count; i++)
        {
            requestStates[i] = missStates[stillMissing[i]];
            mappedKeys[i] = missOptions[stillMissing[i]].CacheKey;
        }

        var telemetryKey = CompositeCacheKey.For(mappedKeys);
        LogBatchCacheMissed(telemetryKey, stillMissing.Count);

        var produced = await InvokeFactoryAsync(telemetryKey, ct => generator(requestStates, ct), policy.FactoryTimeout, token).ConfigureAwait(false);
        var producedByState = SelectRequestedProduced(produced, requestStates);

        var toStore = SelectEntriesToStore<T, TState>(missOptions, missStates, stillMissing, producedByState);
        if (toStore.Count > 0)
        {
            var innerCacheDisconnected = GetInnerCacheDisconnected();
            await InternalSetAsync<T>(toStore.ToArray(), innerCacheDisconnected, policy, token).ConfigureAwait(false);
        }

        return BuildBatchEntries<T, TState>(missOptions, missStates, probe, producedByState);
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
            if (!producedByState.TryGetValue(missStates[index], out var value))
            {
                continue;
            }
            if (value is null && !_multiLayerCacheOptions.CacheNullValues)
            {
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
        Dictionary<TState, T?> producedByState)
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
            var wasProduced = producedByState.TryGetValue(missStates[i], out var value);
            results[i] = new KeyValuePair<CacheKey, ICacheEntry<T?>>(
                missOptions[i].CacheKey,
                _cacheEntryFactory.Create<T?>(value, wasProduced ? missOptions[i].Expiration : DateTimeOffset.MinValue));
        }
        return results;
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

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, T? value, CachePolicy? policy = null, CancellationToken token = default)
    {
        policy ??= _defaultPolicy;
        return SetAsync(cacheKey, value, ResolveWriteDuration(policy), policy, token);
    }

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, T? value, TimeSpan? expiration = null, CachePolicy? policy = null, CancellationToken token = default)
    {
        policy ??= _defaultPolicy;
        return SetAsync(cacheKey, value, _clock.ToDateTimeOffset(ResolveWriteDuration(policy, expiration)), policy, token);
    }

    public async ValueTask<bool> SetAsync<T>(CacheKey cacheKey, T? value, DateTimeOffset? expiration = null, CachePolicy? policy = null, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        policy ??= _defaultPolicy;
        expiration ??= _clock.ToDateTimeOffset(ResolveWriteDuration(policy));
        var cacheEntryOptions = _entryBuilder.BuildEntryOptions<T>(cacheKey, expiration, token);
        if (value is null && !_multiLayerCacheOptions.CacheNullValues)
        {
            return await RemoveAsync<T>(cacheEntryOptions).ConfigureAwait(false);
        }

        LogReplacingCachedKey(cacheEntryOptions.CacheKey);
        var innerCacheDisconnected = GetInnerCacheDisconnected();
        if (innerCacheDisconnected)
        {
            LogSettingLocalOnly(cacheEntryOptions.CacheKey);
            return await InternalSetAsync(cacheEntryOptions, value, innerCacheDisconnected, policy).ConfigureAwait(false);
        }
        else
        {
            var fired = await _eventPublisher.CacheSetAsync(cacheEntryOptions).ConfigureAwait(false);
            return fired && await InternalSetAsync(cacheEntryOptions, value, innerCacheDisconnected, policy).ConfigureAwait(false);
        }
    }


    public ValueTask<bool> SetAsync<T>(KeyValuePair<CacheKey, T?>[] keyValues, CachePolicy? policy = null, CancellationToken token = default)
    {
        policy ??= _defaultPolicy;
        return SetAsync(keyValues, ResolveWriteDuration(policy), policy, token);
    }

    public ValueTask<bool> SetAsync<T>(KeyValuePair<CacheKey, T?>[] keyValues, TimeSpan? expiration = null, CachePolicy? policy = null, CancellationToken token = default)
    {
        policy ??= _defaultPolicy;
        return SetAsync(keyValues, _clock.ToDateTimeOffset(ResolveWriteDuration(policy, expiration)), policy, token);
    }

    public async ValueTask<bool> SetAsync<T>(KeyValuePair<CacheKey, T?>[] keyValues, DateTimeOffset? expiration = null, CachePolicy? policy = null, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        policy ??= _defaultPolicy;
        expiration ??= _clock.ToDateTimeOffset(ResolveWriteDuration(policy));
        var removeEntries = new List<CacheEntryOptions>();
        var setEntries = new List<CacheEntryValue<T>>();
        foreach (var keyValue in keyValues)
        {
            if (keyValue.Value is null && !_multiLayerCacheOptions.CacheNullValues)
            {
                removeEntries.Add(_entryBuilder.BuildEntryOptions<T>(keyValue.Key, token: token));
            }
            else
            {
                setEntries.Add(new (_entryBuilder.BuildEntryOptions<T>(keyValue.Key, expiration, token), keyValue.Value));
            }
        }

        if (removeEntries.Count > 0)
        {
            var result = await RemoveAsync<T>(removeEntries.ToArray(), token).ConfigureAwait(false);
            if (!result)
            {
                return false;
            }
        }

        var innerCacheDisconnected = GetInnerCacheDisconnected();
        var internalSetResult = await InternalSetAsync<T>(setEntries.ToArray(), innerCacheDisconnected, policy, token).ConfigureAwait(false);
        if (!internalSetResult)
        {
            return false;
        }

        if (innerCacheDisconnected)
        {
            if (_logger.IsEnabled(LogLevel.Trace))
            {
                LogSettingLocalOnlyForCacheKeys(string.Join(",", setEntries.Select(o => o.CacheEntry.CacheKey)));
            }
            return true;
        }

        foreach (var cacheEntry in setEntries.Select(s => s.CacheEntry))
        {
            LogReplacingCachedKey(cacheEntry.CacheKey);
            var fired = await _eventPublisher.CacheSetAsync(cacheEntry).ConfigureAwait(false);
            if (!fired)
            {
                return false;
            }
        }

        return true;
    }

    public ValueTask<bool> RemoveAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return RemoveAsync<T>(_entryBuilder.BuildEntryOptions<T>(cacheKey, default, token));
    }

    public ValueTask<bool> RemoveAsync<T>(CacheKey[] cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var options = cacheKey.Select(k => _entryBuilder.BuildEntryOptions<T>(k, default)).ToArray();
        return RemoveAsync<T>(options, token);
    }

    public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, CachePolicy? policy = null, CancellationToken token = default)
    {
        policy ??= _defaultPolicy;
        return RefreshAsync<T>(cacheKey, ResolveWriteDuration(policy), policy, token);
    }

    public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, TimeSpan? expiration = null, CachePolicy? policy = null, CancellationToken token = default)
    {
        policy ??= _defaultPolicy;
        return RefreshAsync<T>(cacheKey, _clock.ToDateTimeOffset(ResolveWriteDuration(policy, expiration)), policy, token);
    }

    public async ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, DateTimeOffset? expiration = null, CachePolicy? policy = null, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        policy ??= _defaultPolicy;
        expiration ??= _clock.ToDateTimeOffset(ResolveWriteDuration(policy));
        var cacheEntryOptions = _entryBuilder.BuildEntryOptions<T>(cacheKey, expiration, token);
        LogClearingCached(cacheEntryOptions.CacheKey);
        _memoryCache.Remove(cacheEntryOptions.CacheKey);
        LogRefreshingInnerCacheKey(cacheEntryOptions.CacheKey, cacheEntryOptions.Expiration);
        try
        {
            var fired = await _eventPublisher.CacheRefreshedAsync(cacheEntryOptions).ConfigureAwait(false);
            return fired && await _innerCache.RefreshAsync<T>(cacheEntryOptions.CacheKey, cacheEntryOptions.Expiration, policy, token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogInnerCacheRefreshError(ex, cacheKey);
            return false;
        }
    }

    public async ValueTask<bool> ContainsAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var cacheEntryOptions = _entryBuilder.BuildEntryOptions<T>(cacheKey, default, token);
        try
        {
            return _memoryCache.TryGetValue(cacheEntryOptions.CacheKey, out _) || await _innerCache.ContainsAsync<T>(cacheEntryOptions.CacheKey, cacheEntryOptions.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogInnerCacheContainsError(ex, cacheKey);
            return false;
        }
    }

    public async ValueTask<TimeSpan?> TimeToLiveAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var cacheEntryOptions = _entryBuilder.BuildEntryOptions<T>(cacheKey, default, token);
        return _memoryCache.TryGetValue<ICacheEntry>(cacheEntryOptions.CacheKey, out var value)
            ? value?.Expiration.Subtract(_clock.UtcNow)
            : await _innerCache.TimeToLiveAsync<T>(cacheEntryOptions.CacheKey, token);
    }

    public async ValueTask<DateTimeOffset?> ExpireTimeAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var cacheEntryOptions = _entryBuilder.BuildEntryOptions<T>(cacheKey, default, token);

        return _memoryCache.TryGetValue<ICacheEntry>(cacheEntryOptions.CacheKey, out var value)
            ? value?.Expiration
            : await _innerCache.ExpireTimeAsync<T>(cacheEntryOptions.CacheKey, token);
    }

    private async ValueTask<bool> RemoveAsync<T>(CacheEntryOptions options)
    {
        LogClearingLocalCached(options.CacheKey);
        try
        {
            _memoryCache.Remove(options.CacheKey);
            var removed = await _innerCache.RemoveAsync<T>(options.CacheKey, options.Token).ConfigureAwait(false);
            var eventFired = await _eventPublisher.CacheRemovedAsync(options).ConfigureAwait(false);
            return removed && eventFired;
        }
        catch (Exception ex)
        {
            LogInnerCacheRemoveError(ex, options.CacheKey);
            return false;
        }
    }

    private async ValueTask<bool> RemoveAsync<T>(CacheEntryOptions[] options, CancellationToken token = default)
    {
        try
        {
            var removeInnerResult = await _innerCache.RemoveAsync<T>(options.Select(o => o.CacheKey).ToArray(), token).ConfigureAwait(false);
            if (!removeInnerResult)
            {
                return false;
            }

            foreach (var option in options)
            {
                _memoryCache.Remove(option.CacheKey);
            }

            foreach (var option in options)
            {
                var removedEventPublished = await _eventPublisher.CacheRemovedAsync(option).ConfigureAwait(false);
                if (!removedEventPublished)
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Warning))
            {
                LogInnerCacheRemoveKeysError(ex, string.Join(",", options.Select(o => o.CacheKey)));
            }
            return false;
        }
    }

    private async ValueTask<KeyValuePair<CacheKey, T?>[]> GetInnerAsync<T>(CacheEntryOptions[] options, CachePolicy policy, CancellationToken token = default)
    {
        List<KeyValuePair<CacheKey, T?>> results = [];
        List<CacheEntryOptions> cacheEntriesToFetch = [];
        foreach (var option in options)
        {
            if (_memoryCache.TryGetValue<ICacheEntry<T>>(option.CacheKey, out var entry))
            {
                LogFoundLocal(option.CacheKey);
                if (_connectionState.IsConnected)
                {
                    results.Add(new KeyValuePair<CacheKey, T?>(option.CacheKey, entry!.Value));
                }
                else if (_useLocalOnlyWhenDisconnected)
                {
                    LogUsingPrimaryOnlyWhenDisconnected(option.CacheKey);
                    results.Add(new KeyValuePair<CacheKey, T?>(option.CacheKey, entry!.Value));
                }
                else
                {
                    LogReturningDefaultDisconnected(option.CacheKey);
                    _memoryCache.Remove(option.CacheKey);
                }
            }
            else
            {
                cacheEntriesToFetch.Add(option);
            }
        }

        var keys = cacheEntriesToFetch.Select(c => c.CacheKey).ToArray();
        if (keys.Length == 0)
        {
            return results.ToArray();
        }

        var fetched = await _innerCache.GetCacheEntriesAsync<T>(keys, policy, token).ConfigureAwait(false);

        for (int i = 0; i < keys.Length; i++)
        {
            var entry = fetched[i].Value;
            var key = fetched[i].Key;
            results.Add(new KeyValuePair<CacheKey, T?>(key, entry.Value));

            if (!entry.Found)
            {
                continue;
            }

            LogFoundInnerCacheCopy(key);
            var option = cacheEntriesToFetch[i];
            option.Expiration = entry.Expiration;
            MemorySet(option, new KeyValuePair<CacheKey, T?>(key, entry.Value), policy.LocalExpiration ?? _multiLayerCacheOptions.LocalMaxExpiration);
        }

        return results.ToArray();
    }

    private async ValueTask<T?> GetInnerAsync<T>(CacheEntryOptions options, CachePolicy policy)
    {
        if (_memoryCache.TryGetValue<ICacheEntry<T>>(options.CacheKey, out var entry))
        {
            LogFoundLocal(options.CacheKey);
            if(_connectionState.IsConnected)
            {
                return entry!.Value;
            }
            else if (_useLocalOnlyWhenDisconnected)
            {
                LogUsingPrimaryOnlyWhenDisconnected(options.CacheKey);
                return entry!.Value;
            }
            else
            {
                LogReturningDefaultDisconnected(options.CacheKey);
                _memoryCache.Remove(options.CacheKey);
                return default;
            }
        }

        var fetched = await _innerCache.GetCacheEntryAsync<T>(options.CacheKey, policy, options.Token).ConfigureAwait(false);

        if (!fetched.Found)
        {
            return default;
        }

        LogFoundInnerCacheCopy(options.CacheKey);
        options.Expiration = fetched.Expiration;
        MemorySet(options, fetched.Value, policy.LocalExpiration ?? _multiLayerCacheOptions.LocalMaxExpiration);
        return fetched.Value;
    }

    private async ValueTask<KeyValuePair<CacheKey, ICacheEntry<T?>>[]> GetCacheEntriesInnerAsync<T>(CacheEntryOptions[] options, CachePolicy policy, CancellationToken token = default)
    {
        var results = new KeyValuePair<CacheKey, ICacheEntry<T?>>[options.Length];
        List<int> missIndices = [];
        List<CacheEntryOptions> cacheEntriesToFetch = [];
        for (int i = 0; i < options.Length; i++)
        {
            var option = options[i];
            if (_memoryCache.TryGetValue<ICacheEntry<T?>>(option.CacheKey, out var entry))
            {
                LogFoundLocal(option.CacheKey);
                if (_connectionState.IsConnected || _useLocalOnlyWhenDisconnected)
                {
                    if (!_connectionState.IsConnected)
                    {
                        LogUsingPrimaryOnlyWhenDisconnected(option.CacheKey);
                    }
                    results[i] = new KeyValuePair<CacheKey, ICacheEntry<T?>>(option.CacheKey, entry!);
                    continue;
                }

                LogReturningDefaultDisconnected(option.CacheKey);
                _memoryCache.Remove(option.CacheKey);
                results[i] = new KeyValuePair<CacheKey, ICacheEntry<T?>>(option.CacheKey, _cacheEntryFactory.Create<T?>(default, DateTimeOffset.MinValue));
                continue;
            }

            missIndices.Add(i);
            cacheEntriesToFetch.Add(option);
        }

        if (cacheEntriesToFetch.Count == 0)
        {
            return results;
        }

        var keys = cacheEntriesToFetch.Select(c => c.CacheKey).ToArray();
        var fetched = await _innerCache.GetCacheEntriesAsync<T>(keys, policy, token).ConfigureAwait(false);

        for (int j = 0; j < keys.Length; j++)
        {
            var resultIndex = missIndices[j];
            var entry = fetched[j].Value;
            var key = fetched[j].Key;
            results[resultIndex] = new KeyValuePair<CacheKey, ICacheEntry<T?>>(key, entry);

            if (!entry.Found)
            {
                continue;
            }

            LogFoundInnerCacheCopy(key);
            var option = cacheEntriesToFetch[j];
            option.Expiration = entry.Expiration;
            MemorySet(option, new KeyValuePair<CacheKey, T?>(key, entry.Value), policy.LocalExpiration ?? _multiLayerCacheOptions.LocalMaxExpiration);
        }

        return results;
    }

    private async ValueTask<ICacheEntry<T?>> GetCacheEntryInnerAsync<T>(CacheEntryOptions options, CachePolicy policy)
    {
        if (_memoryCache.TryGetValue<ICacheEntry<T?>>(options.CacheKey, out var entry))
        {
            LogFoundLocal(options.CacheKey);
            if (_connectionState.IsConnected || _useLocalOnlyWhenDisconnected)
            {
                if (!_connectionState.IsConnected)
                {
                    LogUsingPrimaryOnlyWhenDisconnected(options.CacheKey);
                }
                return entry!;
            }

            LogReturningDefaultDisconnected(options.CacheKey);
            _memoryCache.Remove(options.CacheKey);
            return _cacheEntryFactory.Create<T?>(default, DateTimeOffset.MinValue);
        }

        var fetched = await _innerCache.GetCacheEntryAsync<T>(options.CacheKey, policy, options.Token).ConfigureAwait(false);

        if (!fetched.Found)
        {
            return fetched;
        }

        LogFoundInnerCacheCopy(options.CacheKey);
        options.Expiration = fetched.Expiration;
        MemorySet(options, fetched.Value, policy.LocalExpiration ?? _multiLayerCacheOptions.LocalMaxExpiration);
        return fetched;
    }

    private async ValueTask<bool> InternalSetAsync<T>(CacheEntryOptions options, T? value, bool innerCacheDisconnected, CachePolicy policy)
    {
        try
        {
            if (innerCacheDisconnected)
            {
                LogSettingLocalOnly(options.CacheKey);
                return MemorySet(options, value, policy.LocalExpirationDisconnected ?? _multiLayerCacheOptions.LocalMaxExpirationDisconnected);
            }

            var ret = await _innerCache.SetAsync<T?>(options.CacheKey, value, options.Expiration, policy, options.Token).ConfigureAwait(false);
            return ret && MemorySet(options, value, policy.LocalExpiration ?? _multiLayerCacheOptions.LocalMaxExpiration);
        }
        catch (Exception ex)
        {
            LogInnerCacheSetError(ex, options.CacheKey);
            return false;
        }
    }

    private async ValueTask<bool> InternalSetAsync<T>(CacheEntryValue<T>[] cacheEntries, bool innerCacheDisconnected, CachePolicy policy, CancellationToken token = default)
    {
        try
        {
            var cacheKeyValuePairs = cacheEntries.Select(c => new KeyValuePair<CacheKey, T?>(c.CacheEntry.CacheKey, c.Value)).ToArray();

            if (innerCacheDisconnected)
            {
                if (_logger.IsEnabled(LogLevel.Trace))
                {
                    LogSettingLocalOnlyForCacheKeys(string.Join(",", cacheEntries.Select(o => o.CacheEntry.CacheKey)));
                }
                return MemSet(policy.LocalExpirationDisconnected ?? _multiLayerCacheOptions.LocalMaxExpirationDisconnected);
            }

            DateTimeOffset? batchExpiration = cacheEntries.Length > 0 ? cacheEntries[0].CacheEntry.Expiration : null;
            var set = await _innerCache.SetAsync<T?>(cacheKeyValuePairs, batchExpiration, policy, token).ConfigureAwait(false);
            return set && MemSet(policy.LocalExpiration ?? _multiLayerCacheOptions.LocalMaxExpiration);
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Warning))
            {
                LogInnerCacheSetKeysError(ex, string.Join(",", cacheEntries.Select(o => o.CacheEntry.CacheKey)));
            }
            return false;
        }

        bool MemSet(TimeSpan? maxExpiration)
        {
            foreach (var cacheEntry in cacheEntries)
            {
                var set = MemorySet(cacheEntry.CacheEntry, cacheEntry.Value, maxExpiration);
                if (!set)
                {
                    return false;
                }
            }
            return true;
        }
    }

    private bool MemorySet<T>(CacheEntryOptions options, T value, TimeSpan? maxExpiration)
    {
        var item = _cacheEntryFactory.Create(value, options.Expiration);
        return _localMemorySetter.Set(options, item, typeof(T), maxExpiration);
    }

    private readonly struct CacheEntryValue<T>(CacheEntryOptions cacheEntry, T? value)
    {
        public CacheEntryOptions CacheEntry { get; init; } = cacheEntry;
        public T? Value { get; init; } = value;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Cache missed. generating new {CacheKey}")]
    private partial void LogCacheMissed(CacheKey cacheKey);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Batch cache missed. generating {Count} keys for {CacheKey}")]
    private partial void LogBatchCacheMissed(CacheKey cacheKey, int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Replacing cached key {CacheKey}")]
    private partial void LogReplacingCachedKey(CacheKey cacheKey);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Inner cache is not connected. Setting local only for cacheKey {CacheKey}")]
    private partial void LogSettingLocalOnly(CacheKey cacheKey);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Inner cache is not connected. Setting local only for cacheKeys {CacheKeys}")]
    private partial void LogSettingLocalOnlyForCacheKeys(string cacheKeys);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Clearing cached. Key {CacheKey}")]
    private partial void LogClearingCached(CacheKey cacheKey);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Refreshing inner cache key {CacheKey} at expiration {Expiration}")]
    private partial void LogRefreshingInnerCacheKey(CacheKey cacheKey, DateTimeOffset? expiration);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inner cache refresh value for cacheKey {CacheKey}")]
    private partial void LogInnerCacheRefreshError(Exception ex, CacheKey cacheKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inner cache contains for cacheKey {CacheKey}")]
    private partial void LogInnerCacheContainsError(Exception ex, CacheKey cacheKey);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Clearing local cached. cacheKey {CacheKey}")]
    private partial void LogClearingLocalCached(CacheKey cacheKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inner cache remove cacheKey {CacheKey}")]
    private partial void LogInnerCacheRemoveError(Exception ex, CacheKey cacheKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inner cache remove cacheKeys {CacheKeys}")]
    private partial void LogInnerCacheRemoveKeysError(Exception ex, string cacheKeys);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Found local. {CacheKey}")]
    private partial void LogFoundLocal(CacheKey cacheKey);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Using primary only when disconnected. Returning local for cacheKey {CacheKey}")]
    private partial void LogUsingPrimaryOnlyWhenDisconnected(CacheKey cacheKey);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Inner cache is not connected. Returning default for cacheKey {CacheKey}")]
    private partial void LogReturningDefaultDisconnected(CacheKey cacheKey);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Found inner cache copy at cacheKey {CacheKey}")]
    private partial void LogFoundInnerCacheCopy(CacheKey cacheKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inner cache set value for {CacheKey}")]
    private partial void LogInnerCacheSetError(Exception ex, CacheKey cacheKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inner cache set value for {CacheKeys}")]
    private partial void LogInnerCacheSetKeysError(Exception ex, string cacheKeys);
}
