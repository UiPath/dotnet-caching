using System.Collections.Concurrent;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching;

public abstract partial class MultilayerCacheBase
{
    /// <summary>Refreshes held entries on a peer's write; null when no shared tier or no broadcast tells this node of one.</summary>
    private protected readonly PeerRefresher? _peerRefresher;

    /// <summary>Tests hold the refresher before each drain.</summary>
    internal Task? PeerRefreshHold
    {
        get => _peerRefresher?.Hold;
        set
        {
            if (_peerRefresher is { } refresher)
            {
                refresher.Hold = value;
            }
        }
    }

    /// <summary>Tests lower the refresher's queue cap.</summary>
    internal int PeerRefreshMaxQueued
    {
        get => _peerRefresher?.MaxQueued ?? 0;
        set
        {
            if (_peerRefresher is { } refresher)
            {
                refresher.MaxQueued = value;
            }
        }
    }

    /// <summary>How an entry kept under <paramref name="policy"/> is refreshed on a peer's write; null, and dropped then, when nothing refreshes it, no policy is in scope, or the policy opts out.</summary>
    private protected PeerRefreshTarget? RefreshTarget(IPeerRefreshReader reader, CachePolicy? policy) =>
        _peerRefresher is null || policy is null || policy.RefreshLocalOnPeerWrite == false ? null : new PeerRefreshTarget(reader, policy);

    /// <summary>Removes the key locally, only while it still holds <paramref name="held"/>.</summary>
    private protected void DropLocal(string name, object held) =>
        ApplyLocal(name, (Cache: this, Name: name, Held: held), static s =>
        {
            if (!s.Cache.IsLocalEntry(s.Name, s.Held))
            {
                return false;
            }

            s.Cache._memoryCache.Remove(s.Name);
            return true;
        });

    /// <summary>Removes the key while it holds <paramref name="held"/>, outside the stripe lock, as a token change expires an entry: safe under a token's notify lock. A write that replaces it meanwhile may be removed too, which costs a miss.</summary>
    private protected void EvictLocal(string name, object held)
    {
        if (IsLocalEntry(name, held))
        {
            _memoryCache.Remove(name);
        }
    }

    /// <summary>Reads the keys peers wrote again, off the dispatcher thread and in batches, and keeps what it read in place of the held entries.</summary>
    private protected sealed partial class PeerRefresher(MultilayerCacheBase cache) : IDisposable
    {
        private const string EventStarted = "cache.peer_refresh.started";
        private const string EventKept = "cache.peer_refresh.kept";
        private const string EventDiscarded = "cache.peer_refresh.discarded";
        private const string EventDropped = "cache.peer_refresh.dropped";
        private const string MetricReadMs = "cache.peer_refresh.read_ms";
        private const string TagCache = "cache.name";
        private const string TagReason = "reason";
        private const string MetricKeys = "keys";
        private const string ReasonNewerLocal = "newer_local";
        private const string ReasonNotFound = "not_found";
        private const string ReasonFailed = "failed";
        private const string ReasonTimedOut = "timed_out";
        private const string ReasonDeclined = "declined";
        private const string ReasonOverflow = "overflow";
        private const string ReasonStillDirty = "still_dirty";
        private const string ReasonExpired = "expired";

        private readonly ILogger _logger = cache._logger;
        // Taken to add a key and to dispose; a key's own transitions lock its item.
        private readonly Lock _gate = new();
        private readonly ConcurrentDictionary<string, Item> _items = new(StringComparer.Ordinal);
        private readonly ConcurrentQueue<Item> _order = new();
        // The entry a refresh kept, by the entry it replaced: an event that reaches only the replaced entry's token drops it.
        private readonly ConditionalWeakTable<object, object> _successors = [];
        private readonly SemaphoreSlim _signal = new(0);
        private readonly CancellationTokenSource _stop = new();
        private Task? _loop;
        private bool _disposed;
        private int _pending;

        private enum Phase
        {
            Queued,
            Reading,
        }

        internal int MaxQueued { get; set; } = 10_000;

        /// <summary>Queued and reading keys.</summary>
        internal int Pending => Volatile.Read(ref _pending);

        /// <summary>Test seam: awaited before each drain.</summary>
        internal Task? Hold { get; set; }

        /// <summary>Queues the held entry's key for a refresh; false, and the entry is dropped, when it cannot be refreshed. Runs on the dispatcher thread, so it never blocks on a read.</summary>
        public bool TryEnqueue(RefreshMetadataState held)
        {
            if (Volatile.Read(ref _disposed) || !cache._connectionState.IsConnected)
            {
                return false;
            }

            // Before the key is queued: a mark taken while counting is off would keep every refresh out.
            cache._background.Track();

            var name = held.CacheKey.Name;
            while (true)
            {
                var queued = _items.TryGetValue(name, out var item) ? TryCoalesce(item, held) : TryAdd(name, held);
                if (queued is { } result)
                {
                    return result;
                }
            }
        }

