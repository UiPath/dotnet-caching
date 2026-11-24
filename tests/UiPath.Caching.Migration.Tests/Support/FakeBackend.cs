namespace UiPath.Caching.Migration.Tests.Support;

internal sealed record Item(string Name);

/// <summary>One Redis, as both the source and the library path see it: a view over a shared store with scripted outcomes.</summary>
internal sealed class FakeBackend(Dictionary<string, Item> store, ManualTimeProvider clock) : ISourceCache<Item>, IPathClient<Item>
{
    private readonly List<string> _log = [];

    public ReadOutcome? ReadOverride { get; set; }

    public bool FillLosesToNull { get; set; }

    public int FillCalls { get; private set; }

    public WriteOutcome WriteOutcome { get; set; } = WriteOutcome.Committed;

    /// <summary>With an unknown outcome, whether the command lands anyway, as a late commit does.</summary>
    public bool UnknownLands { get; set; }

    public TimeSpan? ReportedTtl { get; set; }

    public Func<Task>? AfterRead { get; set; }

    public Func<Task>? BeforeClear { get; set; }

    public Exception? ClearFails { get; set; }

    public Exception? WriteFails { get; set; }

    public TimeSpan? SettleWithin { get; set; }

    /// <summary>Runs after a write is counted and before it is applied, to hold a command in flight.</summary>
    public Func<CancellationToken, Task>? InFlight { get; set; }

    public int Reads { get; private set; }

    public int Writes { get; private set; }

    public int Removes { get; private set; }

    public int Creates { get; private set; }

    /// <summary>Keys the source holds a cached null for.</summary>
    public HashSet<string> NullKeys { get; } = new(StringComparer.Ordinal);

    /// <summary>Forces the answer of a conditional create; null answers from the store.</summary>
    public AddOutcome? CreateOverride { get; set; }

    public int Clears { get; private set; }

    public int TryAdds { get; private set; }

    public int Refreshes { get; private set; }

    /// <summary>When set, the backend offers the library's own batch, as the plain library path does.</summary>
    public bool UsesPlainBatch { get; set; }

    /// <summary>When set, the library batch write throws after it was sent, as a timeout does.</summary>
    public Exception? BatchFails { get; set; }

    /// <summary>When set, a swap throws it after it was sent.</summary>
    public Exception? SwapFails { get; set; }

    /// <summary>What a refresh answers whatever is stored, when set.</summary>
    public WriteResult? RefreshOutcome { get; set; }

    /// <summary>The batch write stores a null as a value, as a library with null caching on does.</summary>
    public bool StoresNull { get; set; }

    /// <summary>The expiry the last batch get-or-add was given.</summary>
    public CacheExpiry LastBatchExpiry { get; set; }

    public List<string> BatchCalls { get; } = [];

    public ILibraryBatch<Item>? PlainBatch => UsesPlainBatch ? new FakeBatch(this, store) : null;

    public DateTimeOffset? LastRefresh { get; private set; }

    public DateTimeOffset? LastDeadline { get; set; }

    public IReadOnlyList<string> Log
    {
        get
        {
            lock (_log)
            {
                return [.. _log];
            }
        }
    }

    public bool KeepsStoredOnFill => true;

    public async ValueTask<PathRead<Item>> ReadAsync(string key, CancellationToken cancellationToken)
    {
        Reads++;
        Note($"read:{key}");
        var stored = Lookup(key) is not null;
        var kind = ReadOverride ?? (stored ? ReadOutcome.Found : ReadOutcome.Absent);
        var item = kind == ReadOutcome.Found ? Lookup(key) : null;
        if (AfterRead is { } gate)
        {
            await gate().ConfigureAwait(false);
        }

        if (kind != ReadOutcome.Found || item is null)
        {
            return PathRead<Item>.Of(kind == ReadOutcome.Found ? ReadOutcome.Absent : kind);
        }

        DateTimeOffset? expiration = null;
        if (ReportedTtl is { } ttl)
        {
            expiration = clock.GetUtcNow() + ttl;
        }

        return new PathRead<Item>(kind, item, expiration);
    }

    public async ValueTask<WriteResult> SetAsync(string key, Item value, DateTimeOffset? deadline, CancellationToken cancellationToken)
    {
        Writes++;
        LastDeadline = deadline;
        Note($"set:{key}");
        await BeforeSend(cancellationToken);
        return Result(Apply(WriteOutcome, () => Put(key, value)));
    }

    public async ValueTask<AddResult> TryCreateAsync(string key, Item value, DateTimeOffset? deadline, CancellationToken cancellationToken)
    {
        Creates++;
        LastDeadline = deadline;
        Note($"create:{key}");
        await BeforeSend(cancellationToken);
        if (CreateOverride is { } forced)
        {
            if (forced == AddOutcome.Added || (forced == AddOutcome.Unknown && UnknownLands))
            {
                Put(key, value);
            }

            return forced == AddOutcome.Unknown && SettleWithin is { } within ? AddResult.UnknownWithin(within) : forced;
        }

        lock (store)
        {
            return store.TryAdd(key, value) ? AddOutcome.Added : AddOutcome.Exists;
        }
    }

