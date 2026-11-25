namespace UiPath.Caching.Migration.Tests.Support;

internal sealed class StoredHash
{
    public Dictionary<string, Item?> Fields { get; set; } = [];

    public DateTimeOffset? Expiration { get; set; }

    public IDictionary<string, string?>? Metadata { get; set; }

    /// <summary>The hash is cached empty: a marker that is the answer, with no field in it.</summary>
    public bool CachedEmpty { get; set; }
}

/// <summary>One Redis, as both the source and the library path of a hash cache see it: a view over a shared store with scripted outcomes.</summary>
internal sealed class FakeHashBackend(Dictionary<string, StoredHash> store, ManualTimeProvider clock) : ISourceHashCache<Item>, IHashPathClient<Item>, IDisposable
{
    private readonly List<string> _log = [];

    public ReadOutcome? ReadOverride { get; set; }

    public WriteOutcome WriteOutcome { get; set; } = WriteOutcome.Committed;

    /// <summary>With an unknown outcome, whether the command lands anyway, as a late commit does.</summary>
    public bool UnknownLands { get; set; }

    public TimeSpan? SettleWithin { get; set; }

    public bool? ContainsAnswer { get; set; }

    /// <summary>What the expiry read answers whatever is stored, when set; an expired hash is not found otherwise.</summary>
    public DateTimeOffset? ReportedExpiry { get; set; }

    /// <summary>The client runs key move forms: it has no other client to fall back on.</summary>
    public bool Moving { get; set; }

    public bool NoFallback => Moving;

    public bool Disposed { get; private set; }

    /// <summary>Thrown by every read and write, as an adapter whose server is down throws.</summary>
    public Exception? Failure { get; set; }

    /// <summary>What the key retry hold of a key answers; the shared no-retry sentinel when not set.</summary>
    public Func<string, Task>? Settled { get; set; }

    public Func<Task>? AfterRead { get; set; }

    public bool KeepsStoredOnFill => true;

    public int Reads { get; private set; }

    public int Writes { get; private set; }

    public int Removes { get; private set; }

    public int FieldRemoves { get; private set; }

    public int Refreshes { get; private set; }

    public int Fills { get; private set; }

    public int Clears { get; private set; }

    public string[]? LastFields { get; private set; }

    public string[]? LastRemovedFields { get; private set; }

    public HashWriteOptions? LastOptions { get; private set; }

    public CacheExpiry LastRefreshExpiry { get; private set; }

    public bool? LastRefreshHadMetadataForm { get; private set; }

    public IDictionary<string, string?>? LastRefreshMetadata { get; private set; }

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

    public async ValueTask<HashRead<Item>> ReadAsync(string key, string[]? fields, CancellationToken cancellationToken)
    {
        Reads++;
        ThrowIfFailing();
        LastFields = fields;
        Note($"read:{key}");
        var hash = Lookup(key);
        var kind = ReadOverride ?? (hash is null ? ReadOutcome.Absent : ReadOutcome.Found);
        if (AfterRead is { } gate)
        {
            await gate().ConfigureAwait(false);
        }

        if (kind != ReadOutcome.Found || hash is null)
        {
            return HashRead<Item>.Of(kind == ReadOutcome.Found ? ReadOutcome.Absent : kind);
        }

        if (hash.CachedEmpty)
        {
            return new HashRead<Item>(ReadOutcome.Absent, null, hash.Expiration, hash.Metadata) { Stops = true };
        }

        var found = new HashRead<Item>(kind, new Dictionary<string, Item?>(hash.Fields), hash.Expiration, hash.Metadata);
        return fields is null ? found : found.Only(fields);
    }

    public ValueTask<bool?> ContainsAsync(string key, CancellationToken cancellationToken)
    {
        Note($"contains:{key}");
        return ValueTask.FromResult(ContainsAnswer ?? (Lookup(key) is not null ? true : null));
    }

    public ValueTask<DateTimeOffset?> ExpireTimeAsync(string key, CancellationToken cancellationToken)
    {
        Note($"expire:{key}");
        return ValueTask.FromResult(ReportedExpiry ?? Lookup(key)?.Expiration);
    }

    public async ValueTask<WriteResult> SetAsync(string key, IDictionary<string, Item?> fields, HashWriteOptions options, CancellationToken cancellationToken)
    {
        Writes++;
        ThrowIfFailing();
        LastOptions = options;
        Note($"set:{key}:{options.Mode}");
        await Task.Yield();
        return Result(Apply(WriteOutcome, () => Put(key, fields, options)));
    }

    public async ValueTask<WriteResult> RemoveAsync(string key, CancellationToken cancellationToken)
    {
        Removes++;
        ThrowIfFailing();
        Note($"remove:{key}");
        await Task.Yield();
        return Result(Apply(WriteOutcome, () => Drop(key)));
    }

    public async ValueTask<WriteResult> RemoveFieldsAsync(string key, string[] fields, CancellationToken cancellationToken)
    {
        FieldRemoves++;
        ThrowIfFailing();
        LastRemovedFields = fields;
        Note($"fields:{key}");
        await Task.Yield();
        return Result(Apply(WriteOutcome, () => DropFields(key, fields)));
    }

