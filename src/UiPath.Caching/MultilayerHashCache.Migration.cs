namespace UiPath.Caching;

internal sealed partial class MultilayerHashCache : IMigrationHashCache
{
    /// <summary>The snapshot of the inner tier, which holds the data; a local copy is never consulted. An inner tier that cannot report one answers <see cref="SnapshotOutcome.Failed"/>.</summary>
    public ValueTask<HashSnapshotRead<T>> ReadSnapshotAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default) =>
        _innerCache is IMigrationHashCache migration ? migration.ReadSnapshotAsync<T>(cacheKey, policy, token) : ValueTask.FromResult(default(HashSnapshotRead<T>));

    /// <summary>Awaits the store write, never the background path, and announces it as a drop of every local copy; the outcome is the inner tier's, and a failed announcement after a committed write stays committed. An inner tier without <see cref="IMigrationHashCache"/> reports <see cref="StoreOutcome.Committed"/> for a true <c>SetAsync</c> and <see cref="StoreOutcome.Unknown"/> otherwise.</summary>
    public ValueTask<StoreOutcome> WriteAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, HashCacheEntryOptions options, CachePolicy? policy, CancellationToken token = default) =>
        StoredAsync<T>(
            cacheKey,
            async ct => _innerCache is IMigrationHashCache migration
                ? await migration.WriteAsync(cacheKey, values, options, policy, ct).ConfigureAwait(false)
                : Plain(await _innerCache.SetAsync(cacheKey, values, options, policy, ct).ConfigureAwait(false)),
            token);

    /// <summary>As <see cref="WriteAsync{T}"/>, for the delete of the key.</summary>
    public ValueTask<StoreOutcome> DeleteAsync<T>(CacheKey cacheKey, CancellationToken token = default) =>
        StoredAsync<T>(
            cacheKey,
            async ct => _innerCache is IMigrationHashCache migration
                ? await migration.DeleteAsync<T>(cacheKey, ct).ConfigureAwait(false)
                : Plain(await _innerCache.RemoveAsync<T>(cacheKey, ct).ConfigureAwait(false)),
            token);

    /// <summary>As <see cref="WriteAsync{T}"/>, for the delete of fields; an inner tier that cannot remove single fields reports <see cref="StoreOutcome.NotSent"/>.</summary>
    public ValueTask<StoreOutcome> DeleteFieldsAsync<T>(CacheKey cacheKey, string[] fields, CancellationToken token = default)
    {
        HashFields.ValidateFieldsForWrite(fields);
        return StoredAsync<T>(
            cacheKey,
            async ct => _innerCache switch
            {
                IMigrationHashCache migration => await migration.DeleteFieldsAsync<T>(cacheKey, fields, ct).ConfigureAwait(false),
                ISpanKeyHashCache inner => Plain(await inner.RemoveFieldsAsync<T>(cacheKey, fields, ct).ConfigureAwait(false)),
                _ => StoreOutcome.NotSent,
            },
            token);
    }

    /// <summary>As <see cref="WriteAsync{T}"/>, for the refresh of the lifetime and the metadata.</summary>
    public ValueTask<StoreOutcome> RenewAsync<T>(CacheKey cacheKey, HashCacheEntryOptions options, CachePolicy? policy, CancellationToken token = default) =>
        StoredAsync<T>(
            cacheKey,
            async ct => _innerCache is IMigrationHashCache migration
                ? await migration.RenewAsync<T>(cacheKey, options, policy, ct).ConfigureAwait(false)
                : Plain(await _innerCache.RefreshAsync<T>(cacheKey, options, policy, ct).ConfigureAwait(false)),
            token);

    /// <summary>As <see cref="WriteAsync{T}"/>, for the expiry-only refresh. An inner tier without <see cref="IMigrationHashCache"/> reports <see cref="StoreOutcome.Committed"/> for a true refresh and <see cref="StoreOutcome.Unknown"/> otherwise.</summary>
    public ValueTask<StoreOutcome> RenewExpiryAsync<T>(CacheKey cacheKey, HashCacheEntryOptions options, CachePolicy? policy, CancellationToken token = default) =>
        StoredAsync<T>(
            cacheKey,
            async ct => _innerCache is IMigrationHashCache migration
                ? await migration.RenewExpiryAsync<T>(cacheKey, options with { ExpireTime = GetExpiration(options, policy ?? _defaultPolicy), TimeToLive = null }, policy, ct).ConfigureAwait(false)
                : Plain(await _innerCache.RefreshAsync<T>(cacheKey, GetExpiration(options, policy ?? _defaultPolicy), policy, ct).ConfigureAwait(false)),
            token);

    private static StoreOutcome Plain(bool done) => done ? StoreOutcome.Committed : StoreOutcome.Unknown;

    // The local copy goes first and every other node is told after, as for a delete; the store call is ordered with the key's other writes and is the only one that records the outcome.
    private async ValueTask<StoreOutcome> StoredAsync<T>(CacheKey cacheKey, Func<CancellationToken, ValueTask<StoreOutcome>> store, CancellationToken token)
    {
        if (token.IsCancellationRequested || !_sharedInner || GetInnerCacheDisconnected())
        {
            return StoreOutcome.NotSent;
        }

        var sink = new StoreSink();
        InternalHashCacheEntryOptions options;
        try
        {
            NotCacheableException.ThrowIfNotCacheable<T>();
            options = _entryBuilder.BuildEntryOptions<T>(cacheKey, default, token: token);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogInnerCacheRemoveError(ex, Logged(cacheKey, typeof(T)));
            return StoreOutcome.NotSent;
        }

        // Counting is on before the local copy goes and stays on until the store call ends, so a read that began before cannot put the old value back, with or without a broadcast to correct it.
        _background.Track();
        LogClearingLocalCached(Logged(options, typeof(T)));
        try
        {
            await _background.WriteAsync(
                options.CacheKey.Name,
                (Cache: this, Options: options, Sink: sink, Store: store),
                static s => s.Cache.RemoveLocal(s.Options.CacheKey.Name),
                static async s =>
                {
                    try
                    {
                        var outcome = await s.Store(s.Options.Token).ConfigureAwait(false);
                        s.Sink.Record(outcome);
                        return outcome == StoreOutcome.Committed;
                    }
                    catch (Exception)
                    {
                        s.Sink.Record(StoreOutcome.Unknown);
                        throw;
                    }
                },
                options.Token).ConfigureAwait(false);
            await _eventPublisher.CacheRemovedAsync(options, typeof(T)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (options.Token.IsCancellationRequested)
        {
            // A cancelled call may have landed; the sink already says so.
        }
        catch (Exception ex)
        {
            LogInnerCacheRemoveError(ex, Logged(options, typeof(T)));
        }

        return sink.Outcome ?? StoreOutcome.NotSent;
    }
}
