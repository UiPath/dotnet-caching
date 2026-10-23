namespace UiPath.Caching;

internal sealed partial class MultilayerHashCache
{
    public ValueTask<IReadOnlyList<CacheKey>> RemoveManyAsync<T>(IEnumerable<CacheKey> cacheKeys, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return RunBatchAsync(
            cacheKeys,
            (Cache: this, Token: token),
            static key => key,
            static (s, chunk, count, announce) => s.Cache.RemoveChunkAsync<T>(chunk, count, announce, s.Token),
            static (s, first) => s.Cache._eventPublisher.TopicInvalidatedAsync(s.Cache._entryBuilder.BuildEntryOptions<T>(first, CancellationToken.None).TopicKey),
            token);
    }

    public ValueTask<IReadOnlyList<CacheKey>> SetManyAsync<T>(IEnumerable<KeyValuePair<CacheKey, IDictionary<string, T?>>> keyValues, HashCacheEntryOptions options, CachePolicy? policy, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        policy ??= _defaultPolicy;
        return RunBatchAsync(
            keyValues,
            (Cache: this, Options: options, Expiration: GetExpiration(options, policy), Policy: policy, Token: token),
            static kv => kv.Key,
            static (s, chunk, count, announce) => s.Cache.SetChunkAsync(chunk, count, s.Options, s.Expiration, s.Policy, announce, s.Token),
            static (s, first) => s.Cache._eventPublisher.TopicInvalidatedAsync(s.Cache._entryBuilder.BuildEntryOptions<T>(first, CancellationToken.None).TopicKey),
            token);
    }

    // A merge after a removal leaves only its fields; after a write, the write's fields with its own over them.
    private static (IDictionary<string, T?>? Values, HashCacheSetOption SetOption) MergeOver<T>((IDictionary<string, T?>? Values, HashCacheSetOption SetOption) before, IDictionary<string, T?> fields)
    {
        Dictionary<string, T?> combined = before.Values is null ? [] : new(before.Values);
        foreach (var (field, value) in fields)
        {
            combined[field] = value;
        }

        return (combined, before.Values is null ? HashCacheSetOption.KeyReplace : before.SetOption);
    }

    private async ValueTask<List<CacheKey>?> RemoveChunkAsync<T>(CacheKey[] keys, int count, bool announce, CancellationToken token)
    {
        var options = new InternalHashCacheEntryOptions[count];
        var names = new string[count];
        for (var i = 0; i < count; i++)
        {
            options[i] = _entryBuilder.BuildEntryOptions<T>(keys[i], token);
            names[i] = options[i].CacheKey.Name;
        }

        Issued removed;
        using (var turn = EnterInnerWrite(names))
        {
            foreach (var name in names)
            {
                RemoveLocal(name);
            }

            if (turn.Tracked)
            {
                await Settled(turn.Previous, token).ConfigureAwait(false);
            }

            removed = await IssueAsync(options, count, this, static (c, o) => c._innerCache.RemoveAsync<T>(o.CacheKey, o.Token), static (c, o, ex) => c.LogInnerCacheRemoveError(ex, c.Logged(o, typeof(T))), token).ConfigureAwait(false);
        }

        // As the single remove does, a key whose delete failed is still announced: the delete may have landed, and a node that drops its copy costs one read. A cancel announces what it issued.
        var announced = announce
            ? await AllAsync(options, removed.Count, this, static (c, o) => c._eventPublisher.CacheRemovedAsync(o, typeof(T)), static (c, o, ex) => c.LogInnerCacheRemoveError(ex, c.Logged(o, typeof(T)))).ConfigureAwait(false)
            : null;
        if (removed.Cancelled)
        {
            throw new OperationCanceledException(token);
        }

        List<CacheKey>? failed = null;
        for (var i = 0; i < count; i++)
        {
            if (!removed.Results[i] || (announced is not null && !announced[i]))
            {
                (failed ??= []).Add(keys[i]);
            }
        }

        return failed;
    }

