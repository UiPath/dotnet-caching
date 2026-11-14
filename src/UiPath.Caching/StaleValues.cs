using System.Collections.Concurrent;

namespace UiPath.Caching;

/// <summary>
/// What fail-safe falls back to: per key, the entry last kept locally under a fail-safe policy, until its local expiry plus the
/// policy's maximum stale age. Never read as a hit. Any removal or invalidation of the key drops it, a peer's one included, which
/// is heard before the key's own token is told. The values live in the local tier's own cache, under keys no cache key can equal:
/// at no size while the local entry holds the same payload, and from that entry's expiry at its size and at low priority, so the
/// tier's one size limit covers both and its compaction takes kept values before live entries.
/// </summary>
internal sealed partial class StaleValues : IDisposable
{
    internal const int CommitLocks = 1024;

    internal static readonly TimeSpan DefaultMaxStaleAge = TimeSpan.FromDays(1);
    internal static readonly TimeSpan DefaultCooldown = TimeSpan.FromSeconds(30);

    /// <summary>How long a topic that could not be watched keeps no values before a write tries again.</summary>
    internal static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

    /// <summary>Told when a local entry leaves a tier with a size limit, for the entries written under fail-safe there; its state is the store.</summary>
    internal static readonly PostEvictionDelegate EntryLeft = static (key, value, reason, state) => ((StaleValues)state!).Left((string)key, (ICacheEntry)value!, reason);

    private readonly IMemoryCache _local;
    private readonly ITopicProvider _topics;
    private readonly string? _source;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<TopicKey, Watcher> _watched = new();
    private readonly Lock _gate = new();

    // Every change to a key's kept entry takes the key's lock: a drop meant for one value never takes a value kept since, and the charge of a
    // value whose local entry has expired never lands after a removal or a write of the key. By key, so writes of other keys do not wait.
    // Innermost: only the tier's cache runs under one, so nothing takes two, and none is held when a key's local turn is asked for.
    private readonly Lock[] _commits = [.. Enumerable.Range(0, CommitLocks).Select(static _ => new Lock())];
    private long _epoch;
    private bool _disposed;

    /// <param name="local">The local tier's cache, which holds the kept values too.</param>
    public StaleValues(ITopicProvider topics, Uri? source, TimeProvider clock, IMemoryCacheOptions memoryOptions, IMemoryCache local, ILogger logger)
    {
        _topics = topics;
        _source = Text(source);
        _clock = clock;
        _local = local;
        _logger = logger;
        Limited = memoryOptions.SizeLimit > 0;
    }

    /// <summary>Whether the local tier has a size limit: a kept value then takes its entry's size once that entry has expired.</summary>
    public bool Limited { get; }

    /// <summary>Moves each time everything kept is dropped at once: lost events, a topic that ended or was invalidated. A value kept under an earlier one is not served.</summary>
    private long Epoch => Volatile.Read(ref _epoch);

    /// <summary>The epoch a write reads before it commits its value and hands to <see cref="Keep"/>, with the topic watched first: a gap during the commit then moves it.</summary>
    public long EpochFor(TopicKey topic)
    {
        Watching(topic);
        return Epoch;
    }

    /// <summary>Keeps <paramref name="entry"/> for the key until <paramref name="localDeadline"/> plus <paramref name="maxStaleAge"/>; null, and the key's value dropped, when its topic is not watched, that is already past, or <paramref name="epoch"/> is no longer the store's.</summary>
    /// <param name="size">The size the local tier charged the entry, which the value takes over when that entry expires: a size asked for again then could differ, or fail.</param>
    /// <param name="epoch">What <see cref="EpochFor"/> returned before the value was committed locally: everything kept was dropped since when it moved, and this value is from before that.</param>
    public StaleValue? Keep(string name, TopicKey topic, ICacheEntry entry, DateTimeOffset localDeadline, long size, TimeSpan maxStaleAge, long epoch)
    {
        var until = maxStaleAge.Ticks > DateTimeOffset.MaxValue.UtcTicks - localDeadline.UtcTicks ? DateTimeOffset.MaxValue : localDeadline + maxStaleAge;
        if (!Watching(topic) || until <= _clock.GetUtcNow() || epoch != Epoch)
        {
            Drop(name);
            return null;
        }

        // The epoch goes with the value: one that moves after this is seen by every read of it.
        var value = new StaleValue(entry, size, localDeadline, until, epoch);
        lock (Commit(name))
        {
            // The local entry holds the same payload, so the value takes no size yet; a compaction leaves it alone, since it would free nothing.
            Store(new KeptKey(name), value, 0, CacheItemPriority.NeverRemove);
        }

        return value;
    }

