namespace UiPath.Caching;

internal sealed partial class MultilayerCache
{
    public ValueTask<IReadOnlyList<CacheKey>> RemoveManyAsync<T>(IEnumerable<CacheKey> cacheKeys, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return RunBatchAsync(
            cacheKeys,
            (Cache: this, Token: token),
            static key => key,
            static (s, chunk, count, announce) => s.Cache.RemoveChunkAsync<T>(chunk, count, announce, s.Token),
            static (s, first) => s.Cache._eventPublisher.TopicInvalidatedAsync(s.Cache._entryBuilder.BuildEntryOptions<T>(first, default, CancellationToken.None).TopicKey),
            token);
    }

    public ValueTask<IReadOnlyList<CacheKey>> SetManyAsync<T>(IEnumerable<KeyValuePair<CacheKey, T?>> keyValues, CachePolicy? policy, CancellationToken token = default)
    {
        policy ??= _defaultPolicy;
        return SetManyCoreAsync(keyValues, GetExpiration(policy), policy, token);
    }

    public ValueTask<IReadOnlyList<CacheKey>> SetManyAsync<T>(IEnumerable<KeyValuePair<CacheKey, T?>> keyValues, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) =>
        SetManyCoreAsync(keyValues, GetExpiration(expiration), policy ?? _defaultPolicy, token);

    public ValueTask<IReadOnlyList<CacheKey>> SetManyAsync<T>(IEnumerable<KeyValuePair<CacheKey, T?>> keyValues, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) =>
        SetManyCoreAsync(keyValues, GetExpiration(expiration), policy ?? _defaultPolicy, token);

