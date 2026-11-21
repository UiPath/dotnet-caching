namespace UiPath.Caching;

internal sealed partial class MultilayerCache
{
    private const int ChangeAttempts = 3;

    private enum LocalChangeState
    {
        Lost,
        Stored,
        Refused,
        Lapsed,
    }

    public ValueTask<FillResult<T>> TryFillAsync<T>(CacheKey cacheKey, T? value, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) =>
        TryFillCoreAsync(cacheKey, value, GetExpiration(expiration), policy ?? _defaultPolicy, token);

    public ValueTask<FillResult<T>> TryFillAsync<T>(CacheKey cacheKey, T? value, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) =>
        TryFillCoreAsync(cacheKey, value, GetExpiration(expiration), policy ?? _defaultPolicy, token);

    public ValueTask<ChangeResult<T>> SetIfChangedAsync<T>(CacheKey cacheKey, T? value, IEqualityComparer<T> comparer, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) =>
        SetIfChangedCoreAsync(cacheKey, value, comparer, GetExpiration(expiration), policy ?? _defaultPolicy, token);

    public ValueTask<ChangeResult<T>> SetIfChangedAsync<T>(CacheKey cacheKey, T? value, IEqualityComparer<T> comparer, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) =>
        SetIfChangedCoreAsync(cacheKey, value, comparer, GetExpiration(expiration), policy ?? _defaultPolicy, token);

    public ValueTask<SnapshotRead<T>> ReadSnapshotAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var options = _entryBuilder.BuildEntryOptions<T>(cacheKey, default, token);
        if (_sharedInner)
        {
            return _innerCache.AsMigrationCache().ReadSnapshotAsync<T>(options.CacheKey, policy, token);
        }

