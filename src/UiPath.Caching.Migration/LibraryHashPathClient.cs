using UiPath.Caching.Redis;

namespace UiPath.Caching.Migration;

/// <summary>The library path of a hash cache: a failed snapshot read is unavailable and never absent, and a write that was not sent is told apart from one whose result is unknown, which is never replayed.</summary>
internal sealed class LibraryHashPathClient<T>(IMigrationHashCache store, IConnectionState connection, Func<string, string> redisKey, Func<TimeSpan> settleWithin) : IHashPathClient<T>
    where T : class
{
    public LibraryHashPathClient(IMigrationHashCache store, IConnectionState connection, Func<string, string> redisKey, TimeSpan settleWithin)
        : this(store, connection, redisKey, () => settleWithin)
    {
    }

    public TimeSpan? SettleWithin => settleWithin();

    /// <summary>The library hash cache itself, for the members the migration store has no form of: a presence check and an expiry.</summary>
    public IHashCache? Cache { get; init; }

    /// <summary>The shared tier of the library hash cache, which has the write that creates a hash only when it is absent.</summary>
    public ISharedHashTier? Tier { get; init; }

    public Func<WriteLease>? WritePolicy { get; init; }

    public bool KeepsStoredOnFill => true;

    public async ValueTask<HashRead<T>> ReadAsync(string key, string[]? fields, CancellationToken cancellationToken)
    {
        if (!connection.IsConnected)
        {
            return HashRead<T>.Of(ReadOutcome.Unavailable);
        }

        try
        {
            var read = await store.ReadSnapshotAsync<T>(CacheKey.Verbatim(redisKey(key)), null, cancellationToken).ConfigureAwait(false);
            var result = read.Outcome switch
            {
                SnapshotOutcome.Found when read.Entry is { Value: { Count: > 0 } value } entry => new HashRead<T>(ReadOutcome.Found, value, entry.Expiration == DateTimeOffset.MaxValue ? null : entry.Expiration, entry.Metadata),

                // A hash that is cached empty is the answer, and an older one is not to be looked for.
                SnapshotOutcome.Found => new HashRead<T>(ReadOutcome.Absent, null, read.Entry?.Expiration is { } at && at != DateTimeOffset.MaxValue ? at : null, read.Entry?.Metadata) { Stops = true },
                SnapshotOutcome.Absent => HashRead<T>.Of(ReadOutcome.Absent),
                SnapshotOutcome.Unreadable => await SelectedOnlyAsync(key, fields, cancellationToken).ConfigureAwait(false),
                _ => HashRead<T>.Of(ReadOutcome.Unavailable),
            };
            return fields is null ? result : result.Only(fields);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return HashRead<T>.Of(ReadOutcome.Unavailable);
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

    public ValueTask<WriteResult> SetAsync(string key, IDictionary<string, T?> fields, HashWriteOptions options, CancellationToken cancellationToken) =>
        WriteAsync(
            key,
            async (cacheKey, token) =>
            {
                using var lease = WritePolicy?.Invoke();
                var entry = new HashCacheEntryOptions(options.Deadline ?? DateTimeOffset.MaxValue, null, options.Metadata, options.Mode);
                return await store.WriteAsync(cacheKey, fields, entry, lease?.Policy, token).ConfigureAwait(false);
            },
            cancellationToken);

    public ValueTask<WriteResult> RemoveAsync(string key, CancellationToken cancellationToken) =>
        WriteAsync(key, (cacheKey, token) => store.DeleteAsync<T>(cacheKey, token), cancellationToken);

    public ValueTask<WriteResult> RemoveFieldsAsync(string key, string[] fields, CancellationToken cancellationToken) =>
        WriteAsync(key, (cacheKey, token) => store.DeleteFieldsAsync<T>(cacheKey, fields, token), cancellationToken);

    public ValueTask<WriteResult> RefreshAsync(string key, CacheExpiry expiry, CancellationToken cancellationToken) =>
        WriteAsync(key, (cacheKey, token) => store.RenewExpiryAsync<T>(cacheKey, new HashCacheEntryOptions(expiry.Deadline, expiry.Relative), null, token), cancellationToken);

    public ValueTask<WriteResult> RefreshAsync(string key, CacheExpiry expiry, IDictionary<string, string?>? metadata, CancellationToken cancellationToken) =>
        WriteAsync(
            key,
            async (cacheKey, token) =>
            {
                using var lease = WritePolicy?.Invoke();
                return await store.RenewAsync<T>(cacheKey, new HashCacheEntryOptions(expiry.Deadline, expiry.Relative, metadata), lease?.Policy, token).ConfigureAwait(false);
            },
            cancellationToken);

    /// <summary>A miss fill is the library's own fill, which writes only when absent; a lost one leaves the winner's hash and expiration in the box.</summary>
    public async ValueTask<WriteResult> FillAsync(string key, IDictionary<string, T?> fields, HashWriteOptions options, HashFillBox<T>? stored, CancellationToken cancellationToken)
    {
        var tier = Tier ?? throw new NotSupportedException("The library cache is not available for a fill.");
        if (cancellationToken.IsCancellationRequested || !connection.IsConnected)
        {
            return WriteOutcome.NotSent;
        }

        var cacheKey = CacheKey.Verbatim(redisKey(key));
        var bound = settleWithin();
        try
        {
            using var lease = WritePolicy?.Invoke();
            var written = await tier.FillVersionedAsync(cacheKey, fields, new HashCacheEntryOptions(options.Deadline ?? DateTimeOffset.MaxValue, null, options.Metadata, HashCacheSetOption.KeyReplace), lease?.Policy, new PeerFieldLimits(0, 0), cancellationToken).ConfigureAwait(false);
            if (written.Written)
            {
                return WriteOutcome.Committed;
            }

            if (!written.Existed)
            {
                // Nothing proves the fill did not run: the library swallows a failure after the send, and a connection that dropped before the answer may have applied it. Not sent is judged before the send only.
                return WriteResult.UnknownWithin(bound);
            }

            if (stored is not null)
            {
                var winner = await ReadAsync(key, null, cancellationToken).ConfigureAwait(false);
                stored.Lost = true;
                stored.CachedEmpty = winner.Stops;
                stored.Stored = winner.Kind == ReadOutcome.Found ? winner.Fields : null;
                stored.StoredExpiration = winner.Expiration;
                stored.Unreadable = winner.Kind == ReadOutcome.Unreadable;
            }

            return WriteOutcome.NotSent;
        }
        catch (Exception ex) when (ex is not ArgumentException and not OperationCanceledException and not OutOfMemoryException)
        {
            return WriteResult.UnknownWithin(bound);
        }
    }

    // A field that cannot be read fails the whole snapshot; the fields asked for may still be readable on their own, as the native read of them is.
    private async ValueTask<HashRead<T>> SelectedOnlyAsync(string key, string[]? fields, CancellationToken cancellationToken)
    {
        if (fields is not { Length: > 0 } || Cache is not { } cache)
        {
            return HashRead<T>.Of(ReadOutcome.Unreadable);
        }

        var selected = await cache.GetAsync<T>(CacheKey.Verbatim(redisKey(key)), fields, null, cancellationToken).ConfigureAwait(false);
        // The public read also returns null fields when the selected values cannot be read.
        return selected.Values.Any(value => value is not null) ? new HashRead<T>(ReadOutcome.Found, selected, null) : HashRead<T>.Of(ReadOutcome.Unreadable);
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

                // The store answered and applied nothing: the hash is as it was.
                StoreOutcome.Failed => WriteOutcome.NotSent,
                _ => WriteResult.UnknownWithin(bound),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return WriteResult.UnknownWithin(bound);
        }
    }
}