    /// <summary>The value kept for the key, while neither the stale age it was written under nor the caller's <paramref name="maxStaleAge"/> has passed.</summary>
    public bool TryGet<T>(string name, TimeSpan maxStaleAge, [NotNullWhen(true)] out StaleValue? value, [NotNullWhen(true)] out ICacheEntry<T?>? entry)
    {
        if (Find(name) is { Entry: ICacheEntry<T?> typed } kept && kept.Within(_clock.GetUtcNow(), maxStaleAge))
        {
            value = kept;
            entry = typed;
            return true;
        }

        value = null;
        entry = null;
        return false;
    }

    /// <summary>The value kept for the key, of any type and whatever its age.</summary>
    public StaleValue? Find(string name)
    {
        var key = new KeptKey(name);
        var kept = Held(key);
        if (kept is null)
        {
            // While a value is stored again, as it is at its size when its entry expires, the tier's scan for expired entries may take out the one
            // it replaces, and the key reads as absent until the store ends: looked for once more with no store of the key under way.
            lock (Commit(name))
            {
                kept = Held(key);
            }
        }

        if (kept is null)
        {
            return null;
        }

        if (kept.Epoch == Epoch)
        {
            return kept;
        }

        // Kept before everything was dropped at once: not served, and gone at the first look.
        Drop(name, kept);
        return null;
    }

    /// <summary>Whether <paramref name="value"/> is still the one kept for the key: nothing removed, invalidated or replaced it since.</summary>
    public bool IsCurrent(string name, StaleValue value) => ReferenceEquals(Find(name), value);

    public void Drop(string name)
    {
        lock (Commit(name))
        {
            _local.Remove(new KeptKey(name));
        }
    }

    /// <summary>Drops the key's value only while it is <paramref name="value"/>, so a value kept since stays.</summary>
    public void Drop(string name, StaleValue value)
    {
        var key = new KeptKey(name);
        lock (Commit(name))
        {
            if (ReferenceEquals(Held(key), value))
            {
                _local.Remove(key);
            }
        }
    }