    public ValueTask<WriteResult> RefreshAsync(string key, CacheExpiry expiry, CancellationToken cancellationToken)
    {
        LastRefreshHadMetadataForm = false;
        return Refresh(key, expiry, null);
    }

    public ValueTask<WriteResult> RefreshAsync(string key, CacheExpiry expiry, IDictionary<string, string?>? metadata, CancellationToken cancellationToken)
    {
        LastRefreshHadMetadataForm = true;
        return Refresh(key, expiry, metadata);
    }

    public async ValueTask<WriteResult> FillAsync(string key, IDictionary<string, Item?> fields, HashWriteOptions options, HashFillBox<Item>? stored, CancellationToken cancellationToken)
    {
        Fills++;
        if (Lookup(key) is { } existing)
        {
            if (stored is not null)
            {
                stored.Lost = true;
                stored.Stored = new Dictionary<string, Item?>(existing.Fields);
            }

            return WriteOutcome.NotSent;
        }

        return await SetAsync(key, fields, options, cancellationToken);
    }

    public Task SettledAsync(string key) => Settled?.Invoke(key) ?? PathClientTasks.NoPendingRetry;

    async ValueTask<SourceRead<IDictionary<string, Item?>>> ISourceHashCache<Item>.GetAsync(string key, string[]? fields, CancellationToken cancellationToken)
    {
        var read = await ReadAsync(key, fields, cancellationToken).ConfigureAwait(false);
        return read.Kind switch
        {
            ReadOutcome.Found => SourceRead<IDictionary<string, Item?>>.Found(read.Fields!),
            ReadOutcome.Unreadable => SourceRead<IDictionary<string, Item?>>.Unreadable,
            ReadOutcome.Unavailable => SourceRead<IDictionary<string, Item?>>.Unavailable,
            _ => SourceRead<IDictionary<string, Item?>>.Absent,
        };
    }

    ValueTask<WriteResult> ISourceHashCache<Item>.SetAsync(string key, IDictionary<string, Item?> fields, HashCacheSetOption mode, CacheExpiry expiry, CancellationToken cancellationToken) =>
        SetAsync(key, fields, new HashWriteOptions(mode, expiry.Deadline), cancellationToken);

    public ValueTask ClearAsync(CancellationToken cancellationToken)
    {
        Clears++;
        lock (store)
        {
            store.Clear();
        }

        return ValueTask.CompletedTask;
    }

    public void Dispose() => Disposed = true;

    private ValueTask<WriteResult> Refresh(string key, CacheExpiry expiry, IDictionary<string, string?>? metadata)
    {
        Refreshes++;
        LastRefreshExpiry = expiry;
        LastRefreshMetadata = metadata;
        Note($"refresh:{key}");
        var outcome = Apply(WriteOutcome, () => Renew(key, expiry, metadata));
        return ValueTask.FromResult(Lookup(key) is null && outcome == WriteOutcome.Committed ? WriteOutcome.NotSent : Result(outcome));
    }

    private void Renew(string key, CacheExpiry expiry, IDictionary<string, string?>? metadata)
    {
        if (Lookup(key) is { } hash)
        {
            hash.Expiration = expiry.Deadline ?? (expiry.Relative is { } span ? clock.GetUtcNow() + span : null);
            if (LastRefreshHadMetadataForm == true)
            {
                hash.Metadata = metadata;
            }
        }
    }

    private void ThrowIfFailing()
    {
        if (Failure is { } failure)
        {
            throw failure;
        }
    }

    private WriteResult Result(WriteOutcome outcome) => outcome == WriteOutcome.Unknown && SettleWithin is { } within ? WriteResult.UnknownWithin(within) : outcome;

    private WriteOutcome Apply(WriteOutcome outcome, Action action)
    {
        if (outcome == WriteOutcome.Committed || (outcome == WriteOutcome.Unknown && UnknownLands))
        {
            action();
        }

        return outcome;
    }

    private StoredHash? Lookup(string key)
    {
        lock (store)
        {
            return store.TryGetValue(key, out var hash) && (hash.Expiration is null || hash.Expiration > clock.GetUtcNow()) ? hash : null;
        }
    }

    private void Put(string key, IDictionary<string, Item?> fields, HashWriteOptions options)
    {
        lock (store)
        {
            if (options.Mode == HashCacheSetOption.HashReplace && store.TryGetValue(key, out var existing))
            {
                foreach (var (name, value) in fields)
                {
                    existing.Fields[name] = value;
                }

                existing.Expiration = options.Deadline ?? existing.Expiration;
                return;
            }

            store[key] = new StoredHash { Fields = new Dictionary<string, Item?>(fields), Expiration = options.Deadline, Metadata = options.Metadata };
        }
    }

    private void Drop(string key)
    {
        lock (store)
        {
            store.Remove(key);
        }
    }

    private void DropFields(string key, string[] fields)
    {
        lock (store)
        {
            if (store.TryGetValue(key, out var hash))
            {
                foreach (var field in fields)
                {
                    hash.Fields.Remove(field);
                }
            }
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
