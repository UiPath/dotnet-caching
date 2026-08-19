namespace UiPath.Caching;

/// <summary>Shared body for the batch <c>GetOrAddAsync</c> default interface methods on <see cref="ICache"/>.</summary>
internal static class BatchGetOrAdd
{
    internal static async ValueTask<KeyValuePair<TState, T?>[]> RunAsync<T, TState>(
        ICache cache,
        KeyValuePair<CacheKey, TState>[] entries,
        Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator,
        Func<KeyValuePair<CacheKey, T?>[], CancellationToken, ValueTask<bool>> setAsync,
        CachePolicy? policy,
        CancellationToken token)
        where TState : notnull
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(generator);
        NotCacheableException.ThrowIfNotCacheable<T>();

        var (states, keys, representative, probeKeys) = DistinctEntries(entries);
        if (states.Count == 0)
        {
            return [];
        }

        var probe = await cache.GetCacheEntriesAsync<T>(probeKeys.ToArray(), policy, token).ConfigureAwait(false);
        var valueByKey = IndexFoundValues<T>(probe);

        var missKeys = probeKeys.Where(k => !valueByKey.ContainsKey(k)).ToArray();
        if (missKeys.Length > 0)
        {
            await GenerateAndStoreMissesAsync(missKeys, representative, valueByKey, generator, setAsync, token).ConfigureAwait(false);
        }

        var results = new KeyValuePair<TState, T?>[states.Count];
        for (var i = 0; i < states.Count; i++)
        {
            valueByKey.TryGetValue(keys[i], out var value);
            results[i] = new KeyValuePair<TState, T?>(states[i], value);
        }
        return results;
    }

    /// <summary>De-duplicates states for the result and keys for the cache, in first-occurrence order.</summary>
    private static (List<TState> States, List<CacheKey> Keys, Dictionary<CacheKey, TState> Representative, List<CacheKey> ProbeKeys) DistinctEntries<TState>(
        KeyValuePair<CacheKey, TState>[] entries)
        where TState : notnull
    {
        var states = new List<TState>(entries.Length);
        var keys = new List<CacheKey>(entries.Length);
        var seenStates = new HashSet<TState>(entries.Length);
        var representative = new Dictionary<CacheKey, TState>(entries.Length);
        var probeKeys = new List<CacheKey>(entries.Length);
        foreach (var entry in entries)
        {
            if (!seenStates.Add(entry.Value))
            {
                continue;
            }
            states.Add(entry.Value);
            keys.Add(entry.Key);
            if (representative.TryAdd(entry.Key, entry.Value))
            {
                probeKeys.Add(entry.Key);
            }
        }
        return (states, keys, representative, probeKeys);
    }

    /// <summary>Indexes the probe's hits by key, since <see cref="ICache"/> promises no ordering.</summary>
    private static Dictionary<CacheKey, T?> IndexFoundValues<T>(KeyValuePair<CacheKey, ICacheEntry<T?>>[] probe)
    {
        var valueByKey = new Dictionary<CacheKey, T?>(probe.Length);
        foreach (var pair in probe)
        {
            if (pair.Value is { Found: true } entry)
            {
                valueByKey[pair.Key] = entry.Value;
            }
        }
        return valueByKey;
    }

    /// <summary>Generates the missing keys' values, writes them back, and adds them to <paramref name="valueByKey"/>.</summary>
    private static async ValueTask GenerateAndStoreMissesAsync<T, TState>(
        CacheKey[] missKeys,
        Dictionary<CacheKey, TState> representative,
        Dictionary<CacheKey, T?> valueByKey,
        Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator,
        Func<KeyValuePair<CacheKey, T?>[], CancellationToken, ValueTask<bool>> setAsync,
        CancellationToken token)
        where TState : notnull
    {
        var requestStates = Array.ConvertAll(missKeys, k => representative[k]);
        var keyOfRequest = new Dictionary<TState, CacheKey>(missKeys.Length);
        for (var i = 0; i < missKeys.Length; i++)
        {
            keyOfRequest[requestStates[i]] = missKeys[i];
        }

        var produced = await generator(requestStates, token).ConfigureAwait(false);

        var claimed = new HashSet<TState>(missKeys.Length);
        var generated = new List<KeyValuePair<CacheKey, T?>>(missKeys.Length);
        foreach (var pair in produced ?? [])
        {
            if (!keyOfRequest.TryGetValue(pair.Key, out var key) || !claimed.Add(pair.Key))
            {
                continue;
            }
            generated.Add(new KeyValuePair<CacheKey, T?>(key, pair.Value));
            valueByKey[key] = pair.Value;
        }

        if (generated.Count > 0)
        {
            await setAsync(generated.ToArray(), token).ConfigureAwait(false);
        }
    }
}