        return new(LocalSnapshot<T>(options.CacheKey.Name) is { } local
            ? new SnapshotRead<T>(SnapshotOutcome.Found, local)
            : new SnapshotRead<T>(SnapshotOutcome.Absent, null));
    }

    /// <summary>The entry an adoption read answers: a miss for anything but a found value, and a failure as an exception, as the throw-on-failure read did.</summary>
    internal static async ValueTask<ICacheEntry<T?>> AdoptionEntryAsync<T>(ValueTask<SnapshotRead<T>> read)
    {
        var snapshot = await read.ConfigureAwait(false);
        return snapshot.Outcome switch
        {
            SnapshotOutcome.Found when snapshot.Entry is { } entry => entry,
            SnapshotOutcome.Failed => throw new InvalidOperationException("The inner cache could not read the key"),
            _ => NullCache.NullCacheEntry<T?>.Instance,
        };
    }

    private async ValueTask<FillResult<T>> TryFillCoreAsync<T>(CacheKey cacheKey, T? value, DateTimeOffset expiration, CachePolicy policy, CancellationToken token)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        token.ThrowIfCancellationRequested();
        var options = _entryBuilder.BuildEntryOptions<T>(cacheKey, expiration, token);
        if (!CanFill(options, value, policy))
        {
            return default;
        }

        // Counting orders a fill against competing writes, so it is on before the first mark or local commit.
        _background.Track();
        if (!_sharedInner)
        {
            return FillLocally(options, value, policy);
        }

        if (GetInnerCacheDisconnected())
        {
            KeepUnfilled(options, null, value, policy);
            return default;
        }

        var name = options.CacheKey.Name;
        var mark = MarkInnerRead(name, options.TopicKey);
        FillResult<T> filled;
        try
        {
            filled = await _innerCache.TryFillAsync<T>(options.CacheKey, value, options.Expiration, policy, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && token.IsCancellationRequested))
        {
            LogInnerCacheFillError(ex, Logged(options, typeof(T)));
            filled = default;
        }

        switch (filled.Outcome)
        {
            case FillOutcome.Added:
                Layered<T>().FillFromInnerRead(options, mark, value, policy);
                break;
            case FillOutcome.Existed when filled.Expiration is { } deadline && deadline > _clock.GetUtcNow():
                Layered<T>().FillFromInnerRead(options with { Expiration = deadline }, mark, filled.Existing, policy);
                break;
            case FillOutcome.Existed:
            case FillOutcome.Unreadable:
                break;
            default:
                if (options.Expiration > _clock.GetUtcNow())
                {
                    KeepUnfilled(options, mark, value, policy);
                }

                break;
        }

        return filled with { Expiration = null };
    }

    private async ValueTask<ChangeResult<T>> SetIfChangedCoreAsync<T>(CacheKey cacheKey, T? value, IEqualityComparer<T> comparer, DateTimeOffset expiration, CachePolicy policy, CancellationToken token)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        ArgumentNullException.ThrowIfNull(comparer);
        token.ThrowIfCancellationRequested();
        var options = _entryBuilder.BuildEntryOptions<T>(cacheKey, expiration, token);
        if (!CanFill(options, value, policy))
        {
            return default;
        }

        _background.Track();
        _background.NoteChange(options.CacheKey.Name);
        if (!_sharedInner)
        {
            return await ChangeLocallyAsync(options, value, comparer, policy).ConfigureAwait(false);
        }

        if (GetInnerCacheDisconnected())
        {
            KeepUnfilled(options, null, value, policy);
            return default;
        }

        var name = options.CacheKey.Name;
        var mark = MarkInnerRead(name, options.TopicKey);
        ChangeResult<T> changed;
        var guarded = new ThrowRecordingComparer<T>(comparer);
        try
        {
            changed = await _innerCache.SetIfChangedAsync<T>(options.CacheKey, value, guarded, options.Expiration, policy, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (guarded.Thrown is null && !(ex is OperationCanceledException && token.IsCancellationRequested))
        {
            LogInnerCacheFillError(ex, Logged(options, typeof(T)));
            changed = default;
        }

        switch (changed.Outcome)
        {
            case ChangeOutcome.Filled:
                Layered<T>().FillFromInnerRead(options, mark, value, policy);
                break;
            case ChangeOutcome.Unchanged when changed.Expiration is { } deadline && deadline > _clock.GetUtcNow():
                Layered<T>().FillFromInnerRead(options with { Expiration = deadline }, mark, changed.Current, policy);
                break;
            case ChangeOutcome.Replaced:
                await KeepAndAnnounceAsync(options, mark, value, policy).ConfigureAwait(false);
                break;
            case ChangeOutcome.Unchanged:
                break;
            default:
                if (options.Expiration > _clock.GetUtcNow())
                {
                    KeepUnfilled(options, mark, value, policy);
                }

                break;
        }

        return changed with { Expiration = null };
    }

    private bool CanFill<T>(CacheEntryOptions options, T? value, CachePolicy policy)
    {
        if (value is null && !_multiLayerCacheOptions.CacheNullValues)
        {
            LogFillSkipped(Logged(options, typeof(T)), "a null value cannot be represented unless CacheNullValues is on");
            return false;
        }

        if (options.Expiration <= _clock.GetUtcNow())
        {
            LogFillSkipped(Logged(options, typeof(T)), "the requested expiration is not in the future, so the entry would retain nothing");
            return false;
        }

        if (!_sharedInner && (policy.LocalExpiration ?? _multiLayerCacheOptions.LocalMaxExpiration) is { } max && max <= TimeSpan.Zero)
        {
            LogFillSkipped(Logged(options, typeof(T)), "the local retention is not positive, and on this provider it is the only retention");
            return false;
        }

        return true;
    }

    private ICacheEntry<T?>? HeldLocally<T>(string name) =>
        _memoryCache.TryGetValue(name, out ICacheEntry<T?>? held) && held!.Found ? held : null;

    /// <summary>The held entry as of its local deadline, which is when this tier evicts it.</summary>
    private ICacheEntry<T?>? LocalSnapshot<T>(string name) =>
        HeldLocally<T>(name) is { } held
            ? held is ILocalEntry { LocalDeadline: var deadline } && deadline > DateTimeOffset.MinValue && deadline != held.Expiration
                ? (ICacheEntry<T?>)held.NewEntry(deadline, held.Metadata)
                : held
            : null;

    /// <summary>The options a local-only entry is written with; the expiration is the deadline the local tier gives it.</summary>
    private CacheEntryOptions EffectiveLocalOptions(CacheEntryOptions options, CachePolicy policy) =>
        options with { Expiration = _localMemorySetter.DeadlineFor(options.Expiration, ConnectedLifetime(policy)) };

    private FillResult<T> FillLocally<T>(CacheEntryOptions options, T? value, CachePolicy policy)
    {
        var name = options.CacheKey.Name;
        var local = EffectiveLocalOptions(options, policy);
        var added = _background.Exclusive(
            name,
            (Cache: this, Options: local, Value: value, Policy: policy),
            static s => s.Cache.HeldLocally<T>(s.Options.CacheKey.Name) is null
                && s.Cache.MemorySet(s.Options, s.Value, s.Cache.ConnectedLifetime(s.Policy), null, s.Policy));
        if (added)
        {
            return new(FillOutcome.Added, default);
        }

        return HeldLocally<T>(name) is { } existing ? new(FillOutcome.Existed, existing.Value) : default;
    }

    private async ValueTask<ChangeResult<T>> ChangeLocallyAsync<T>(CacheEntryOptions options, T? value, IEqualityComparer<T> comparer, CachePolicy policy)
    {
        var name = options.CacheKey.Name;
        var local = EffectiveLocalOptions(options, policy);
        for (var attempt = 0; attempt < ChangeAttempts; attempt++)
        {
            var last = attempt == ChangeAttempts - 1;
            var seen = HeldLocally<T>(name);
            // The comparer never runs in the key's turn: a slow one would stall every key of the stripe.
            if (seen is not null && ChangeComparison.AreEqual(comparer, seen.Value, value))
            {
                return new(ChangeOutcome.Unchanged, seen.Value);
            }

            // A write since the comparison is compared again; after the last try the value is written over it.
            var change = new LocalChange();
            _background.Exclusive(
                name,
                (Cache: this, Options: local, Value: value, Policy: policy, Seen: seen, Last: last, Change: change),
                static s => s.Cache.TryStoreLocalChange(s.Options, s.Value, s.Policy, s.Seen, s.Last, s.Change));

            switch (change.State)
            {
                case LocalChangeState.Stored when !change.Overwrote:
                    return new(ChangeOutcome.Filled, value);
                case LocalChangeState.Stored:
                    if (!await Layered<T>().PublishSetAsync(options).ConfigureAwait(false))
                    {
                        LogReplacedNotPublished(Logged(options, typeof(T)));
                    }

                    return new(ChangeOutcome.Replaced, value);
                case LocalChangeState.Lapsed:
                case LocalChangeState.Refused:
                    return default;
            }
        }

        return default;
    }

    private bool TryStoreLocalChange<T>(CacheEntryOptions options, T? value, CachePolicy policy, ICacheEntry<T?>? seen, bool last, LocalChange change)
    {
        var current = HeldLocally<T>(options.CacheKey.Name);
        if (!last && !(current is null || ReferenceEquals(current, seen)))
        {
            return false;
        }

        // The comparer may have outlasted the deadline.
        if (options.Expiration <= _clock.GetUtcNow())
        {
            change.State = LocalChangeState.Lapsed;
            return false;
        }

        change.Overwrote = current is not null;
        change.State = MemorySet(options, value, ConnectedLifetime(policy), null, policy) ? LocalChangeState.Stored : LocalChangeState.Refused;
        return change.State == LocalChangeState.Stored;
    }

    /// <summary>Keeps an inner replacement's value if nothing changed the key since the mark, then announces it.</summary>
    private async ValueTask KeepAndAnnounceAsync<T>(CacheEntryOptions options, long mark, T? value, CachePolicy policy)
    {
        // The keep counts as a change of the key, so an earlier replacement or read cannot put an older value back.
        if (!Layered<T>().FillFromInnerRead(options, mark, value, policy, advance: true))
        {
            // Any refusal also drops the copy now held: one extra inner read, never a stale hit.
            RemoveLocal(options.CacheKey.Name);
        }

        if (!await Layered<T>().PublishSetAsync(options).ConfigureAwait(false))
        {
            LogReplacedNotPublished(Logged(options, typeof(T)));
        }
    }

    /// <summary>Keeps the value for the disconnected lifetime unless the key has a local copy; given a mark, only if nothing changed the key since.</summary>
    private void KeepUnfilled<T>(CacheEntryOptions options, long? mark, T? value, CachePolicy policy)
    {
        try
        {
            var state = (Cache: this, Options: options, Value: value, Policy: policy);
            static bool Keep((MultilayerCache Cache, CacheEntryOptions Options, T? Value, CachePolicy Policy) s) =>
                !s.Cache._memoryCache.TryGetValue(s.Options.CacheKey.Name, out _)
                && s.Cache.MemorySet(s.Options, s.Value, s.Cache.DisconnectedLifetime(s.Policy), null, s.Policy, read: true, failSafe: s.Policy);

            if (mark is { } taken)
            {
                _background.Fill(options.CacheKey.Name, taken, state, Keep);
            }
            else
            {
                ApplyLocal(options.CacheKey.Name, state, Keep);
            }
        }
        catch (Exception ex)
        {
            LogInnerCacheFillError(ex, Logged(options, typeof(T)));
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "TryFill or SetIfChanged skipped for {CacheKey}: {Reason}.")]
    private partial void LogFillSkipped(LoggedKey cacheKey, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inner cache fill or compare-and-set for cacheKey {CacheKey} failed.")]
    private partial void LogInnerCacheFillError(Exception ex, LoggedKey cacheKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "SetIfChanged replaced {CacheKey} in the inner cache but the event was not published; peers may serve a stale copy until it expires.")]
    private partial void LogReplacedNotPublished(LoggedKey cacheKey);

    private sealed class ThrowRecordingComparer<T>(IEqualityComparer<T> inner) : IEqualityComparer<T>
    {
        public Exception? Thrown { get; private set; }

        public bool Equals(T? x, T? y)
        {
            try
            {
                return inner.Equals(x, y);
            }
            catch (Exception ex)
            {
                Thrown = ex;
                throw;
            }
        }

        public int GetHashCode(T obj) => inner.GetHashCode(obj!);
    }

    private sealed class LocalChange
    {
        public LocalChangeState State { get; set; }

        public bool Overwrote { get; set; }
    }
}