    private async ValueTask<List<CacheKey>?> SetChunkAsync<T>(KeyValuePair<CacheKey, IDictionary<string, T?>>[] chunk, int count, HashCacheEntryOptions options, DateTimeOffset expiration, CachePolicy policy, bool announce, CancellationToken token)
    {
        var (keys, entries, entryCount, removals) = Partition(chunk, count, options, expiration, token);
        List<CacheKey>? failed = null;
        if (removals is not null)
        {
            AddFailed(ref failed, await RemoveChunkAsync<T>(removals.ToArray(), removals.Count, announce, token).ConfigureAwait(false));
        }

        if (entryCount == 0)
        {
            return failed;
        }

        if (GetInnerCacheDisconnected())
        {
            KeepDisconnected(keys, entries, entryCount, policy, ref failed);
            return failed;
        }

        var (written, kept, toAnnounce, announceIndex, announceCount, cancelled) = await WriteChunkAsync(entries, entryCount, policy, announce, token).ConfigureAwait(false);
        var published = announce && announceCount > 0
            ? await AllAsync(toAnnounce, announceCount, this, static (c, o) => c.PublishSetAsync<T>(o), static (c, o, ex) => c.LogSetEventError(ex, c.Logged(o, typeof(T)))).ConfigureAwait(false)
            : [];
        if (cancelled)
        {
            throw new OperationCanceledException(token);
        }

        var announcedOk = new bool[entryCount];
        for (var j = 0; j < published.Length; j++)
        {
            announcedOk[announceIndex[j]] = published[j];
        }

        for (var i = 0; i < entryCount; i++)
        {
            if (!written[i] || !kept[i] || (announce && !announcedOk[i]))
            {
                (failed ??= []).Add(keys[i]);
            }
        }

        return failed;
    }

    /// <summary>A key named twice gets what its writes in order would leave.</summary>
    private (CacheKey[] Keys, (InternalHashCacheEntryOptions Options, IDictionary<string, T?> Values)[] Entries, int Count, List<CacheKey>? Removals) Partition<T>(KeyValuePair<CacheKey, IDictionary<string, T?>>[] chunk, int count, HashCacheEntryOptions options, DateTimeOffset expiration, CancellationToken token)
    {
        var folded = Fold(chunk, count, options.SetOption);
        List<CacheKey>? removals = null;
        var entries = new (InternalHashCacheEntryOptions Options, IDictionary<string, T?> Values)[folded.Count];
        var keys = new CacheKey[folded.Count];
        var entryCount = 0;
        foreach (var (key, values, setOption) in folded)
        {
            if (values is null)
            {
                (removals ??= []).Add(key);
                continue;
            }

            var entryOptions = _entryBuilder.BuildEntryOptions<T>(key, expiration, setOption, token) with { Partial = setOption == HashCacheSetOption.HashReplace };
            entryOptions.Metadata = options.Metadata;
            keys[entryCount] = key;
            entries[entryCount++] = (entryOptions, values);
        }

        return (keys, entries, entryCount, removals);
    }

    private List<(CacheKey Key, IDictionary<string, T?>? Values, HashCacheSetOption SetOption)> Fold<T>(KeyValuePair<CacheKey, IDictionary<string, T?>>[] chunk, int count, HashCacheSetOption setOption)
    {
        var merge = setOption == HashCacheSetOption.HashReplace;
        var last = LastOccurrences(chunk, count, static kv => kv.Key);
        var state = last is null ? null : new Dictionary<CacheKey, (IDictionary<string, T?>? Values, HashCacheSetOption SetOption)>(last.Count);
        var folded = new List<(CacheKey Key, IDictionary<string, T?>? Values, HashCacheSetOption SetOption)>(count);
        for (var i = 0; i < count; i++)
        {
            var (key, value) = (chunk[i].Key, chunk[i].Value);
            var next = Single(value, setOption);
            if (state is null)
            {
                folded.Add((key, next.Values, next.SetOption));
                continue;
            }

            // Only a merge of some fields builds on what came before; an empty write stands alone, as a single set does.
            if (merge && !IsNullOrEmpty(value) && state.TryGetValue(key, out var before))
            {
                next = MergeOver(before, next.Values!);
            }

            state[key] = next;
            if (last![key] == i)
            {
                folded.Add((key, state[key].Values, state[key].SetOption));
            }
        }

        return folded;
    }