    /// <summary>Drops the key's value only while it holds <paramref name="entry"/>: that entry was invalidated.</summary>
    public void Drop(string name, ICacheEntry entry)
    {
        var key = new KeptKey(name);
        lock (Commit(name))
        {
            if (ReferenceEquals(Held(key)?.Entry, entry))
            {
                _local.Remove(key);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }

        foreach (var watcher in _watched.Values)
        {
            watcher.End();
        }

        _watched.Clear();
    }

    private static string? Text(Uri? uri) => uri switch
    {
        null => null,
        { IsAbsoluteUri: true } => uri.AbsoluteUri,
        _ => uri.OriginalString,
    };

    private Lock Commit(string name) => _commits[(uint)StringComparer.Ordinal.GetHashCode(name) % CommitLocks];

    /// <summary>What the tier holds for the key, whatever its epoch.</summary>
    private StaleValue? Held(KeptKey key) => _local.TryGetValue(key, out var held) ? held as StaleValue : null;

    // Under the key's lock.
    private void Store(KeptKey key, StaleValue value, long size, CacheItemPriority priority)
    {
        try
        {
            using var cached = _local.CreateEntry(key);
            cached.AbsoluteExpiration = value.Until;
            cached.Size = size;
            cached.Priority = priority;
            cached.Value = value;
        }
        catch
        {
            // The tier still holds what the key had: an older write's value, or this one at no size with its entry gone, which nothing would count or take out.
            _local.Remove(key);
            throw;
        }
    }

    private void Left(string name, ICacheEntry entry, EvictionReason reason)
    {
        // The write that replaced the entry has kept its own value, or dropped this one.
        if (reason == EvictionReason.Replaced)
        {
            return;
        }

        try
        {
            if (reason == EvictionReason.Expired)
            {
                Charge(name, entry);
            }
            else
            {
                // Evicted for room, removed or invalidated: nothing would give its kept value a size later, so it does not outlive the entry.
                Drop(name, entry);
            }
        }
        catch (ObjectDisposedException)
        {
            // The tier was disposed before it told that the entry left.
        }
    }

    /// <summary>
    /// The local entry has expired, so its kept value holds the payload alone from here: the value is stored again at the entry's size and at
    /// low priority. A tier with no room for it refuses it, and takes out the one stored before: the value is then not kept.
    /// </summary>
    private void Charge(string name, ICacheEntry entry)
    {
        var key = new KeptKey(name);
        lock (Commit(name))
        {
            // Only the value kept for this entry, and only while it stands: a removal, a write or lost events since the entry expired win.
            if (Held(key) is { } kept && ReferenceEquals(kept.Entry, entry))
            {
                if (kept.Epoch == Epoch)
                {
                    Store(key, kept, kept.Size, CacheItemPriority.Low);
                }
                else
                {
                    _local.Remove(key);
                }
            }
        }
    }

    /// <summary>Hears the topic's events from now on, so a peer's change to a key kept here drops its value; false while it cannot.</summary>
    private bool Watching(TopicKey topic)
    {
        if (_watched.TryGetValue(topic, out var watcher) && !watcher.Failed)
        {
            return true;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            if (_watched.TryGetValue(topic, out watcher) && (!watcher.Failed || _clock.GetElapsedTime(watcher.FailedAt) < RetryDelay))
            {
                return !watcher.Failed;
            }

            var next = new Watcher(this, topic);
            // Before the subscription: a topic that completes while it subscribes removes this one.
            _watched[topic] = next;
            try
            {
                next.Attach(_topics.Create(topic).Subscribe(next));

                // A topic that completed as it subscribed removed the watcher: nothing is heard.
                return _watched.TryGetValue(topic, out var current) && ReferenceEquals(current, next);
            }
            catch (Exception ex)
            {
                next.Fail(_clock.GetTimestamp());
                LogWatchFailed(ex, topic);
                return false;
            }
        }
    }

    private void Heard(ICacheEvent value)
    {
        if (string.Equals(_source, Text(value.Source), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (string.Equals(value.Type, KnownEventTypes.TopicInvalidated, StringComparison.OrdinalIgnoreCase))
        {
            Invalidate();
        }
        else if (value.Key is { } key)
        {
            Drop(key);
        }
    }

    /// <summary>Any events missed, a subscription gap included: the local tier may keep its entries over a gap until they expire, but a kept value outlives that by its stale age.</summary>
    private void Lost() => Invalidate();

    // The kept values share the tier's cache with its entries, so they are not cleared: one kept under an earlier epoch is never served, and
    // goes when its key is next looked at or its entry leaves the tier, and otherwise as any kept value does.
    private void Invalidate() => Interlocked.Increment(ref _epoch);

    /// <summary>The topic ended: what a peer changes is no longer heard, so nothing kept can be trusted.</summary>
    private void Ended(Watcher watcher)
    {
        _watched.TryRemove(new KeyValuePair<TopicKey, Watcher>(watcher.Topic, watcher));
        watcher.End();
        Invalidate();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not watch topic {Topic} for peers' changes; fail-safe keeps no values of it until a write after the retry delay.")]
    private partial void LogWatchFailed(Exception ex, TopicKey topic);

    /// <summary>The key of a kept value in the local tier's cache, which a cache key, a string, never equals.</summary>
    private sealed record KeptKey(string Name);

    /// <summary>The store's subscription to one topic, told of an event before the keyed observers of its key.</summary>
    private sealed class Watcher(StaleValues owner, TopicKey topic) : IFirstObserver<ICacheEvent>, IMissedEventsObserver
    {
        private IDisposable? _subscription;
        private int _ended;
        private long _failedAt;
        private volatile bool _failed;

        public TopicKey Topic => topic;

        public bool Failed => _failed;

        public long FailedAt => Volatile.Read(ref _failedAt);

        public void Fail(long at)
        {
            Volatile.Write(ref _failedAt, at);
            _failed = true;
        }

        public void Attach(IDisposable subscription)
        {
            Volatile.Write(ref _subscription, subscription);
            if (Volatile.Read(ref _ended) != 0)
            {
                Interlocked.Exchange(ref _subscription, null)?.Dispose();
            }
        }

        public void End()
        {
            Interlocked.Exchange(ref _ended, 1);
            Interlocked.Exchange(ref _subscription, null)?.Dispose();
        }

        public void OnNext(ICacheEvent value) => owner.Heard(value);

        public void OnEventsMissed(MissedEventsReason reason) => owner.Lost();

        public void OnError(Exception error) => owner.Ended(this);

        public void OnCompleted() => owner.Ended(this);
    }
}
