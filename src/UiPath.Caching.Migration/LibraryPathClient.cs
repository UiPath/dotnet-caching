namespace UiPath.Caching.Migration;

/// <summary>The library path: a failed snapshot read is unavailable and never absent, and a write that was not sent is told apart from one whose result is unknown, which is never replayed.</summary>
/// <remarks><paramref name="writePolicy"/> is asked at each write, so a group can write another format than the stack default, and <paramref name="readPolicy"/> at each read, so a group that is not ready is never rewritten on read; neither may throw.</remarks>
internal sealed class LibraryPathClient<T>(IMigrationCache store, IConnectionState connection, Func<string, string> redisKey, Func<TimeSpan> settleWithin, Func<WriteLease>? writePolicy = null, Func<CachePolicy?>? readPolicy = null) : IPathClient<T>
    where T : class
{
    private ILibraryBatch<T>? _batch;

    public LibraryPathClient(IMigrationCache store, IConnectionState connection, Func<string, string> redisKey, TimeSpan settleWithin)
        : this(store, connection, redisKey, () => settleWithin)
    {
    }

    /// <summary>A stored null reads as found, for a registration with <see cref="RouterCacheRegistration{T}.CacheNulls"/>.</summary>
    public bool CacheNulls { get; init; }

    public TimeSpan? SettleWithin => settleWithin();

    /// <summary>The library cache itself, for the members the migration store has no form of: a conditional create and a refresh.</summary>
    public ICache? Cache { get; init; }

    public ILibraryBatch<T>? PlainBatch => Cache is { } cache ? _batch ??= new LibraryBatch<T>(cache, redisKey, writePolicy, readPolicy) : null;

    public bool KeepsStoredOnFill => Cache is not null;

    public async ValueTask<PathRead<T>> ReadAsync(string key, CancellationToken cancellationToken)
    {
        if (!connection.IsConnected)
        {
            return PathRead<T>.Of(ReadOutcome.Unavailable);
        }

        try
        {
            var read = await store.ReadSnapshotAsync<T>(CacheKey.Verbatim(redisKey(key)), readPolicy?.Invoke(), cancellationToken).ConfigureAwait(false);
            return read.Outcome switch
            {
                SnapshotOutcome.Found when read.Entry is { } entry && (entry.Value is not null || CacheNulls) => new PathRead<T>(ReadOutcome.Found, entry.Value, entry.Expiration == DateTimeOffset.MaxValue ? null : entry.Expiration),
                SnapshotOutcome.Found or SnapshotOutcome.Absent => PathRead<T>.Of(ReadOutcome.Absent),
                SnapshotOutcome.Unreadable => PathRead<T>.Of(ReadOutcome.Unreadable),
                _ => PathRead<T>.Of(ReadOutcome.Unavailable),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return PathRead<T>.Of(ReadOutcome.Unavailable);
        }
    }

    public ValueTask<WriteResult> SetAsync(string key, T value, DateTimeOffset? deadline, CancellationToken cancellationToken) =>
        WriteAsync(
            key,
            writePolicy is null
                ? (cacheKey, token) => store.WriteAsync<T>(cacheKey, value, deadline ?? DateTimeOffset.MaxValue, null, token)
                : (cacheKey, token) => WriteLeasedAsync(writePolicy, cacheKey, value, deadline, token),
            cancellationToken);

    public async ValueTask<AddResult> TryCreateAsync(string key, T value, DateTimeOffset? deadline, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || !connection.IsConnected)
        {
            return AddOutcome.NotSent;
        }

        CacheKey cacheKey;
        try
        {
            cacheKey = CacheKey.Verbatim(redisKey(key));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return AddOutcome.NotSent;
        }

        var bound = settleWithin();
        try
        {
            // The group's write format, under the same lease as a write, so a create never writes a format the group is not ready for.
            var created = writePolicy is null
                ? await store.TryCreateAsync<T>(cacheKey, value, deadline ?? DateTimeOffset.MaxValue, null, cancellationToken).ConfigureAwait(false)
                : await CreateLeasedAsync(writePolicy, cacheKey, value, deadline, cancellationToken).ConfigureAwait(false);
            return created switch
            {
                AddOutcome.Added or AddOutcome.Exists or AddOutcome.NotSent => created,
                _ => AddResult.UnknownWithin(bound),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return AddResult.UnknownWithin(bound);
        }
    }

    public ValueTask<WriteResult> RemoveAsync(string key, CancellationToken cancellationToken) =>
        WriteAsync(key, (cacheKey, token) => store.DeleteAsync<T>(cacheKey, token), cancellationToken);

    public async ValueTask<TryAddResult<T>> TryAddAsync(string key, T? value, DateTimeOffset? deadline, CancellationToken cancellationToken)
    {
        var cache = Cache ?? throw new NotSupportedException("The library cache is not available for a conditional create.");
        if (cancellationToken.IsCancellationRequested || !connection.IsConnected)
        {
            return TryAddResult<T>.Failed(WriteOutcome.NotSent);
        }

        var cacheKey = CacheKey.Verbatim(redisKey(key));
        var bound = settleWithin();
        try
        {
            // False is a lost race or a failure the library swallowed, a reply lost after the send included; a value that a read finds proves nothing about who wrote it, so false is always Unknown.
            using var lease = writePolicy?.Invoke();
            return await cache.TryAddAsync<T>(cacheKey, value, deadline ?? DateTimeOffset.MaxValue, lease?.Policy, cancellationToken).ConfigureAwait(false)
                ? TryAddResult<T>.Created
                : TryAddResult<T>.Failed(WriteResult.UnknownWithin(bound));
        }
        catch (Exception ex) when (ex is not ArgumentException and not OutOfMemoryException and not OperationCanceledException)
        {
            return TryAddResult<T>.Failed(WriteResult.UnknownWithin(bound));
        }
    }

    public async ValueTask<SwapResult<T>> SetIfChangedAsync(string key, T? value, IEqualityComparer<T> comparer, DateTimeOffset? deadline, CancellationToken cancellationToken)
    {
        var cache = Cache ?? throw new NotSupportedException("The library cache is not available for a compare-and-swap.");
        if (!connection.IsConnected)
        {
            return new SwapResult<T>(default, WriteOutcome.NotSent);
        }

        var bound = settleWithin();
        try
        {
            using var lease = writePolicy?.Invoke();
            var change = await cache.SetIfChangedAsync<T>(CacheKey.Verbatim(redisKey(key)), value, comparer, deadline ?? DateTimeOffset.MaxValue, lease?.Policy, cancellationToken).ConfigureAwait(false);
            return new SwapResult<T>(change, change.Outcome switch
            {
                ChangeOutcome.Filled or ChangeOutcome.Replaced => WriteOutcome.Committed,

                // Failed is what the library answers for an error it swallowed, a timeout after the send included.
                ChangeOutcome.Failed => WriteResult.UnknownWithin(bound),
                _ => WriteOutcome.NotSent,
            });
        }
        catch (Exception ex) when (ex is not ArgumentException and not OutOfMemoryException and not OperationCanceledException)
        {
            // The swap may have applied: it is settled as an unknown write. An argument the library refuses is the caller's, and a cancellation is settled by the router and rethrown.
            return new SwapResult<T>(default, WriteResult.UnknownWithin(bound));
        }
    }

    /// <summary>A miss fill is the library's own fill, which writes only when absent; a lost one leaves the winner's value and expiration in the box.</summary>
    public async ValueTask<WriteResult> FillAsync(string key, T value, DateTimeOffset? deadline, FillBox<T>? stored, CancellationToken cancellationToken)
    {
        // Not sent is judged before the send only.
        if (!connection.IsConnected)
        {
            return WriteOutcome.NotSent;
        }

        if (Cache is null)
        {
            return await SetAsync(key, value, deadline, cancellationToken).ConfigureAwait(false);
        }

        var fill = await TryFillAsync(key, value, deadline, cancellationToken).ConfigureAwait(false);
        switch (fill.Outcome)
        {
            case FillOutcome.Added:
                return WriteOutcome.Committed;
            case FillOutcome.Existed when stored is not null:
                stored.Lost = true;
                stored.Stored = fill.Existing;
                stored.CachedNull = fill.Existing is null;
                stored.StoredExpiration = fill.Expiration;
                return WriteOutcome.NotSent;
            case FillOutcome.Unreadable when stored is not null:
                stored.Lost = true;
                stored.Unreadable = true;
                return WriteOutcome.NotSent;
            case FillOutcome.Existed or FillOutcome.Unreadable:
                return WriteOutcome.NotSent;
            default:
                // Failed: the library hid what happened after the send, and a connection that dropped before the answer may have applied the fill.
                return WriteResult.UnknownWithin(settleWithin());
        }
    }

    public async ValueTask<FillResult<T>> TryFillAsync(string key, T? value, DateTimeOffset? deadline, CancellationToken cancellationToken)
    {
        var cache = Cache ?? throw new NotSupportedException("The library cache is not available for a fill.");
        if (!connection.IsConnected)
        {
            return default;
        }

        try
        {
            using var lease = writePolicy?.Invoke();
            return await cache.TryFillAsync<T>(CacheKey.Verbatim(redisKey(key)), value, deadline ?? DateTimeOffset.MaxValue, lease?.Policy, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not ArgumentException and not OperationCanceledException and not OutOfMemoryException)
        {
            return default;
        }
    }

    public async ValueTask<TimeSpan?> TimeToLiveAsync(string key, CancellationToken cancellationToken)
    {
        var cache = Cache ?? throw new NotSupportedException("The library cache is not available for a remaining life.");
        if (!connection.IsConnected)
        {
            return null;
        }

        try
        {
            return await cache.TimeToLiveAsync<T>(CacheKey.Verbatim(redisKey(key)), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            return null;
        }
    }

    public async ValueTask<DateTimeOffset?> ExpireTimeAsync(string key, CancellationToken cancellationToken)
    {
        var cache = Cache ?? throw new NotSupportedException("The library cache is not available for an expiry.");
        if (!connection.IsConnected)
        {
            return null;
        }

        try
        {
            return await cache.ExpireTimeAsync<T>(CacheKey.Verbatim(redisKey(key)), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            return null;
        }
    }

    public async ValueTask<WriteResult> RefreshAsync(string key, CacheExpiry expiry, CancellationToken cancellationToken)
    {
        var cache = Cache ?? throw new NotSupportedException("The library cache is not available for a refresh.");
        if (!connection.IsConnected)
        {
            throw new CacheUnavailableException("The library cannot answer a refresh.");
        }

        var cacheKey = CacheKey.Verbatim(redisKey(key));
        var bound = settleWithin();
        try
        {
            var refreshed = expiry switch
            {
                { Deadline: { } at } => await cache.RefreshAsync<T>(cacheKey, at, null, cancellationToken).ConfigureAwait(false),
                { Relative: { } span } => await cache.RefreshAsync<T>(cacheKey, span, null, cancellationToken).ConfigureAwait(false),
                _ => await cache.RefreshAsync<T>(cacheKey, null, cancellationToken).ConfigureAwait(false),
            };
            // False is an absent key or a failure the library swallowed, and a shorter life can commit and expire before any read: false is always Unknown.
            return refreshed ? WriteOutcome.Committed : WriteResult.UnknownWithin(bound);
        }
        catch (Exception ex) when (ex is not ArgumentException and not OutOfMemoryException and not OperationCanceledException)
        {
            return WriteResult.UnknownWithin(bound);
        }
    }

    public async ValueTask<bool?> ContainsAsync(string key, CancellationToken cancellationToken)
    {
        if (Cache is not { } cache)
        {
            return null;
        }

        if (!connection.IsConnected)
        {
            throw new CacheUnavailableException("The library cannot answer a presence check.");
        }

        try
        {
            return await cache.ContainsAsync<T>(CacheKey.Verbatim(redisKey(key)), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            throw new CacheUnavailableException("The library could not answer a presence check.", ex);
        }
    }

    private async ValueTask<AddOutcome> CreateLeasedAsync(Func<WriteLease> policy, CacheKey cacheKey, T value, DateTimeOffset? deadline, CancellationToken cancellationToken)
    {
        using var lease = policy();
        return await store.TryCreateAsync<T>(cacheKey, value, deadline ?? DateTimeOffset.MaxValue, lease.Policy, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<StoreOutcome> WriteLeasedAsync(Func<WriteLease> policy, CacheKey cacheKey, T value, DateTimeOffset? deadline, CancellationToken cancellationToken)
    {
        using var lease = policy();
        return await store.WriteAsync<T>(cacheKey, value, deadline ?? DateTimeOffset.MaxValue, lease.Policy, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<WriteResult> WriteAsync(string key, Func<CacheKey, CancellationToken, ValueTask<StoreOutcome>> command, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || !connection.IsConnected)
        {
            return WriteOutcome.NotSent;
        }

        CacheKey cacheKey;
        try
        {
            cacheKey = CacheKey.Verbatim(redisKey(key));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return WriteOutcome.NotSent;
        }

        var bound = settleWithin();
        try
        {
            var write = await command(cacheKey, cancellationToken).ConfigureAwait(false);
            return write switch
            {
                StoreOutcome.Committed => WriteOutcome.Committed,
                StoreOutcome.NotSent => WriteOutcome.NotSent,
                _ => WriteResult.UnknownWithin(bound),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return WriteResult.UnknownWithin(bound);
        }
    }
}