        /// <summary>A held entry's token changed: a removal, loss or write that reached only the token of an entry a refresh replaced drops what the refresh kept. Runs on the dispatcher thread.</summary>
        public void TokenChanged(RefreshMetadataState held)
        {
            if (Volatile.Read(ref _disposed))
            {
                return;
            }

            // Runs under the token's notify lock, which a keep committing an entry can wait for while it holds the item and stripe locks: takes neither.
            // Pairs with the keep: either this sees the successor, or the keep sees the token changed.
            Interlocked.MemoryBarrier();
            if (_successors.TryGetValue(held.CacheEntity, out var next))
            {
                try
                {
                    cache.EvictLocal(held.CacheKey.Name, next);
                }
                catch (ObjectDisposedException) when (Volatile.Read(ref _disposed))
                {
                    // The cache was disposed meanwhile.
                }
            }
        }

        public void Dispose()
        {
            Task? loop;
            lock (_gate)
            {
                if (Volatile.Read(ref _disposed))
                {
                    return;
                }

                Volatile.Write(ref _disposed, true);
                loop = _loop;
            }

            foreach (var item in _items.Values)
            {
                lock (item)
                {
                    Remove(item);
                }
            }

            _order.Clear();
            _stop.Cancel();
            if (loop is null)
            {
                Release();
            }
        }

        /// <summary>Folds the write into the key's queued or reading refresh; null when the item ended meanwhile.</summary>
        private bool? TryCoalesce(Item item, RefreshMetadataState held)
        {
            lock (item)
            {
                if (item.Removed)
                {
                    return null;
                }

                // The read may have begun before this write: it reads once more.
                if (item.Phase == Phase.Reading)
                {
                    item.Dirty = true;
                }

                // A concurrent loss or a local write may have replaced the entry the event hit: nothing is kept for it.
                if (!cache.IsLocalEntry(item.Name, held.CacheEntity))
                {
                    return false;
                }

                item.State = held;
                return true;
            }
        }

        /// <summary>Queues the key; null when another write queued it meanwhile.</summary>
        private bool? TryAdd(string name, RefreshMetadataState held)
        {
            lock (_gate)
            {
                // Under the lock that disposes: the local tier is disposed only after this refresher.
                if (Volatile.Read(ref _disposed))
                {
                    return false;
                }

                if (_items.ContainsKey(name))
                {
                    return null;
                }

                if (!cache.IsLocalEntry(name, held.CacheEntity))
                {
                    // A write a refresh's read may predate, heard only by the token of the entry it replaced.
                    if (_successors.TryGetValue(held.CacheEntity, out var next))
                    {
                        cache.DropLocal(name, next);
                    }

                    return false;
                }

                if (Volatile.Read(ref _pending) < MaxQueued)
                {
                    var item = new Item(name, held, cache._clock.GetTimestamp());
                    _items[name] = item;
                    Interlocked.Increment(ref _pending);
                    _order.Enqueue(item);
                    _loop ??= Task.Run(RunAsync, CancellationToken.None);
                    // Under the lock, so it cannot follow the loop disposing the signal.
                    _signal.Release();
                    return true;
                }
            }

            Dropped(held, ReasonOverflow);
            return false;
        }

