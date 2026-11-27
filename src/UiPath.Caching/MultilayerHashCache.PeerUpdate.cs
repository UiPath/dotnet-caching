using System.Collections.Immutable;

namespace UiPath.Caching;

internal sealed partial class MultilayerHashCache
{
    /// <summary>The held entry takes a write's fields only when it was exactly one write behind it: then the merge is the hash the write left, with this node's own later writes still over it.</summary>
    internal bool ApplyPeerUpdate<T>(RefreshMetadataState state, ICacheEntry<IDictionary<string, T?>> held, HashVersion version, PeerUpdate update)
    {
        if (_versioned is not { } versioned || !held.Found || held.Value is null || !version.TryResolve(out var number, out var ahead) || number <= 0)
        {
            return false;
        }

        // A write of the epoch the copy stands on, which the copy already has: an event can come late, behind a newer one or behind a read.
        if (update.Version <= number && HashVersion.SameEpoch(number, update.Version))
        {
            return true;
        }

        if (number != update.Version - 1 || HashVersion.ReturnedAtOrBefore(ahead, update.Version))
        {
            return false;
        }

        var now = _clock.GetUtcNow();
        var expiration = update.Expiration ?? held.Expiration;
        TimeSpan? remaining = null;
        if (expiration <= now || (version.LocalDeadline is { } deadline && (remaining = deadline - now) <= TimeSpan.Zero))
        {
            return false;
        }

        if (!versioned.TryDecodeFields<T>(update.Fields, state.Refresh?.Policy, out var written))
        {
            return false;
        }

        var changes = new List<KeyValuePair<string, T?>>(written.Count);
        foreach (var (field, value) in written)
        {
            if (!HashVersion.Writes(ahead, field))
            {
                changes.Add(new(field, value));
            }
        }

        var combined = held.Value.ToImmutableDictionary().SetItems(changes);

        var options = new InternalHashCacheEntryOptions
        {
            CacheKey = state.CacheKey,
            CallerKey = state.CallerKey,
            TopicKey = state.TopicKey,
            Expiration = expiration,
            Metadata = update.Metadata ?? held.Metadata,
        };
        var merged = CreateEntry(combined, options);
        HashVersions.Stamp<T>(merged, update.Version, version.LocalDeadline, ahead);
        Track(merged, held is not ILocalEntry { IsTracked: false });
        InheritRead(held, merged);
        return _localMemorySetter.Set(options, merged, typeof(T), remaining, state.Refresh);
    }

    private protected override void RemovingLocal(string name) => MarkDropped(name);

    private bool TryApplyPeerUpdate(RefreshMetadataState state, PeerUpdate update)
    {
        var name = state.CacheKey.Name;
        var handed = false;
        try
        {
            // The same lock a local merge takes, so the two take their turns over the held hash.
            lock (MergeLock(name))
            {
                if (_memoryCache.TryGetValue(name, out object? current) && current is not null && !_awaitingRefresh.TryGetValue(current, out _)
                    && HashVersions.TryGet(current, out var version) && version.Apply(this, state, current, update))
                {
                    if (!_awaitingRefresh.TryGetValue(current, out _))
                    {
                        return true;
                    }

                    // This node dropped the copy while the update was merged over it: the merge is marked too, and goes below.
                    MarkDropped(name);
                }
                else
                {
                    // Marked before the lock is let go, so a merge that takes it next does not lay its fields over the copy.
                    handed = state.Refresh is not null && MarkAwaitingRefresh(name, state.CacheEntity);
                }
            }
        }
        catch (Exception ex)
        {
            LogPeerUpdateFailed(ex, Logged(state.CallerKey, state.EntryType));
        }

        // The token's refresh hook takes the write next, and drops the entry if it cannot. The key still counts as changed, as a removal
        // would: a fill from a read begun before the write must not replace the held hash. Runs on the dispatcher, outside the token's notify lock.
        if (handed)
        {
            ApplyLocal(name, 0, static _ => true);
            return false;
        }

        // What is held may lack the write, whether or not it is the entry the event was for.
        RemoveLocal(name);
        return false;
    }

    /// <summary>Takes a peer's write to a held hash for a background refresh; false drops the entry. Runs on the dispatcher thread.</summary>
    private bool HandToRefresh(RefreshMetadataState held)
    {
        try
        {
            PeerRefreshHandOver?.Invoke();
            // Let go before the queue is touched: a keep holds the refresher's locks, then the stripe's, then this one.
            lock (MergeLock(held.CacheKey.Name))
            {
                MarkAwaitingRefresh(held.CacheKey.Name, held.CacheEntity);
            }

            return _peerRefresher!.TryEnqueue(held);
        }
        catch (Exception ex)
        {
            LogPeerUpdateFailed(ex, Logged(held.CallerKey, held.EntryType));
            return false;
        }
    }

    /// <summary>Under the key's merge lock; false when the entry is no longer held.</summary>
    private bool MarkAwaitingRefresh(string name, object held)
    {
        if (!IsLocalEntry(name, held))
        {
            return false;
        }

        _awaitingRefresh.AddOrUpdate(held, held);
        return true;
    }

    /// <summary>A copy a peer's write is refreshing lacks that write, so fields are not merged over it: it is dropped, as it was before refreshes, and the next read takes the hash whole.</summary>
    /// <remarks>Outside the merge lock: removing an entry disposes its change token.</remarks>
    private void DropAwaitingRefresh(string name)
    {
        if (_memoryCache.TryGetValue(name, out object? held) && held is not null && _awaitingRefresh.TryGetValue(held, out _))
        {
            EvictLocal(name, held);
        }
    }