    public async ValueTask<WriteResult> RemoveAsync(string key, CancellationToken cancellationToken)
    {
        Removes++;
        Note($"remove:{key}");
        await BeforeSend(cancellationToken);
        return Result(Apply(WriteOutcome, () => Drop(key)));
    }

    public ValueTask<TryAddResult<Item>> TryAddAsync(string key, Item? value, DateTimeOffset? deadline, CancellationToken cancellationToken)
    {
        TryAdds++;
        Note($"tryadd:{key}");
        LastDeadline = deadline;
        lock (store)
        {
            if (store.ContainsKey(key))
            {
                return ValueTask.FromResult(TryAddResult<Item>.Exists(null));
            }
        }

        var outcome = Apply(WriteOutcome, () => Put(key, value!));
        return ValueTask.FromResult(outcome == WriteOutcome.Committed ? TryAddResult<Item>.Created : TryAddResult<Item>.Failed(Result(outcome)));
    }

    public ValueTask<SwapResult<Item>> SetIfChangedAsync(string key, Item? value, IEqualityComparer<Item> comparer, DateTimeOffset? deadline, CancellationToken cancellationToken)
    {
        Note($"swap:{key}");
        LastDeadline = deadline;
        if (SwapFails is { } failure)
        {
            throw failure;
        }
        var existing = Lookup(key);
        if (value is null)
        {
            if (existing is null)
            {
                return Swap(ChangeOutcome.Unchanged, null, WriteOutcome.NotSent);
            }

            Drop(key);
            return Swap(ChangeOutcome.Replaced, null, WriteOutcome.Committed);
        }

        if (existing is not null && comparer.Equals(existing, value))
        {
            return Swap(ChangeOutcome.Unchanged, existing, WriteOutcome.NotSent);
        }

        Writes++;
        var outcome = Apply(WriteOutcome, () => Put(key, value));
        var change = existing is null ? ChangeOutcome.Filled : ChangeOutcome.Replaced;
        return outcome == WriteOutcome.Committed
            ? Swap(change, value, WriteOutcome.Committed, deadline)
            : ValueTask.FromResult(new SwapResult<Item>(default, Result(outcome)));
    }

    public async ValueTask<WriteResult> FillAsync(string key, Item value, DateTimeOffset? deadline, FillBox<Item>? stored, CancellationToken cancellationToken)
    {
        FillCalls++;
        if (FillLosesToNull)
        {
            if (stored is not null)
            {
                stored.Lost = true;
                stored.CachedNull = true;
            }

            return WriteOutcome.NotSent;
        }

        if ((ReadOverride is null or ReadOutcome.Found ? Lookup(key) : null) is { } existing)
        {
            if (stored is not null)
            {
                stored.Lost = true;
                stored.Stored = existing;
            }

            return WriteOutcome.NotSent;
        }

        return await SetAsync(key, value, deadline, cancellationToken);
    }

    public ValueTask<FillResult<Item>> TryFillAsync(string key, Item? value, DateTimeOffset? deadline, CancellationToken cancellationToken)
    {
        Note($"fill:{key}");
        LastDeadline = deadline;
        var existing = Lookup(key);
        if (existing is not null)
        {
            return ValueTask.FromResult(new FillResult<Item>(FillOutcome.Existed, existing, clock.GetUtcNow() + (ReportedTtl ?? TimeSpan.FromMinutes(1))));
        }

        Put(key, value!);
        return ValueTask.FromResult(new FillResult<Item>(FillOutcome.Added, null, deadline));
    }

    public ValueTask<TimeSpan?> TimeToLiveAsync(string key, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Lookup(key) is not null ? ReportedTtl : null);

    public ValueTask<DateTimeOffset?> ExpireTimeAsync(string key, CancellationToken cancellationToken) =>
        ValueTask.FromResult<DateTimeOffset?>(Lookup(key) is not null && ReportedTtl is { } ttl ? clock.GetUtcNow() + ttl : null);

    public ValueTask<WriteResult> RefreshAsync(string key, CacheExpiry expiry, CancellationToken cancellationToken)
    {
        Refreshes++;
        LastRefresh = expiry.Deadline ?? (expiry.Relative is { } span ? clock.GetUtcNow() + span : null);
        Note($"refresh:{key}");
        return ValueTask.FromResult<WriteResult>(RefreshOutcome ?? (Lookup(key) is not null ? WriteOutcome.Committed : WriteOutcome.NotSent));
    }

    public ValueTask<bool?> ContainsAsync(string key, CancellationToken cancellationToken)
    {
        lock (store)
        {
            return ValueTask.FromResult<bool?>(store.ContainsKey(key));
        }
    }