    /// <summary>What a single set leaves: an empty hash is a removal (null), or with null values cached a replacement.</summary>
    private (IDictionary<string, T?>? Values, HashCacheSetOption SetOption) Single<T>(IDictionary<string, T?>? value, HashCacheSetOption setOption)
    {
        if (!IsNullOrEmpty(value))
        {
            return (value, setOption);
        }

        return _multiLayerCacheOptions.CacheNullValues ? (new Dictionary<string, T?>(), HashCacheSetOption.KeyReplace) : (null, setOption);
    }

    private void KeepDisconnected<T>(CacheKey[] keys, (InternalHashCacheEntryOptions Options, IDictionary<string, T?> Values)[] entries, int entryCount, CachePolicy policy, ref List<CacheKey>? failed)
    {
        var max = policy.LocalExpirationDisconnected ?? _multiLayerCacheOptions.LocalMaxExpirationDisconnected;
        for (var i = 0; i < entryCount; i++)
        {
            LogSettingLocalOnly(Logged(entries[i].Options, typeof(T)));
            if (!ApplyLocal(entries[i].Options.CacheKey.Name, (Cache: this, Entry: entries[i], Max: max), static s => s.Cache.MemorySet(s.Entry.Options, s.Entry.Values, s.Max, keepWhenAbsent: true)))
            {
                (failed ??= []).Add(keys[i]);
            }
        }
    }

    /// <summary>The turn is released before the events.</summary>
    private async ValueTask<(bool[] Written, bool[] Kept, InternalHashCacheEntryOptions[] ToAnnounce, int[] AnnounceIndex, int AnnounceCount, bool Cancelled)> WriteChunkAsync<T>((InternalHashCacheEntryOptions Options, IDictionary<string, T?> Values)[] entries, int entryCount, CachePolicy policy, bool announce, CancellationToken token)
    {
        var kept = new bool[entryCount];
        var toAnnounce = new InternalHashCacheEntryOptions[entryCount];
        var announceIndex = new int[entryCount];
        var announceCount = 0;
        using var turn = EnterInnerWrite(entries.Take(entryCount).Select(e => e.Options.CacheKey.Name));
        if (turn.Tracked)
        {
            await Settled(turn.Previous, token).ConfigureAwait(false);
        }

        var issued = await IssueAsync(
            entries,
            entryCount,
            (Cache: this, Policy: policy),
            static (s, e) => s.Cache._innerCache.SetAsync<T?>(e.Options.CacheKey, e.Values, new HashCacheEntryOptions(e.Options.Expiration, null, e.Options.Metadata, e.Options.SetOption), s.Policy, e.Options.Token),
            static (s, e, ex) => s.Cache.LogInnerCacheSetError(ex, s.Cache.Logged(e.Options, typeof(T))),
            token).ConfigureAwait(false);
        var written = issued.Results;

        for (var i = 0; i < issued.Count; i++)
        {
            if (!written[i])
            {
                // Cut short by the caller's cancel, the write may have landed: nothing stays local, and the event tells the others.
                if (issued.Cancelled)
                {
                    RemoveLocal(entries[i].Options.CacheKey.Name);
                    if (announce)
                    {
                        announceIndex[announceCount] = i;
                        toAnnounce[announceCount++] = entries[i].Options;
                    }
                }

                continue;
            }

            kept[i] = turn.Tracked
                ? KeepAfterInnerWrite(true, false, policy, (Cache: this, entries[i].Options, entries[i].Values, Turn: turn), static (s, max) => s.Turn.KeepLocal(s.Options.CacheKey.Name, (s, max), static x => x.s.Cache.MemorySet(x.s.Options, x.s.Values, x.max)), token)
                : KeepAfterInnerWrite(true, false, policy, (Cache: this, entries[i].Options, entries[i].Values), static (s, max) => s.Cache.MemorySet(s.Options, s.Values, max), token);
            if (announce)
            {
                announceIndex[announceCount] = i;
                toAnnounce[announceCount++] = entries[i].Options;
            }
        }

        return (written, kept, toAnnounce, announceIndex, announceCount, issued.Cancelled);
    }
}