    /// <summary>The copy a later background write built was made before this write landed, so it lacks its fields and its count.</summary>
    private bool DropSuperseded(string name)
    {
        if (_versioned is null)
        {
            return true;
        }

        // Not under the merge lock: removing an entry disposes its change token, which waits on the lock a delivery holds while it waits on this one.
        MarkDropped(name);
        _memoryCache.Remove(name);
        return true;
    }

    /// <summary>Marks the copy this node is about to drop, so a peer's update that read it before it went does not put it back with its fields merged in.</summary>
    /// <remarks>Without the merge lock, which that update holds: it checks the mark once it has written.</remarks>
    private void MarkDropped(string name)
    {
        if (_versioned is null)
        {
            return;
        }

        // Read again after marking: an update that wrote before it saw the mark left its merge here.
        while (_memoryCache.TryGetValue(name, out object? held) && held is not null && !_awaitingRefresh.TryGetValue(held, out _))
        {
            _awaitingRefresh.AddOrUpdate(held, held);
        }
    }

    private Lock MergeLock(string name) => _mergeLocks[(int)((uint)StringComparer.Ordinal.GetHashCode(name) % (uint)_mergeLocks.Length)];

    /// <summary>False when the local tier holds no hash to merge over, or holds one newer than the write.</summary>
    private bool TryMergeLocal<T>(ref InternalHashCacheEntryOptions options, IDictionary<string, T?> fields, [MaybeNullWhen(false)] out IDictionary<string, T?> merged)
    {
        if (!_memoryCache.TryGetValue(options.CacheKey.Name, out ICacheEntry<IDictionary<string, T?>>? held) || held is null || !held.Found || held.Value is null
            || _awaitingRefresh.TryGetValue(held, out _))
        {
            merged = null;
            options.Version = 0;
            options.Own = null;
            return false;
        }

        // The merge stands on what the held hash stands on, with the write laid over it; a hash that stands on nothing known leaves the entry without a version.
        var write = options.Pending ?? (options.Version > 0 ? new OwnWrite([.. fields.Keys]) { Version = options.Version } : null);
        options.Pending = null;
        options.Version = 0;
        options.Own = null;
        if (_versioned is not null && write is not null && HashVersions.TryGet(held, out var stamp) && stamp.TryResolve(out var number, out var ahead))
        {
            // The held hash already stands on this write or a later one: its fields are newer than the write's.
            if (write.Version > 0 && write.Version <= number && HashVersion.SameEpoch(number, write.Version))
            {
                merged = null;
                return false;
            }

            if (ahead.Length < MaxOwnWritesAhead)
            {
                options.Version = number;
                options.Own = [.. ahead, write];
            }
        }

        merged = held.Value.ToImmutableDictionary().SetItems(fields);
        options.Metadata ??= held.Metadata;
        return true;
    }

    /// <summary>Reads and keeps the refreshed hashes of one value type, whole.</summary>
    private sealed class RefreshReader<T> : IPeerRefreshReader
    {
        public static readonly RefreshReader<T> Instance = new();

        public async ValueTask<PeerRead[]> ReadAsync(MultilayerCacheBase cache, CacheKey[] keys, CachePolicy policy, CancellationToken token)
        {
            var inner = ((MultilayerHashCache)cache)._innerCache;
            if (keys.Length == 1)
            {
                var entry = await inner.GetCacheEntryAsync<T>(keys[0], policy, token).ConfigureAwait(false);
                return [new PeerRead(entry.Found, entry)];
            }

            // A hash cache has no many-key entry read: each key is read at once, all started in the caller's master scope.
            var reads = new Task<ICacheEntry<IDictionary<string, T?>>>[keys.Length];
            for (var i = 0; i < keys.Length; i++)
            {
                reads[i] = inner.GetCacheEntryAsync<T>(keys[i], policy, token).AsTask();
            }

            var entries = await Task.WhenAll(reads).ConfigureAwait(false);
            return Array.ConvertAll(entries, static e => new PeerRead(e.Found, e));
        }

        public PeerKeep Keep(MultilayerCacheBase cache, RefreshMetadataState held, PeerRead read, long mark, out object? installed)
        {
            var owner = (MultilayerHashCache)cache;
            var entry = (ICacheEntry<IDictionary<string, T?>>)read.Entry!;
            var options = new InternalHashCacheEntryOptions
            {
                CacheKey = held.CacheKey,
                CallerKey = held.CallerKey,
                TopicKey = held.TopicKey,
            };
            new HashShape<T>(owner).Adopt(ref options, entry);
            var name = held.CacheKey.Name;
            var policy = held.Refresh!.Value.Policy;
            ICacheEntry<IDictionary<string, T?>>? kept = null;
            var failed = false;
            // Only over the entry the event hit: a write, removal or loss since then replaced or dropped it.
            var filled = owner._background.Fill(name, mark, owner, o =>
            {
                if (!o.IsLocalEntry(name, held.CacheEntity))
                {
                    return false;
                }

                var item = o.CreateEntry(entry.Value ?? Empty<T>(), options);
                InheritRead(held.CacheEntity, item);
                LinkRefresh(held.CacheEntity, item);
                if (!o.MemorySet(options, item, o.LocalLifetime(policy), policy, read: false))
                {
                    failed = true;
                    return false;
                }

                kept = item;
                return true;
            });
            installed = kept;
            return filled switch
            {
                true => PeerKeep.Kept,
                false when failed => PeerKeep.Failed,
                false => PeerKeep.Replaced,
                null => owner.IsLocalEntry(name, held.CacheEntity) ? PeerKeep.Declined : PeerKeep.Replaced,
            };
        }
    }
}
