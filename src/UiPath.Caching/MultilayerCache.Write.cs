namespace UiPath.Caching;

internal sealed partial class MultilayerCache
{
    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, T? value, CachePolicy? policy, CancellationToken token = default)
    {
        policy ??= _defaultPolicy;
        return SetCoreAsync(cacheKey, value, GetExpiration(policy), policy, token);
    }

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, T? value, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) =>
        SetCoreAsync(cacheKey, value, GetExpiration(expiration), policy ?? _defaultPolicy, token);

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, T? value, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) =>
        SetCoreAsync(cacheKey, value, GetExpiration(expiration), policy ?? _defaultPolicy, token);

    public ValueTask<bool> SetAsync<T>(Span<char> cacheKey, T? value, CachePolicy? policy, CancellationToken token = default) =>
        SetAsync(new CacheKey(cacheKey), value, policy, token);

    public ValueTask<bool> SetAsync<T>(Span<char> cacheKey, T? value, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) =>
        SetAsync(new CacheKey(cacheKey), value, expiration, policy, token);

    public ValueTask<bool> SetAsync<T>(Span<char> cacheKey, T? value, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) =>
        SetAsync(new CacheKey(cacheKey), value, expiration, policy, token);

    public ValueTask<bool> SetAsync<T>(KeyValuePair<CacheKey, T?>[] keyValues, CachePolicy? policy, CancellationToken token = default)
    {
        policy ??= _defaultPolicy;
        return SetCoreAsync(keyValues, GetExpiration(policy), policy, token);
    }

    public ValueTask<bool> SetAsync<T>(KeyValuePair<CacheKey, T?>[] keyValues, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) =>
        SetCoreAsync(keyValues, GetExpiration(expiration), policy ?? _defaultPolicy, token);

    public ValueTask<bool> SetAsync<T>(KeyValuePair<CacheKey, T?>[] keyValues, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) =>
        SetCoreAsync(keyValues, GetExpiration(expiration), policy ?? _defaultPolicy, token);

    /// <summary>
    /// One path for every provider: take the local lock, probe the local tier, let the L2 decide,
    /// then populate the local tier — the reverse of <c>SetAsync</c>, which writes both tiers
    /// unconditionally. The probe is what narrows an L2 that retains nothing, and so grants every
    /// caller a win, back to one winner per process; where the L2 does arbitrate, a local hit means
    /// the key was already claimed or read here, so the loss is reported without a round-trip. That
    /// can cost a win the L2 would have granted, when the local copy outlived the shared one — the
    /// fail-closed direction the ambiguous <c>false</c> already covers. A disconnected L2 answers
    /// for itself (<c>RedisCache</c> checks its connection first) rather than being gated on
    /// <c>GetInnerCacheDisconnected</c>, whose state also covers the broadcast transport: a dead
    /// topic must not stop a healthy Redis.
    /// </summary>
    public ValueTask<bool> TryAddAsync<T>(CacheKey cacheKey, T? value, CachePolicy? policy, CancellationToken token = default)
    {
        policy ??= _defaultPolicy;
        return TryAddCoreAsync(cacheKey, value, GetExpiration(policy), policy, token);
    }

    /// <inheritdoc cref="TryAddAsync{T}(CacheKey, T, CachePolicy, CancellationToken)"/>
    public ValueTask<bool> TryAddAsync<T>(CacheKey cacheKey, T? value, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) =>
        TryAddCoreAsync(cacheKey, value, GetExpiration(expiration), policy ?? _defaultPolicy, token);

    /// <inheritdoc cref="TryAddAsync{T}(CacheKey, T, CachePolicy, CancellationToken)"/>
    public ValueTask<bool> TryAddAsync<T>(CacheKey cacheKey, T? value, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) =>
        TryAddCoreAsync(cacheKey, value, GetExpiration(expiration), policy ?? _defaultPolicy, token);

    public ValueTask<bool> RemoveAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return RemoveAsync<T>(_entryBuilder.BuildEntryOptions<T>(cacheKey, default, token));
    }

    public ValueTask<bool> RemoveAsync<T>(CacheKey[] cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var options = Array.ConvertAll(cacheKey, k => _entryBuilder.BuildEntryOptions<T>(k, default, token));
        return RemoveAsync<T>(options, token);
    }

    public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default)
    {
        policy ??= _defaultPolicy;
        return RefreshCoreAsync<T>(cacheKey, GetExpiration(policy), policy, token);
    }

    public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) =>
        RefreshCoreAsync<T>(cacheKey, GetExpiration(expiration), policy ?? _defaultPolicy, token);

    public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) =>
        RefreshCoreAsync<T>(cacheKey, GetExpiration(expiration), policy ?? _defaultPolicy, token);

    private async ValueTask<bool> TryAddCoreAsync<T>(CacheKey cacheKey, T? value, DateTimeOffset expiration, CachePolicy policy, CancellationToken token)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var options = _entryBuilder.BuildEntryOptions<T>(cacheKey, expiration, token);

        if (value is null && !_multiLayerCacheOptions.CacheNullValues)
        {
            LogTryAddSkippedUnrepresentableValue(Logged(options, typeof(T)));
            return false;
        }

        if (options.Expiration <= _clock.GetUtcNow())
        {
            LogTryAddSkippedExpiredEntry(Logged(options, typeof(T)), options.Expiration);
            return false;
        }

        var localMaxExpiration = policy.LocalExpiration ?? _multiLayerCacheOptions.LocalMaxExpiration;
        if (localMaxExpiration is { } max && max <= TimeSpan.Zero)
        {
            LogTryAddSkippedNonPositiveLocalRetention(Logged(options, typeof(T)), max);
            return false;
        }

        return await TryAddUnderRunAsync(options, value, policy, localMaxExpiration).ConfigureAwait(false);
    }

    /// <summary>Takes the key's run for the add, so a miss does not generate the key, and a generator does not store over the added value, between the add's check and its write. A run that holds the key is waited for as long as the local lock would have been; one that still holds it then makes the add lose, as a lock not acquired in time did.</summary>
    private async ValueTask<bool> TryAddUnderRunAsync<T>(CacheEntryOptions options, T? value, CachePolicy policy, TimeSpan? localMaxExpiration)
    {
        var key = new InFlightKey(options.CacheKey.Name, typeof(T), null, null);
        if (!_generations.TryReserve(key, out var reservation)
            && (!await WaitForRunAsync(key, ResolveLocalLock(policy.Lock).Timeout, options.Token).ConfigureAwait(false) || !_generations.TryReserve(key, out reservation)))
        {
            LogTryAddKeyBusy(Logged(options, typeof(T)));
            return false;
        }

        var added = false;
        try
        {
            added = await TryAddUnderLocalLockAsync(options, value, policy, localMaxExpiration).ConfigureAwait(false);
            return added;
        }
        finally
        {
            // Callers that joined the add take its value, or take the key over when it lost.
            if (added)
            {
                reservation.Complete(_cacheEntryFactory.Create<T?>(value, options.Expiration));
            }
            else
            {
                reservation.Fail(new InFlightAbandonedException());
            }
        }
    }

    /// <summary>True when the run that held the key has ended, whether it stored or failed, and false when it still holds it after <paramref name="timeout"/>.</summary>
    private async ValueTask<bool> WaitForRunAsync(InFlightKey key, TimeSpan timeout, CancellationToken token)
    {
        if (!_generations.TryJoin(key, token, out var joined))
        {
            return true;
        }

        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
        bounded.CancelAfter(BoundedTimeout(timeout));
        try
        {
            await joined.Result.WaitAsync(bounded.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (bounded.IsCancellationRequested && !token.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception) when (!token.IsCancellationRequested)
        {
            // The run failed, so it holds the key no longer.
            return true;
        }
        finally
        {
            joined.Withdraw();
            // Left behind on a timeout or a cancellation, the wait still ends with the run's failure: observed here, not reported unobserved.
            joined.Result.Forget();
        }
    }

    /// <summary>
    /// Probe the local tier, then let the L2 decide. The probe is what narrows a store that retains
    /// nothing — and therefore grants every caller a win — back to one winner per process; where the
    /// L2 does arbitrate, a local hit means the key was already claimed or read here, so reporting
    /// the loss early is both correct and a round-trip saved. It can cost a win the L2 would have
    /// granted, when the local copy outlived the shared one; that is the fail-closed direction the
    /// ambiguous <c>false</c> already covers.
    /// </summary>
    private async ValueTask<bool> TryAddUnderLocalLockAsync<T>(CacheEntryOptions options, T? value, CachePolicy policy, TimeSpan? localMaxExpiration)
    {
        if (_memoryCache.TryGetValue(options.CacheKey.Name, out _))
        {
            return false;
        }

        // Released before the event, so a slow one does not hold the key's background writes.
        var added = await _background.WriteAsync(
            options.CacheKey.Name,
            (Cache: this, Options: options, Value: value, Policy: policy, Max: localMaxExpiration),
            null,
            [AsyncMethodBuilder(typeof(Pooled<>))] static async ValueTask<bool> (s, _) =>
            {
                try
                {
                    return await s.Cache._innerCache.TryAddAsync<T?>(s.Options.CacheKey, s.Value, s.Options.Expiration, s.Policy, s.Options.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (!(ex is OperationCanceledException && s.Options.Token.IsCancellationRequested))
                {
                    s.Cache.LogInnerCacheTryAddError(ex, s.Cache.Logged(s.Options, typeof(T)));
                    return false;
                }
            },
            static (s, added, keeper) =>
            {
                if (added)
                {
                    try
                    {
                        keeper.Local(s.Options.CacheKey.Name, s, static x => x.Cache.MemorySet(x.Options, x.Value, x.Max, null, x.Policy));
                    }
                    catch (Exception ex)
                    {
                        s.Cache.LogTryAddLocalPropagationFailed(ex, s.Cache.Logged(s.Options, typeof(T)));
                    }
                }

                return added;
            },
            options.Token).ConfigureAwait(false);
        if (!added)
        {
            return false;
        }

        // Best-effort after the win: a loss reported here would strand the entry with no owner.
        try
        {
            if (!await _eventPublisher.CacheSetAsync(options, typeof(T)).ConfigureAwait(false))
            {
                LogTryAddBroadcastNotPublished(Logged(options, typeof(T)));
            }
        }
        catch (Exception ex)
        {
            LogTryAddLocalPropagationFailed(ex, Logged(options, typeof(T)));
        }

        return true;
    }

    private async ValueTask<bool> SetCoreAsync<T>(CacheKey cacheKey, T? value, DateTimeOffset expiration, CachePolicy policy, CancellationToken token)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var cacheEntryOptions = _entryBuilder.BuildEntryOptions<T>(cacheKey, expiration, token);
        if (value is null && !_multiLayerCacheOptions.CacheNullValues)
        {
            return await RemoveAsync<T>(cacheEntryOptions).ConfigureAwait(false);
        }

        LogReplacingCachedKey(Logged(cacheEntryOptions, typeof(T)));
        var innerCacheDisconnected = GetInnerCacheDisconnected();
        if (innerCacheDisconnected)
        {
            return await Layered<T>().InternalSetAsync(cacheEntryOptions, value, innerCacheDisconnected, policy).ConfigureAwait(false);
        }

        if (policy.BackgroundDistributedWrite == true && _background.TryReserve(cacheEntryOptions.CacheKey.Name, true) && SetInBackground(cacheEntryOptions, value, policy) is { } detached)
        {
            return detached;
        }

        // Written before the event, so a node it wakes reads the new value.
        var (written, kept, _) = await Layered<T>().SetInnerAsync(cacheEntryOptions, value, innerCacheDisconnected, policy, keepRefused: false).ConfigureAwait(false);
        return written && await Layered<T>().PublishSetAsync(cacheEntryOptions).ConfigureAwait(false) && kept;
    }

    private async ValueTask<bool> SetCoreAsync<T>(KeyValuePair<CacheKey, T?>[] keyValues, DateTimeOffset expiration, CachePolicy policy, CancellationToken token)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var removeEntries = new List<CacheEntryOptions>();
        var setEntries = new List<CacheEntryValue<T>>();
        foreach (var keyValue in keyValues)
        {
            if (keyValue.Value is null && !_multiLayerCacheOptions.CacheNullValues)
            {
                removeEntries.Add(_entryBuilder.BuildEntryOptions<T>(keyValue.Key, token: token));
            }
            else
            {
                setEntries.Add(new (_entryBuilder.BuildEntryOptions<T>(keyValue.Key, expiration, token), keyValue.Value));
            }
        }

        if (removeEntries.Count > 0)
        {
            var result = await RemoveAsync<T>(removeEntries.ToArray(), token).ConfigureAwait(false);
            if (!result)
            {
                return false;
            }
        }

        var innerCacheDisconnected = GetInnerCacheDisconnected();
        var (written, kept) = await SetInnerAsync<T>(setEntries.ToArray(), innerCacheDisconnected, policy, keepRefused: false, token).ConfigureAwait(false);
        if (innerCacheDisconnected)
        {
            if (kept && _logger.IsEnabled(LogLevel.Trace))
            {
                LogSettingLocalOnlyForCacheKeys(Logged(setEntries.Select(o => o.CacheEntry).ToArray(), typeof(T)));
            }
            return kept;
        }

        // Announced even when the write reports failure: across Redis Cluster slots it can have written some keys, and a peer
        // that drops an unchanged copy only reads it again.
        var published = await PublishSetsAsync(setEntries).ConfigureAwait(false);
        return written && published && kept;
    }

    /// <summary>Every key is announced, even if one event is refused.</summary>
    private async ValueTask<bool> PublishSetsAsync<T>(List<CacheEntryValue<T>> setEntries)
    {
        var published = true;
        foreach (var cacheEntry in setEntries.Select(s => s.CacheEntry))
        {
            LogReplacingCachedKey(Logged(cacheEntry, typeof(T)));
            published &= await Layered<T>().PublishSetAsync(cacheEntry).ConfigureAwait(false);
        }

        return published;
    }

    private async ValueTask<bool> RefreshCoreAsync<T>(CacheKey cacheKey, DateTimeOffset expiration, CachePolicy policy, CancellationToken token)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var cacheEntryOptions = _entryBuilder.BuildEntryOptions<T>(cacheKey, expiration, token);
        LogClearingCached(Logged(cacheEntryOptions, typeof(T)));
        LogRefreshingInnerCacheKey(Logged(cacheEntryOptions, typeof(T)), cacheEntryOptions.Expiration);
        try
        {
            // In the key's order first, so a background set issued meanwhile lands after this and keeps its local value.
            return await _background.WriteAsync(
                cacheEntryOptions.CacheKey.Name,
                (Cache: this, Options: cacheEntryOptions, Policy: policy, Token: token),
                static s => s.Cache.RemoveLocal(s.Options.CacheKey.Name),
                static async s =>
                {
                    var fired = await s.Cache._eventPublisher.CacheRefreshedAsync(s.Options, typeof(T)).ConfigureAwait(false);
                    return fired && await s.Cache._innerCache.RefreshAsync<T>(s.Options.CacheKey, s.Options.Expiration, s.Policy, s.Token).ConfigureAwait(false);
                },
                token).ConfigureAwait(false);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && token.IsCancellationRequested))
        {
            LogInnerCacheRefreshError(ex, Logged(cacheKey, typeof(T)));
            return false;
        }
    }

    private async ValueTask<bool> RemoveAsync<T>(CacheEntryOptions options)
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

    private async ValueTask<bool> RemoveAsync<T>(CacheEntryOptions[] options, CancellationToken token = default)
    {
        try
        {
            // As the single-key remove does: the local entries go before the inner delete, so a failed one cannot leave them here.
            var removed = await _background.WriteAsync(
                options.Select(o => o.CacheKey.Name),
                (Cache: this, Options: options, Token: token),
                static s =>
                {
                    foreach (var option in s.Options)
                    {
                        s.Cache.RemoveLocal(option.CacheKey.Name);
                    }
                },
                static s => RemoveInnerAsync(s.Cache._innerCache.RemoveAsync<T>(Array.ConvertAll(s.Options, static o => o.CacheKey), s.Token), s.Token),
                token).ConfigureAwait(false);

            // Each key's invalidation is sent on its own, so one that fails does not keep the others from the other nodes. Started
            // together, as RemoveManyAsync does: one round trip for the batch, and their doorbells fold into one.
            var published = await AllAsync(options, options.Length, this, static (c, o) => c._eventPublisher.CacheRemovedAsync(o, typeof(T)), static (c, o, ex) => c.LogInnerCacheRemoveError(ex, c.Logged(o, typeof(T)))).ConfigureAwait(false);
            return removed && Array.TrueForAll(published, static p => p);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Warning))
            {
                LogInnerCacheRemoveKeysError(ex, Logged(options, typeof(T)));
            }
            return false;
        }
    }

    private async ValueTask<bool> InternalSetAsync<T>(CacheEntryValue<T>[] cacheEntries, bool innerCacheDisconnected, CachePolicy policy, bool keepRefused, CancellationToken token) =>
        (await SetInnerAsync(cacheEntries, innerCacheDisconnected, policy, keepRefused, token).ConfigureAwait(false)).Kept;

    private async ValueTask<(bool Written, bool Kept)> SetInnerAsync<T>(CacheEntryValue<T>[] cacheEntries, bool innerCacheDisconnected, CachePolicy policy, bool keepRefused, CancellationToken token)
    {
        try
        {
            var cacheKeyValuePairs = cacheEntries.Select(c => new KeyValuePair<CacheKey, T?>(c.CacheEntry.CacheKey, c.Value)).ToArray();

            if (innerCacheDisconnected)
            {
                if (_logger.IsEnabled(LogLevel.Trace))
                {
                    LogSettingLocalOnlyForCacheKeys(Logged(cacheEntries.Select(o => o.CacheEntry).ToArray(), typeof(T)));
                }
                return (false, Array.TrueForAll(cacheEntries, e => ApplyLocal(e.CacheEntry.CacheKey.Name, (Cache: this, Entry: e, Max: policy.LocalExpirationDisconnected ?? _multiLayerCacheOptions.LocalMaxExpirationDisconnected, Policy: policy), static s => s.Cache.MemorySet(s.Entry.CacheEntry, s.Entry.Value, s.Max, failSafe: s.Policy))));
            }

            return await _background.WriteAsync(
                cacheEntries.Select(c => c.CacheEntry.CacheKey.Name),
                (Cache: this, Entries: cacheEntries, Pairs: cacheKeyValuePairs, Policy: policy, KeepRefused: keepRefused, Token: token),
                null,
                static (s, _) => s.Cache.SetBatchAsync(s.Entries, s.Pairs, s.Policy, s.KeepRefused, s.Token),
                static (s, set, keeper) =>
                {
                    // Across Redis Cluster slots a write that reports failure can have written some keys, and this node does not hear
                    // its own events; dropped inside the turn, so no other write of these keys lands in between.
                    if (!set && !s.KeepRefused)
                    {
                        s.Cache.RemoveLocal(s.Entries);
                    }

                    return (set, s.Cache.KeepAfterInnerWrite(set, s.KeepRefused, s.Policy, (s.Cache, s.Entries, Keeper: keeper, s.Policy), static (x, max) => Array.TrueForAll(x.Entries, e => x.Keeper.Local(e.CacheEntry.CacheKey.Name, (x.Cache, e, max, x.Policy), static y => y.Cache.MemorySet(y.e.CacheEntry, y.e.Value, y.max, null, y.Policy))), s.Token));
                },
                token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return (false, false);
        }
        catch (Exception ex)
        {
            LogBatchSetError(ex, cacheEntries);
            return (false, false);
        }
    }

    private async ValueTask<bool> SetBatchAsync<T>(CacheEntryValue<T>[] entries, KeyValuePair<CacheKey, T?>[] pairs, CachePolicy policy, bool keepRefused, CancellationToken token)
    {
        try
        {
            // One deadline for the batch, taken from the first entry — every entry in a batch is
            // built from the same resolved expiration. With no entries there is nothing to date,
            // so the write inherits the policy.
            return entries.Length > 0
                ? await _innerCache.SetAsync<T?>(pairs, entries[0].CacheEntry.Expiration, policy, token).ConfigureAwait(false)
                : await _innerCache.SetAsync<T?>(pairs, policy, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!keepRefused && token.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex) when (keepRefused && !(ex is OperationCanceledException && token.IsCancellationRequested))
        {
            LogBatchSetError(ex, entries);
            return false;
        }
    }

    private void RemoveLocal<T>(CacheEntryValue<T>[] entries)
    {
        foreach (var entry in entries)
        {
            RemoveLocal(entry.CacheEntry.CacheKey.Name);
        }
    }

    private void LogBatchSetError<T>(Exception ex, CacheEntryValue<T>[] cacheEntries)
    {
        if (_logger.IsEnabled(LogLevel.Warning))
        {
            LogInnerCacheSetKeysError(ex, Logged(cacheEntries.Select(o => o.CacheEntry).ToArray(), typeof(T)));
        }
    }

    /// <param name="policy">The policy the value is kept under, which says whether a peer's write refreshes it; none drops it then.</param>
    /// <param name="read">Counts the entry as read now; a refresh keep passes false and inherits the read time of the copy it replaces.</param>
    /// <param name="failSafe">The policy whose fail-safe settings apply when <paramref name="policy"/> is left out so that no peer's write refreshes the entry.</param>
    /// <param name="inTurn">The call is already in the key's local turn.</param>
    private bool MemorySet<T>(CacheEntryOptions options, T value, TimeSpan? maxExpiration, ICacheEntry<T>? item = null, CachePolicy? policy = null, bool read = true, CachePolicy? failSafe = null, bool inTurn = false)
    {
        var keeps = (failSafe ?? policy)?.FailSafeEnabled == true;
        if (!inTurn && (keeps || StaleOrNull is not null))
        {
            // Fail-safe is in use: the commit and the keep or drop of the key's kept value go together, from the first such write on.
            return InLocalTurn(
                options.CacheKey.Name,
                (Cache: this, Options: options, Value: value, Max: maxExpiration, Item: item, Policy: policy, Read: read, FailSafe: failSafe),
                static s => s.Cache.MemorySet(s.Options, s.Value, s.Max, s.Item, s.Policy, s.Read, s.FailSafe, inTurn: true));
        }

        // Before the write: kept values dropped all at once from here on are newer than this value.
        var epoch = keeps ? KeepEpoch(options.TopicKey) : 0;
        if (!LateStillCurrent(options))
        {
            return false;
        }

        item ??= _cacheEntryFactory.Create(value, options.Expiration);
        var target = RefreshTarget(RefreshReader<T>.Instance, policy, item);
        Track(item, target is not null);
        if (target is not null && read)
        {
            MarkRead(item);
        }

        // Under fail-safe in a tier with a size limit, the entry's expiry is what gives its kept value a size.
        var kept = _localMemorySetter.Set(options, item, typeof(T), maxExpiration, target, out var localDeadline, out var size, keeps && StaleOrNull is { Limited: true } charged ? charged : null);
        KeepStale(options.CacheKey.Name, options.TopicKey, item, localDeadline, size, epoch, kept ? failSafe ?? policy : null);
        return kept;
    }
}
