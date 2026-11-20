namespace UiPath.Caching;

internal sealed partial class MultilayerHashCache
{
    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, CachePolicy? policy, CancellationToken token = default)
    {
        policy ??= _defaultPolicy;
        return SetCoreAsync(cacheKey, values, GetExpiration(policy), policy, token);
    }

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) =>
        SetCoreAsync(cacheKey, values, GetExpiration(expiration), policy ?? _defaultPolicy, token);

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) =>
        SetCoreAsync(cacheKey, values, GetExpiration(expiration), policy ?? _defaultPolicy, token);

    public ValueTask<bool> SetAsync<T>(Span<char> cacheKey, IDictionary<string, T?> values, CachePolicy? policy, CancellationToken token = default) =>
        SetAsync(new CacheKey(cacheKey), values, policy, token);

    public ValueTask<bool> SetAsync<T>(Span<char> cacheKey, IDictionary<string, T?> values, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) =>
        SetAsync(new CacheKey(cacheKey), values, expiration, policy, token);

    public ValueTask<bool> SetAsync<T>(Span<char> cacheKey, IDictionary<string, T?> values, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) =>
        SetAsync(new CacheKey(cacheKey), values, expiration, policy, token);

    public ValueTask<bool> SetAsync<T>(Span<char> cacheKey, IDictionary<string, T?> values, HashCacheEntryOptions options, CachePolicy? policy, CancellationToken token = default) =>
        SetAsync(new CacheKey(cacheKey), values, options, policy, token);

    public async ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, HashCacheEntryOptions options, CachePolicy? policy, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        policy ??= _defaultPolicy;
        // An empty write replaces the key whole in the inner tier, so it does locally too.
        var setOption = IsNullOrEmpty(values) ? HashCacheSetOption.KeyReplace : options.SetOption;
        var cacheEntryOptions = _entryBuilder.BuildEntryOptions<T>(cacheKey, GetExpiration(options, policy), setOption, token) with { Partial = setOption == HashCacheSetOption.HashReplace };
        cacheEntryOptions.Metadata = options.Metadata;
        if (IsNullOrEmpty(values) && !_multiLayerCacheOptions.CacheNullValues)
        {
            return await RemoveAsync<T>(cacheEntryOptions).ConfigureAwait(false);
        }

        values ??= new Dictionary<string, T?>();

        LogReplacingCachedKey(Logged(cacheEntryOptions, typeof(T)));
        var innerCacheDisconnected = GetInnerCacheDisconnected();
        if (innerCacheDisconnected)
        {
            return await Layered<T>().InternalSetAsync(cacheEntryOptions, values, innerCacheDisconnected, policy).ConfigureAwait(false);
        }

        if (policy.BackgroundDistributedWrite == true && _background.TryReserve())
        {
            return SetInBackground(cacheEntryOptions, values, policy);
        }

        var (written, kept, counted) = await Layered<T>().SetInnerAsync(cacheEntryOptions, values, innerCacheDisconnected, policy, keepRefused: false).ConfigureAwait(false);
        return written && await Layered<T>().PublishSetAsync(cacheEntryOptions, counted).ConfigureAwait(false) && kept;
    }

    public ValueTask<bool> RemoveAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return RemoveAsync<T>(_entryBuilder.BuildEntryOptions<T>(cacheKey, default, token: token));
    }

    public async ValueTask<bool> RemoveFieldsAsync<T>(CacheKey cacheKey, string[] fields, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        HashFields.ValidateFieldsForWrite(fields);
        if (_innerCache is not ISpanKeyHashCache inner)
        {
            throw new NotSupportedException($"{_innerCache.GetType().Name} does not remove single fields of a hash.");
        }

        var options = _entryBuilder.BuildEntryOptions<T>(cacheKey, default, token: token);
        if (fields.Length == 0)
        {
            return true;
        }

        // The whole local hash goes, here and on every node: an entry is replaced whole, never patched, so a read racing the removal cannot bring a removed field back.
        LogClearingLocalCached(Logged(options, typeof(T)));
        var removed = false;
        var issued = false;
        var cancelled = false;
        try
        {
            // The callback reports that the delete went out, so a cancel while waiting for an earlier write is told apart from one during it.
            (issued, removed, cancelled) = await _background.WriteAsync(
                options.CacheKey.Name,
                (Cache: this, Options: options, Fields: fields, Inner: inner),
                static s => s.Cache.RemoveLocal(s.Options.CacheKey.Name),
                [AsyncMethodBuilder(typeof(Pooled<>))] static async ValueTask<(bool Issued, bool Removed, bool Cancelled)> (s) =>
                {
                    try
                    {
                        var removed = s.Cache._versioned is { } versioned
                            ? await versioned.RemoveFieldsVersionedAsync<T>(s.Options.CacheKey, s.Fields, s.Options.Token).ConfigureAwait(false)
                            : await s.Inner.RemoveFieldsAsync<T>(s.Options.CacheKey, s.Fields, s.Options.Token).ConfigureAwait(false);
                        return (true, removed, false);
                    }
                    catch (OperationCanceledException) when (s.Options.Token.IsCancellationRequested)
                    {
                        return (true, false, true);
                    }
                    catch (Exception ex)
                    {
                        s.Cache.LogInnerCacheRemoveError(ex, s.Cache.Logged(s.Options, typeof(T)));
                        return (true, false, false);
                    }
                },
                options.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !options.Token.IsCancellationRequested)
        {
            LogInnerCacheRemoveError(ex, Logged(options, typeof(T)));
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        if (!issued)
        {
            options.Token.ThrowIfCancellationRequested();
            return false;
        }

        // Announced however the delete ended, a cancel included: some of its chunks may have landed.
        bool announced;
        try
        {
            announced = await _eventPublisher.CacheRemovedAsync(options, typeof(T)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogInnerCacheRemoveError(ex, Logged(options, typeof(T)));
            announced = false;
        }

        if (cancelled)
        {
            throw new OperationCanceledException(options.Token);
        }

        return announced && removed;
    }

    public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default) =>
        RefreshAsync<T>(cacheKey, new HashCacheEntryOptions(), policy, token);

    public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) =>
        RefreshAsync<T>(cacheKey, new HashCacheEntryOptions(GetExpiration(expiration)), policy, token);

    public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) =>
        RefreshAsync<T>(cacheKey, new HashCacheEntryOptions(GetExpiration(expiration)), policy, token);

    public async ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, HashCacheEntryOptions options, CachePolicy? policy, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        policy ??= _defaultPolicy;
        var expiration = GetExpiration(options, policy);
        var cacheEntryOptions = _entryBuilder.BuildEntryOptions<T>(cacheKey, expiration, token: token);
        cacheEntryOptions.Metadata = options.Metadata;
        LogClearingCached(Logged(cacheEntryOptions, typeof(T)));

        LogRefreshingInnerCacheKey(Logged(cacheEntryOptions, typeof(T)), cacheEntryOptions.Expiration);
        try
        {
            return await _background.WriteAsync(
                cacheEntryOptions.CacheKey.Name,
                (Cache: this, Options: cacheEntryOptions, Refresh: options, Expiration: expiration, Policy: policy, Token: token),
                static s => s.Cache.RemoveLocal(s.Options.CacheKey.Name),
                static async s =>
                {
                    var fired = await s.Cache._eventPublisher.CacheRefreshedAsync(s.Options, typeof(T)).ConfigureAwait(false);
                    // Forward the multilayer-resolved expiration so the inner write uses the same TTL the broadcast announced.
                    var innerOptions = s.Refresh with { ExpireTime = s.Expiration, TimeToLive = null };
                    return fired && await s.Cache._innerCache.RefreshAsync<T>(s.Options.CacheKey, innerOptions, s.Policy, s.Token).ConfigureAwait(false);
                },
                token).ConfigureAwait(false);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && token.IsCancellationRequested))
        {
            LogInnerCacheRefreshError(ex, Logged(cacheEntryOptions, typeof(T)));
            return false;
        }
    }

    private async ValueTask<bool> SetCoreAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, DateTimeOffset expiration, CachePolicy policy, CancellationToken token)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var options = _entryBuilder.BuildEntryOptions<T>(cacheKey, expiration, token: token);
        if (IsNullOrEmpty(values) && !_multiLayerCacheOptions.CacheNullValues)
        {
            return await RemoveAsync<T>(options).ConfigureAwait(false);
        }

        values ??= new Dictionary<string, T?>();

        LogReplacingCachedKey(Logged(options, typeof(T)));
        var innerCacheDisconnected = GetInnerCacheDisconnected();
        if (innerCacheDisconnected)
        {
            return await Layered<T>().InternalSetAsync(options, values, innerCacheDisconnected, policy).ConfigureAwait(false);
        }
        else
        {
            if (policy.BackgroundDistributedWrite == true && _background.TryReserve())
            {
                return SetInBackground(options, values, policy);
            }

            var (written, kept, counted) = await Layered<T>().SetInnerAsync(options, values, innerCacheDisconnected, policy, keepRefused: false).ConfigureAwait(false);
            return written && await Layered<T>().PublishSetAsync(options, counted).ConfigureAwait(false) && kept;
        }
    }

    private async ValueTask<bool> RemoveAsync<T>(InternalHashCacheEntryOptions options)
    {
        LogClearingLocalCached(Logged(options, typeof(T)));
        try
        {
            // Released before the event, so a slow one does not hold the key's background writes.
            var removed = await _background.WriteAsync(
                options.CacheKey.Name,
                (Cache: this, Options: options),
                static s => s.Cache.RemoveLocal(s.Options.CacheKey.Name),
                static s => RemoveInnerAsync(s.Cache._innerCache.RemoveAsync<T>(s.Options.CacheKey, s.Options.Token), s.Options.Token),
                options.Token).ConfigureAwait(false);

            var eventFired = await _eventPublisher.CacheRemovedAsync(options, typeof(T)).ConfigureAwait(false);
            return removed && eventFired;
        }
        catch (OperationCanceledException) when (options.Token.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            LogInnerCacheRemoveError(ex, Logged(options, typeof(T)));
            return false;
        }
    }

    /// <param name="policy">The policy the hash is kept under, which says whether a peer's write refreshes it; fields kept without the hash never are.</param>
    /// <param name="read">Counts the entry as read now; a refresh keep passes false and inherits the read time of the copy it replaces.</param>
    private bool MemorySet<T>(InternalHashCacheEntryOptions options, IDictionary<string, T?> value, TimeSpan? maxExpiration, bool keepWhenAbsent = false, CachePolicy? policy = null, bool read = true)
    {
        if (!options.Partial)
        {
            return MemorySet(options, CreateEntry(value, options), maxExpiration, policy, read);
        }

        // Read, merged and written as one, so two merges of one hash do not each drop the other's fields.
        lock (MergeLock(options.CacheKey.Name))
        {
            if (TryMergeLocal(ref options, value, out var merged))
            {
                return MemorySet(options, CreateEntry(merged, options), maxExpiration, policy, read);
            }

            // Nothing held here to merge into: the fields alone are not the hash, so the next read takes it whole from the inner tier, unless there is none.
            options.Version = 0;
            if (keepWhenAbsent || OnlyTier)
            {
                return MemorySet(options, CreateEntry(value, options), maxExpiration, read: read);
            }
        }

        DropAwaitingRefresh(options.CacheKey.Name);
        return true;
    }

    // Under the key's merge lock, so a merge never reads the hash a write is replacing and writes it back.
    private bool MemorySet<T>(InternalHashCacheEntryOptions options, ICacheEntry<IDictionary<string, T?>> item, TimeSpan? maxExpiration, CachePolicy? policy = null, bool read = true)
    {
        lock (MergeLock(options.CacheKey.Name))
        {
            var target = RefreshTarget(RefreshReader<T>.Instance, policy, item);
            Track(item, target is not null);
            if (target is not null && read)
            {
                MarkRead(item);
            }

            if (_versioned is not null && (options.Version > 0 || options.Own is { Length: > 0 }))
            {
                HashVersions.Stamp<T>(item, options.Version, maxExpiration is { } max ? _clock.ToDateTimeOffset(max) : null, options.Own);
            }

            return _localMemorySetter.Set(options, item, typeof(T), maxExpiration, target);
        }
    }
}
