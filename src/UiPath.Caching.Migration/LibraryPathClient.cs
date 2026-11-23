namespace UiPath.Caching.Migration;

/// <summary>The library path: a failed snapshot read is unavailable and never absent, and a write that was not sent is told apart from one whose result is unknown, which is never replayed.</summary>
internal sealed class LibraryPathClient<T>(IMigrationCache store, IConnectionState connection, Func<string, string> redisKey, Func<TimeSpan> settleWithin) : IPathClient<T>
    where T : class
{
    public LibraryPathClient(IMigrationCache store, IConnectionState connection, Func<string, string> redisKey, TimeSpan settleWithin)
        : this(store, connection, redisKey, () => settleWithin)
    {
    }

    /// <summary>A stored null reads as found, for a registration with <see cref="RouterCacheRegistration{T}.CacheNulls"/>.</summary>
    public bool CacheNulls { get; init; }

    public TimeSpan? SettleWithin => settleWithin();

    public async ValueTask<PathRead<T>> ReadAsync(string key, CancellationToken cancellationToken)
    {
        if (!connection.IsConnected)
        {
            return PathRead<T>.Of(ReadOutcome.Unavailable);
        }

        try
        {
            var read = await store.ReadSnapshotAsync<T>(CacheKey.Verbatim(redisKey(key)), null, cancellationToken).ConfigureAwait(false);
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
        WriteAsync(key, (cacheKey, token) => store.WriteAsync<T>(cacheKey, value, deadline ?? DateTimeOffset.MaxValue, null, token), cancellationToken);

    public async ValueTask<AddResult> TryAddAsync(string key, T value, DateTimeOffset? deadline, CancellationToken cancellationToken)
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
            var created = await store.TryCreateAsync<T>(cacheKey, value, deadline ?? DateTimeOffset.MaxValue, null, cancellationToken).ConfigureAwait(false);
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