        private async Task RunAsync()
        {
            var stop = _stop.Token;
            try
            {
                while (true)
                {
                    await _signal.WaitAsync(stop).ConfigureAwait(false);
                    while (!_order.IsEmpty)
                    {
                        if (Hold is { } hold)
                        {
                            await hold.WaitAsync(stop).ConfigureAwait(false);
                        }

                        await DrainAsync(stop).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                // Disposed.
            }
            finally
            {
                Release();
            }
        }

        /// <summary>Refreshes one batch. A failure past the per-key handling drops the batch's keys and leaves the loop running: a stopped loop would leave every later refresh queued for good.</summary>
        private async Task DrainAsync(CancellationToken stop)
        {
            var batch = new List<Taken>();
            try
            {
                Take(batch);
                if (batch.Count > 0)
                {
                    await RefreshAsync(batch, stop).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (!stop.IsCancellationRequested)
            {
                foreach (var taken in batch)
                {
                    Abandon(taken.Item, ex);
                }
            }
        }

        /// <summary>Takes up to a batch of queued keys into <paramref name="batch"/> for a read; a key that waited past the timeout drops instead.</summary>
        private void Take(List<Taken> batch)
        {
            var timeout = BoundedTimeout(cache._multiLayerCacheOptions.Timeout);
            while (batch.Count < cache.BatchChunkSize && _order.TryDequeue(out var item))
            {
                lock (item)
                {
                    if (item.Removed)
                    {
                        continue;
                    }

                    try
                    {
                        if (cache._clock.GetElapsedTime(item.EnqueuedAt) > timeout)
                        {
                            End(item, ReasonExpired);
                            continue;
                        }

                        item.Phase = Phase.Reading;
                        item.Dirty = false;
                        batch.Add(new Taken(item, item.State, cache._background.Mark(item.Name)));
                    }
                    catch (Exception ex)
                    {
                        End(item, ReasonFailed, ex);
                    }
                }
            }
        }

        /// <summary>Reads the batch, one inner read per value type and policy, and settles each key on what its read found.</summary>
        private async Task RefreshAsync(List<Taken> batch, CancellationToken stop)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
            timeout.CancelAfter(BoundedTimeout(cache._multiLayerCacheOptions.Timeout));
            var reads = batch
                .GroupBy(static t => t.Held.Refresh!.Value)
                .Select(g => g.ToArray())
                .Select(taken => (Taken: taken, Read: ReadAsync(taken[0].Held.Refresh!.Value, [.. taken.Select(static t => t.Held.CacheKey)], timeout, stop)))
                .ToArray();
            foreach (var (taken, read) in reads)
            {
                var (found, failure, error) = await read.ConfigureAwait(false);
                if (stop.IsCancellationRequested)
                {
                    return;
                }

                for (var i = 0; i < taken.Length; i++)
                {
                    Settle(taken[i], found?[i], failure, error);
                }
            }
        }

        /// <summary>One read from the master, within the cache timeout; on failure the reason the keys drop, none when the refresher stopped.</summary>
        private async Task<(PeerRead[]? Found, string? Failure, Exception? Error)> ReadAsync(PeerRefreshTarget target, CacheKey[] keys, CancellationTokenSource timeout, CancellationToken stop)
        {
            TrackStarted(keys.Length);
            var started = cache._clock.GetTimestamp();
            try
            {
                var reading = ReadOnMasterAsync(target, keys, timeout.Token);
                try
                {
                    // The inner tier may not cancel a command in flight; the wait is bounded, not only the read.
                    var found = await reading.WaitAsync(timeout.Token).ConfigureAwait(false);
                    return found.Length == keys.Length ? (found, null, null) : (null, ReasonFailed, new InvalidOperationException($"The inner tier returned {found.Length} entries for {keys.Length} keys."));
                }
                catch (OperationCanceledException)
                {
                    reading.Forget();
                    throw;
                }
            }
            catch (Exception) when (stop.IsCancellationRequested)
            {
                return (null, null, null);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                return (null, ReasonTimedOut, null);
            }
            catch (Exception ex)
            {
                return (null, ReasonFailed, ex);
            }
            finally
            {
                TrackReadMs(cache._clock.GetElapsedTime(started).TotalMilliseconds);
            }
        }

        /// <summary>Holds the master scope until the read ends, abandoned or not: commands it sends after its first await demand the master too.</summary>
        private async Task<PeerRead[]> ReadOnMasterAsync(PeerRefreshTarget target, CacheKey[] keys, CancellationToken token)
        {
            using (cache._innerTier.MasterReads?.DemandMaster())
            {
                return await target.Reader.ReadAsync(cache, keys, target.Policy, token).ConfigureAwait(false);
            }
        }

        /// <summary>Keeps, re-reads or drops the key. Under the item's lock, so a peer write that marks it lands before the keep, and is read again, or after, on the replaced entry's token, where its successor drops.</summary>
        private void Settle(Taken taken, PeerRead? read, string? failure, Exception? error)
        {
            var item = taken.Item;
            lock (item)
            {
                if (item.Removed)
                {
                    return;
                }

                try
                {
                    if (failure is not null)
                    {
                        End(item, failure, error);
                    }
                    else if (item.Dirty)
                    {
                        // One more read, unless the key was read again already.
                        if (!item.Requeued)
                        {
                            Requeue(item);
                        }
                        else
                        {
                            End(item, ReasonStillDirty);
                        }
                    }
                    else if (read is not { Found: true } found)
                    {
                        End(item, ReasonNotFound);
                    }
                    else
                    {
                        Keep(item, found, taken.Mark);
                    }
                }
                catch (Exception ex)
                {
                    End(item, ReasonFailed, ex);
                }
            }
        }

        private void Keep(Item item, PeerRead read, long mark)
        {
            var held = item.State;
            switch (held.Refresh!.Value.Reader.Keep(cache, held, read, mark, out var installed))
            {
                case PeerKeep.Kept:
                    // Before the item ends: a write that finds no item must find the successor.
                    if (installed is not null)
                    {
                        _successors.AddOrUpdate(held.CacheEntity, installed);
                        Interlocked.MemoryBarrier();
                        // The replaced entry's token changed before the successor was recorded, so its change found none.
                        if (held.Token.HasChanged)
                        {
                            cache.DropLocal(item.Name, installed);
                        }
                    }

                    Remove(item);
                    cache.Telemetry.TryTrackEvent(EventKept, [new(TagCache, cache.Name)]);
                    break;
                case PeerKeep.Replaced:
                    Remove(item);
                    cache.Telemetry.TryTrackEvent(EventDiscarded, [new(TagCache, cache.Name), new(TagReason, ReasonNewerLocal)]);
                    LogDiscarded(Logged(held));
                    break;
                case PeerKeep.Declined when !item.Requeued:
                    // A write of a key in the same stripe, or of this key still in flight: read once more.
                    Requeue(item);
                    break;
                case PeerKeep.Declined:
                    End(item, ReasonDeclined);
                    break;
                default:
                    End(item, ReasonFailed);
                    break;
            }
        }

        /// <summary>Under the item's lock.</summary>
        private void Requeue(Item item)
        {
            item.Requeued = true;
            item.Phase = Phase.Queued;
            item.EnqueuedAt = cache._clock.GetTimestamp();
            _order.Enqueue(item);
        }

        /// <summary>Ends a key a failed drain left unsettled; the key is dropped if it can be, and ends either way.</summary>
        private void Abandon(Item item, Exception failure)
        {
            lock (item)
            {
                if (item.Removed)
                {
                    return;
                }

                try
                {
                    End(item, ReasonFailed, failure);
                }
                catch (Exception ex)
                {
                    // End let the key go before it threw.
                    cache.Telemetry.TryTrackException(ex);
                }
            }
        }

        /// <summary>Drops the held entry and ends the key's refresh. Under the item's lock.</summary>
        private void End(Item item, string reason, Exception? failure = null)
        {
            try
            {
                cache.DropLocal(item.Name, item.State.CacheEntity);
            }
            finally
            {
                Remove(item);
            }

            Dropped(item.State, reason, failure);
        }

        /// <summary>Ends the key's refresh. Under the item's lock.</summary>
        private void Remove(Item item)
        {
            if (item.Removed)
            {
                return;
            }

            item.Removed = true;
            _items.TryRemove(new KeyValuePair<string, Item>(item.Name, item));
            Interlocked.Decrement(ref _pending);
        }

        private void Dropped(RefreshMetadataState held, string reason, Exception? failure = null)
        {
            cache.Telemetry.TryTrackEvent(EventDropped, [new(TagCache, cache.Name), new(TagReason, reason)]);
            LogDropped(failure, Logged(held), reason);
        }

        private void TrackStarted(int keys)
        {
            try
            {
                cache.Telemetry.TrackEvent(EventStarted, [new(TagCache, cache.Name)], [new(MetricKeys, keys)]);
            }
            catch (Exception ex)
            {
                cache.Telemetry.TryTrackException(ex);
            }
        }

        private void TrackReadMs(double milliseconds)
        {
            try
            {
                cache.Telemetry.TrackMetric(MetricReadMs, milliseconds, [new(TagCache, cache.Name)]);
            }
            catch (Exception ex)
            {
                cache.Telemetry.TryTrackException(ex);
            }
        }

        private void Release()
        {
            _stop.Dispose();
            _signal.Dispose();
        }

        private LoggedKey Logged(RefreshMetadataState held) => LoggedKey.For(cache._masker, held.CallerKey, held.CacheKey.Name, held.EntryType);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Refresh of {CacheKey} discarded: a newer local change won.")]
        private partial void LogDiscarded(LoggedKey cacheKey);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Refresh of {CacheKey} dropped the local copy: {Reason}.")]
        private partial void LogDropped(Exception? failure, LoggedKey cacheKey, string reason);

        /// <summary>A key taken into a read: the entry it read for, and the mark taken before.</summary>
        private readonly record struct Taken(Item Item, RefreshMetadataState Held, long Mark);

        /// <summary>One key's refresh, from its first queued write until it keeps, discards or drops; transitions lock it.</summary>
        private sealed class Item(string name, RefreshMetadataState state, long enqueuedAt)
        {
            public string Name { get; } = name;

            /// <summary>The held entry the latest write hit.</summary>
            public RefreshMetadataState State { get; set; } = state;

            public long EnqueuedAt { get; set; } = enqueuedAt;

            public Phase Phase { get; set; }

            /// <summary>A peer wrote the key again since its read began.</summary>
            public bool Dirty { get; set; }

            /// <summary>Read once more already, after a write during its read or a declined keep: a key is re-queued once, for either.</summary>
            public bool Requeued { get; set; }

            public bool Removed { get; set; }
        }
    }
}
