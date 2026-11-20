namespace UiPath.Caching;

internal sealed partial class MultilayerCache
{
    /// <summary>Awaits the store write, never the background path; the outcome is the inner tier's, and a failed announcement or local keep after a committed write stays committed. An inner tier without <see cref="IMigrationCache"/> reports <see cref="StoreOutcome.Committed"/> for a true <c>SetAsync</c> and <see cref="StoreOutcome.Unknown"/> otherwise.</summary>
    public async ValueTask<StoreOutcome> WriteAsync<T>(CacheKey cacheKey, T? value, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default)
    {
        if (token.IsCancellationRequested || (_sharedInner && GetInnerCacheDisconnected()))
        {
            return StoreOutcome.NotSent;
        }

        if (!_sharedInner)
        {
            return await WriteLocallyAsync(cacheKey, value, expiration, policy, token).ConfigureAwait(false);
        }

        var sink = new StoreSink();
        try
        {
            NotCacheableException.ThrowIfNotCacheable<T>();
            var options = _entryBuilder.BuildEntryOptions<T>(cacheKey, GetExpiration(expiration), token);
            if (value is null && !_multiLayerCacheOptions.CacheNullValues)
            {
                await RemoveRecordedAsync<T>(options, sink).ConfigureAwait(false);
            }
            else
            {
                LogReplacingCachedKey(Logged(options, typeof(T)));
                var (written, counted) = await SetRecordedAsync(options, value, policy ?? _defaultPolicy, sink).ConfigureAwait(false);
                if (written)
                {
                    await Layered<T>().PublishSetAsync(options, counted).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogInnerCacheSetError(ex, Logged(cacheKey, typeof(T)));
        }

        return sink.Outcome ?? StoreOutcome.NotSent;
    }

    /// <summary>Awaits the store delete; the outcome is the inner tier's, and a failed announcement after a committed delete stays committed. An inner tier without <see cref="IMigrationCache"/> reports <see cref="StoreOutcome.Committed"/> for a true <c>RemoveAsync</c> and <see cref="StoreOutcome.Unknown"/> otherwise.</summary>
    public async ValueTask<StoreOutcome> DeleteAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        if (token.IsCancellationRequested || (_sharedInner && GetInnerCacheDisconnected()))
        {
            return StoreOutcome.NotSent;
        }

        if (!_sharedInner)
        {
            return await RemoveAsync<T>(cacheKey, token).ConfigureAwait(false) ? StoreOutcome.Committed : StoreOutcome.Failed;
        }

        var sink = new StoreSink();
        try
        {
            NotCacheableException.ThrowIfNotCacheable<T>();
            await RemoveRecordedAsync<T>(_entryBuilder.BuildEntryOptions<T>(cacheKey, default, token), sink).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogInnerCacheRemoveError(ex, Logged(cacheKey, typeof(T)));
        }

        return sink.Outcome ?? StoreOutcome.NotSent;
    }

    private async ValueTask<StoreOutcome> WriteLocallyAsync<T>(CacheKey cacheKey, T? value, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token)
    {
        try
        {
            var kept = await SetCoreAsync(cacheKey, value, GetExpiration(expiration), policy ?? _defaultPolicy, token).ConfigureAwait(false);
            return kept ? StoreOutcome.Committed : StoreOutcome.Failed;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogInnerCacheSetError(ex, Logged(cacheKey, typeof(T)));
            return StoreOutcome.Failed;
        }
    }

    /// <summary>The ordered inner write of <c>SetAsync</c> with the sink handed to the inner store call only.</summary>
    private async ValueTask<(bool Written, VersionedWrite Counted)> SetRecordedAsync<T>(CacheEntryOptions options, T? value, CachePolicy policy, StoreSink sink)
    {
        try
        {
            var (written, _, counted) = await _background.WriteAsync(
                options.CacheKey.Name,
                (Cache: this, Shape: new CacheShape<T>(this), Options: options, Value: value, Policy: policy, Sink: sink),
                null,
                static (s, keeper) => s.Cache.WriteRecordedAsync(s.Options, s.Value, s.Policy, s.Sink),
                static (s, written, keeper) => (written, s.Cache.KeepAfterInnerWrite(written, false, s.Policy, (s.Shape, s.Options, s.Value, Keeper: keeper, Written: written, s.Policy), static (x, max) => x.Shape.KeepWritten(x.Keeper, x.Options, x.Value, max, null, x.Shape.Version(x.Written), x.Policy), s.Options.Token), s.Shape.Counted(written)),
                options.Token).ConfigureAwait(false);
            return (written, counted);
        }
        catch (OperationCanceledException) when (options.Token.IsCancellationRequested)
        {
            return (false, default);
        }
        catch (Exception ex)
        {
            LogInnerCacheSetError(ex, Logged(options, typeof(T)));
            return (false, default);
        }
    }

    private async ValueTask<bool> WriteRecordedAsync<T>(CacheEntryOptions options, T? value, CachePolicy policy, StoreSink sink)
    {
        try
        {
            if (!LateStillCurrent(options))
            {
                return false;
            }

            StoreOutcome outcome;
            if (_innerCache is IMigrationCache migration)
            {
                outcome = await migration.WriteAsync<T?>(options.CacheKey, value, options.Expiration, policy, options.Token).ConfigureAwait(false);
            }
            else
            {
                outcome = await _innerCache.SetAsync<T?>(options.CacheKey, value, options.Expiration, policy, options.Token).ConfigureAwait(false) ? StoreOutcome.Committed : StoreOutcome.Unknown;
            }

            sink.Record(outcome);
            return outcome == StoreOutcome.Committed;
        }
        catch (Exception)
        {
            sink.Record(StoreOutcome.Unknown);
            throw;
        }
    }

    /// <summary>The ordered removal of <c>RemoveAsync</c> with the sink handed to the inner store call only.</summary>
    private async ValueTask RemoveRecordedAsync<T>(CacheEntryOptions options, StoreSink sink)
    {
        LogClearingLocalCached(Logged(options, typeof(T)));
        try
        {
            await _background.WriteAsync(
                options.CacheKey.Name,
                (Cache: this, Options: options, Sink: sink),
                static s => s.Cache.RemoveLocal(s.Options.CacheKey.Name),
                static s => s.Cache.DeleteRecordedAsync<T>(s.Options, s.Sink),
                options.Token).ConfigureAwait(false);
            await _eventPublisher.CacheRemovedAsync(options, typeof(T)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (options.Token.IsCancellationRequested)
        {
            // A cancelled delete may have landed; the sink already says so.
        }
        catch (Exception ex)
        {
            LogInnerCacheRemoveError(ex, Logged(options, typeof(T)));
        }
    }

    private async ValueTask<bool> DeleteRecordedAsync<T>(CacheEntryOptions options, StoreSink sink)
    {
        try
        {
            StoreOutcome outcome;
            if (_innerCache is IMigrationCache migration)
            {
                outcome = await migration.DeleteAsync<T>(options.CacheKey, options.Token).ConfigureAwait(false);
            }
            else
            {
                outcome = await _innerCache.RemoveAsync<T>(options.CacheKey, options.Token).ConfigureAwait(false) ? StoreOutcome.Committed : StoreOutcome.Unknown;
            }

            sink.Record(outcome);
            return outcome == StoreOutcome.Committed;
        }
        catch (Exception)
        {
            sink.Record(StoreOutcome.Unknown);
            throw;
        }
    }
}