    async ValueTask<SourceRead<Item>> ISourceCache<Item>.GetAsync(string key, CancellationToken cancellationToken)
    {
        if (NullKeys.Contains(key))
        {
            Reads++;
            return new SourceRead<Item>(ReadOutcome.Found, null);
        }

        var read = await ReadAsync(key, cancellationToken).ConfigureAwait(false);
        return read.Kind switch
        {
            ReadOutcome.Found => SourceRead<Item>.Found(read.Value!),
            ReadOutcome.Unreadable => SourceRead<Item>.Unreadable,
            ReadOutcome.Unavailable => SourceRead<Item>.Unavailable,
            _ => SourceRead<Item>.Absent,
        };
    }

    ValueTask<AddResult> ISourceCache<Item>.TryAddAsync(string key, Item value, CacheExpiry expiry, CancellationToken cancellationToken) =>
        TryCreateAsync(key, value, expiry.Deadline, cancellationToken);

    ValueTask<WriteResult> ISourceCache<Item>.SetAsync(string key, Item value, CacheExpiry expiry, CancellationToken cancellationToken) =>
        SetAsync(key, value, expiry.Deadline, cancellationToken);

    public async ValueTask ClearAsync(CancellationToken cancellationToken)
    {
        Clears++;
        Note("clear");
        if (BeforeClear is { } gate)
        {
            await gate().ConfigureAwait(false);
        }

        if (ClearFails is { } failure)
        {
            throw failure;
        }

        lock (store)
        {
            store.Clear();
        }
    }

    /// <summary>The late commit of a write that reported unknown and did not land.</summary>
    public void Land(string key, Item? value)
    {
        if (value is null)
        {
            Drop(key);
        }
        else
        {
            Put(key, value);
        }
    }

    private static ValueTask<SwapResult<Item>> Swap(ChangeOutcome outcome, Item? current, WriteOutcome write, DateTimeOffset? expiration = null) =>
        ValueTask.FromResult(new SwapResult<Item>(new ChangeResult<Item>(outcome, current, expiration), write));

    private async Task BeforeSend(CancellationToken cancellationToken)
    {
        if (InFlight is { } hold)
        {
            await hold(cancellationToken);
        }

        if (WriteFails is { } failure)
        {
            throw failure;
        }
    }

    private WriteResult Result(WriteOutcome outcome) =>
        outcome == WriteOutcome.Unknown && SettleWithin is { } within ? WriteResult.UnknownWithin(within) : outcome;

    private WriteOutcome Apply(WriteOutcome outcome, Action command)
    {
        if (outcome == WriteOutcome.Committed || (outcome == WriteOutcome.Unknown && UnknownLands))
        {
            command();
        }

        return outcome;
    }

    private Item? Lookup(string key)
    {
        lock (store)
        {
            return store.GetValueOrDefault(key);
        }
    }

    private void Put(string key, Item value)
    {
        lock (store)
        {
            store[key] = value;
        }
    }

    private void Drop(string key)
    {
        lock (store)
        {
            store.Remove(key);
        }
    }

    private void Note(string entry)
    {
        lock (_log)
        {
            _log.Add(entry);
        }
    }
}

internal sealed class FakeBatch(FakeBackend owner, Dictionary<string, Item> store) : ILibraryBatch<Item>
{
    public ValueTask<KeyValuePair<string, Item?>[]> GetAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken)
    {
        owner.BatchCalls.Add("get");
        lock (store)
        {
            return ValueTask.FromResult(keys.Select(k => new KeyValuePair<string, Item?>(k, store.GetValueOrDefault(k))).ToArray());
        }
    }

    public ValueTask<bool> SetAsync(IReadOnlyList<KeyValuePair<string, Item?>> entries, DateTimeOffset? deadline, CancellationToken cancellationToken)
    {
        owner.BatchCalls.Add("set");
        owner.LastDeadline = deadline;
        lock (store)
        {
            foreach (var (key, value) in entries)
            {
                if (value is null && !owner.StoresNull)
                {
                    store.Remove(key);
                }
                else
                {
                    store[key] = value!;
                }
            }
        }

        return owner.BatchFails is { } failure ? throw failure : ValueTask.FromResult(true);
    }

    public ValueTask<bool> RemoveAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken)
    {
        owner.BatchCalls.Add("remove");
        lock (store)
        {
            foreach (var key in keys)
            {
                store.Remove(key);
            }
        }

        return owner.BatchFails is { } failure ? throw failure : ValueTask.FromResult(true);
    }

    public async ValueTask<KeyValuePair<TState, Item?>[]> GetOrAddAsync<TState>(KeyValuePair<string, TState>[] entries, Func<TState[], CancellationToken, Task<KeyValuePair<TState, Item?>[]>> generator, CacheExpiry expiry, CancellationToken cancellationToken)
        where TState : notnull
    {
        owner.BatchCalls.Add("getoradd");
        owner.LastBatchExpiry = expiry;
        return await generator([.. entries.Select(e => e.Value)], cancellationToken);
    }
}