    private ValueTask<IReadOnlyList<CacheKey>> SetManyCoreAsync<T>(IEnumerable<KeyValuePair<CacheKey, T?>> keyValues, DateTimeOffset expiration, CachePolicy policy, CancellationToken token)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return RunBatchAsync(
            keyValues,
            (Cache: this, Expiration: expiration, Policy: policy, Token: token),
            static kv => kv.Key,
            static (s, chunk, count, announce) => s.Cache.SetChunkAsync(chunk, count, s.Expiration, s.Policy, announce, s.Token),
            static (s, first) => s.Cache._eventPublisher.TopicInvalidatedAsync(s.Cache._entryBuilder.BuildEntryOptions<T>(first, default, CancellationToken.None).TopicKey),
            token);
    }

    private async ValueTask<List<CacheKey>?> RemoveChunkAsync<T>(CacheKey[] keys, int count, bool announce, CancellationToken token)
    {
        var options = new CacheEntryOptions[count];
        var names = new string[count];
        for (var i = 0; i < count; i++)
        {
            options[i] = _entryBuilder.BuildEntryOptions<T>(keys[i], default, token);
            names[i] = options[i].CacheKey.Name;
        }

        List<CacheKey>? failed = null;
        Issued removed;
        // In the keys' order first, then cleared locally; released before the events.
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

        for (var i = 0; i < count; i++)
        {
            if (!removed.Results[i] || (announced is not null && !announced[i]))
            {
                (failed ??= []).Add(keys[i]);
            }
        }

        return failed;
    }

    private async ValueTask<List<CacheKey>?> SetChunkAsync<T>(KeyValuePair<CacheKey, T?>[] chunk, int count, DateTimeOffset expiration, CachePolicy policy, bool announce, CancellationToken token)
    {
        var (keys, entries, entryCount, removals) = Partition(chunk, count, expiration, token);
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

        var (written, kept, toAnnounce, announceIndex, announceCount, cancelled) = await WriteChunkAsync(entries, entryCount, expiration, policy, announce, token).ConfigureAwait(false);
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

    /// <summary>A key named twice takes its last value; a null value not cached is a removal.</summary>
    private (CacheKey[] Keys, CacheEntryValue<T>[] Entries, int Count, List<CacheKey>? Removals) Partition<T>(KeyValuePair<CacheKey, T?>[] chunk, int count, DateTimeOffset expiration, CancellationToken token)
    {
        var last = LastOccurrences(chunk, count, static kv => kv.Key);
        List<CacheKey>? removals = null;
        var entries = new CacheEntryValue<T>[count];
        var keys = new CacheKey[count];
        var entryCount = 0;
        for (var i = 0; i < count; i++)
        {
            var kv = chunk[i];
            if (last is not null && last[kv.Key] != i)
            {
                continue;
            }

            if (kv.Value is null && !_multiLayerCacheOptions.CacheNullValues)
            {
                (removals ??= []).Add(kv.Key);
                continue;
            }

            keys[entryCount] = kv.Key;
            entries[entryCount++] = new CacheEntryValue<T>(_entryBuilder.BuildEntryOptions<T>(kv.Key, expiration, token), kv.Value);
        }

        return (keys, entries, entryCount, removals);
    }

    private void KeepDisconnected<T>(CacheKey[] keys, CacheEntryValue<T>[] entries, int entryCount, CachePolicy policy, ref List<CacheKey>? failed)
    {
        var max = policy.LocalExpirationDisconnected ?? _multiLayerCacheOptions.LocalMaxExpirationDisconnected;
        for (var i = 0; i < entryCount; i++)
        {
            LogSettingLocalOnly(Logged(entries[i].CacheEntry, typeof(T)));
            if (!ApplyLocal(entries[i].CacheEntry.CacheKey.Name, (Cache: this, Entry: entries[i], Max: max), static s => s.Cache.MemorySet(s.Entry.CacheEntry, s.Entry.Value, s.Max)))
            {
                (failed ??= []).Add(keys[i]);
            }
        }
    }

    /// <summary>The turn is released before the events.</summary>
    private async ValueTask<(bool[] Written, bool[] Kept, CacheEntryOptions[] ToAnnounce, int[] AnnounceIndex, int AnnounceCount, bool Cancelled)> WriteChunkAsync<T>(CacheEntryValue<T>[] entries, int entryCount, DateTimeOffset expiration, CachePolicy policy, bool announce, CancellationToken token)
    {
        var kept = new bool[entryCount];
        var toAnnounce = new CacheEntryOptions[entryCount];
        var announceIndex = new int[entryCount];
        var announceCount = 0;
        using var turn = EnterInnerWrite(entries.Take(entryCount).Select(e => e.CacheEntry.CacheKey.Name));
        if (turn.Tracked)
        {
            await Settled(turn.Previous, token).ConfigureAwait(false);
        }

        var issued = await IssueAsync(
            entries,
            entryCount,
            (Cache: this, Expiration: expiration, Policy: policy),
            static (s, e) => s.Cache._innerCache.SetAsync<T?>(e.CacheEntry.CacheKey, e.Value, s.Expiration, s.Policy, e.CacheEntry.Token),
            static (s, e, ex) => s.Cache.LogInnerCacheSetError(ex, s.Cache.Logged(e.CacheEntry, typeof(T))),
            token).ConfigureAwait(false);
        var written = issued.Results;

        for (var i = 0; i < issued.Count; i++)
        {
            if (!written[i])
            {
                // Cut short by the caller's cancel, the write may have landed: nothing stays local, and the event tells the others.
                if (issued.Cancelled)
                {
                    RemoveLocal(entries[i].CacheEntry.CacheKey.Name);
                    if (announce)
                    {
                        announceIndex[announceCount] = i;
                        toAnnounce[announceCount++] = entries[i].CacheEntry;
                    }
                }

                continue;
            }

            kept[i] = turn.Tracked
                ? KeepAfterInnerWrite(true, false, policy, (Cache: this, Options: entries[i].CacheEntry, entries[i].Value, Turn: turn), static (s, max) => s.Turn.KeepLocal(s.Options.CacheKey.Name, (s, max), static x => x.s.Cache.MemorySet(x.s.Options, x.s.Value, x.max)), token)
                : KeepAfterInnerWrite(true, false, policy, (Cache: this, Options: entries[i].CacheEntry, entries[i].Value), static (s, max) => s.Cache.MemorySet(s.Options, s.Value, max), token);
            if (announce)
            {
                announceIndex[announceCount] = i;
                toAnnounce[announceCount++] = entries[i].CacheEntry;
            }
        }

        return (written, kept, toAnnounce, announceIndex, announceCount, issued.Cancelled);
    }
}
