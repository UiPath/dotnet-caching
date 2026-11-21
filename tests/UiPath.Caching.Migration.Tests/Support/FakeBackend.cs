namespace UiPath.Caching.Migration.Tests.Support;

internal sealed record Item(string Name);

/// <summary>One Redis, as both the source and the library path see it: a view over a shared store with scripted outcomes.</summary>
internal sealed class FakeBackend(Dictionary<string, Item> store, ManualTimeProvider clock) : ISourceCache<Item>, IPathClient<Item>
{
    private readonly List<string> _log = [];

    public ReadOutcome? ReadOverride { get; set; }

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

    public DateTimeOffset? LastDeadline { get; private set; }

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

    public async ValueTask<AddResult> TryAddAsync(string key, Item value, DateTimeOffset? deadline, CancellationToken cancellationToken)
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
        TryAddAsync(key, value, expiry.Deadline, cancellationToken);

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
