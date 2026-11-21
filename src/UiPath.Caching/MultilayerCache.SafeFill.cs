namespace UiPath.Caching;

internal sealed partial class MultilayerCache
{
    private static long BatchToken(Dictionary<string, long> watched, string name) =>
        watched.TryGetValue(name, out var token) ? token : BackgroundWrites.Untracked;

    /// <summary>Listens for peers' writes of the key and watches it for changes until <see cref="EndFill"/>; none with no inner tier to fill.</summary>
    private GenerationScope BeginFill(CacheEntryOptions options, GenerationScope scope, bool localOnly)
    {
        if (localOnly || !_sharedInner)
        {
            return scope;
        }

        MarkInnerRead(options.CacheKey.Name, options.TopicKey);
        return scope with { Fill = _background.BeginFill(options.CacheKey.Name) };
    }

    private void EndFill(string name, in GenerationScope scope)
    {
        if (scope.Fill != BackgroundWrites.Untracked)
        {
            _background.EndFill(name);
        }
    }

    /// <summary>Writes the generated value only if the key is absent, and answers with the stored value when it was not.</summary>
    private async ValueTask<Filled<T>> FillInnerAsync<T>(CacheEntryOptions options, T? value, CachePolicy policy, ICacheEntry<T?> entry, long fill)
    {
        try
        {
            return await _background.FillAsync(
                options.CacheKey.Name,
                new FillArgs<T>(this, options, value, policy, entry, fill),
                static (s, _) => s.Cache.TryFillOrReplaceAsync(s.Options, s.Value, s.Policy),
                static (s, filled, keeper) => s.Cache.KeepFilled(s, filled, keeper),
                options.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (options.Token.IsCancellationRequested)
        {
            return default;
        }
        catch (Exception ex)
        {
            LogInnerCacheSetError(ex, Logged(options, typeof(T)));
            return default;
        }
    }

    [AsyncMethodBuilder(typeof(Pooled<>))]
    private async ValueTask<FillResult<T>> TryFillOrReplaceAsync<T>(CacheEntryOptions options, T? value, CachePolicy policy)
    {
        try
        {
            // A late fail-safe value checks in the key's turn that nothing removed or replaced the key since its generator started.
            if (!LateStillCurrent(options))
            {
                return default;
            }

            var filled = await _innerCache.TryFillAsync<T>(options.CacheKey, value, options.Expiration, policy, options.Token).ConfigureAwait(false);
            if (filled.Outcome != FillOutcome.Unreadable)
            {
                return filled;
            }

            // A stored value no serializer reads is replaced, as a write over it always did: a fill that left it would never heal it.
            return await _innerCache.SetAsync<T?>(options.CacheKey, value, options.Expiration, policy, options.Token).ConfigureAwait(false)
                ? new FillResult<T>(FillOutcome.Added, default)
                : default;
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && options.Token.IsCancellationRequested))
        {
            LogInnerCacheSetError(ex, Logged(options, typeof(T)));
            return default;
        }
    }

    private Filled<T> KeepFilled<T>(FillArgs<T> s, FillResult<T> filled, BackgroundWrites.Keeper keeper)
    {
        switch (filled.Outcome)
        {
            case FillOutcome.Added:
                KeepIfIntact(keeper, s, s.Options, s.Value, s.Entry, ConnectedLifetime(s.Policy));
                return new(null, true);
            case FillOutcome.Existed:
                var deadline = filled.Expiration ?? s.Options.Expiration;
                var adopted = _cacheEntryFactory.Create<T?>(filled.Existing, deadline);
                if (filled.Expiration is not null && deadline > _clock.GetUtcNow())
                {
                    KeepIfIntact(keeper, s, s.Options with { Expiration = deadline }, filled.Existing, adopted, ConnectedLifetime(s.Policy));
                }

                return new(adopted, false);
            default:
                // As a refused write is kept: for the disconnected cap, unless the caller left.
                if (!s.Options.Token.IsCancellationRequested)
                {
                    KeepIfIntact(keeper, s, s.Options, s.Value, s.Entry, DisconnectedLifetime(s.Policy));
                }

                return default;
        }
    }

    private void KeepIfIntact<T>(BackgroundWrites.Keeper keeper, FillArgs<T> s, CacheEntryOptions options, T? value, ICacheEntry<T?>? entry, TimeSpan? max) =>
        keeper.Local(
            options.CacheKey.Name,
            (Cache: this, Options: options, Value: value, Entry: entry, Max: max, s.Policy, s.Fill),
            static x =>
            {
                if (!x.Cache._background.FillIntact(x.Options.CacheKey.Name, x.Fill))
                {
                    x.Cache._background.FillSuperseded();
                    return false;
                }

                return x.Cache.MemorySet(x.Options, x.Value, x.Max, x.Entry, x.Policy, read: true);
            });

    /// <summary>The watch tokens of the keys a batch generates, by key; empty with no inner tier to fill.</summary>
    private Dictionary<string, long> BeginBatchFill(CacheEntryOptions[] missOptions, List<int> stillMissing)
    {
        var watched = new Dictionary<string, long>(stillMissing.Count, StringComparer.Ordinal);
        if (!_sharedInner)
        {
            return watched;
        }

        MarkInnerRead(missOptions[stillMissing[0]].CacheKey.Name, missOptions[stillMissing[0]].TopicKey);
        foreach (var index in stillMissing)
        {
            var name = missOptions[index].CacheKey.Name;
            watched[name] = _background.BeginFill(name);
        }

        return watched;
    }

    private void EndBatchFill(Dictionary<string, long> watched)
    {
        foreach (var name in watched.Keys)
        {
            _background.EndFill(name);
        }
    }

    /// <summary>Writes each key only if it is absent, the keys independently; answers the stored entries of those that were not.</summary>
    private async ValueTask<Dictionary<string, ICacheEntry<T?>>?> FillBatchAsync<T>(CacheEntryValue<T>[] entries, Dictionary<string, long> watched, CachePolicy policy, CancellationToken token)
    {
        if (entries.Length == 0)
        {
            return null;
        }

        try
        {
            return await _background.FillAsync(
                entries.Select(static e => e.CacheEntry.CacheKey.Name),
                (Cache: this, Entries: entries, Watched: watched, Policy: policy),
                static (s, _) => s.Cache.TryFillEachAsync(s.Entries, s.Policy),
                static (s, results, keeper) => s.Cache.KeepFilledBatch(s.Entries, s.Watched, s.Policy, results, keeper),
                token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            LogBatchSetError(ex, entries);
            return null;
        }
    }

    private async ValueTask<FillResult<T>[]> TryFillEachAsync<T>(CacheEntryValue<T>[] entries, CachePolicy policy)
    {
        var fills = new Task<FillResult<T>>[entries.Length];
        for (var i = 0; i < entries.Length; i++)
        {
            fills[i] = TryFillOrReplaceAsync(entries[i].CacheEntry, entries[i].Value, policy).AsTask();
        }

        return await Task.WhenAll(fills).ConfigureAwait(false);
    }

    private Dictionary<string, ICacheEntry<T?>>? KeepFilledBatch<T>(CacheEntryValue<T>[] entries, Dictionary<string, long> watched, CachePolicy policy, FillResult<T>[] results, BackgroundWrites.Keeper keeper)
    {
        Dictionary<string, ICacheEntry<T?>>? adopted = null;
        for (var i = 0; i < entries.Length; i++)
        {
            var options = entries[i].CacheEntry;
            var kept = KeepFilled(new FillArgs<T>(this, options, entries[i].Value, policy, null, BatchToken(watched, options.CacheKey.Name)), results[i], keeper);
            if (kept.Adopted is not null)
            {
                (adopted ??= new Dictionary<string, ICacheEntry<T?>>(StringComparer.Ordinal))[options.CacheKey.Name] = kept.Adopted;
            }
        }

        return adopted;
    }
    private async ValueTask<bool> FillDetachedAsync<T>(CacheEntryOptions options, T? value, CachePolicy policy, ICacheEntry<T?> entry, CancellationToken token)
    {
        try
        {
            var filled = await TryFillOrReplaceAsync(options, value, policy).ConfigureAwait(false);
            switch (filled.Outcome)
            {
                case FillOutcome.Added:
                    return true;
                case FillOutcome.Existed:
                    SwapForStored(options, policy, entry, filled);
                    return true;
                default:
                    return false;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogInnerCacheSetError(ex, Logged(options, typeof(T)));
            return false;
        }
    }

    /// <summary>Puts the stored value in place of the generated one this node kept ahead of the write, unless a change of the key since has taken its place.</summary>
    private void SwapForStored<T>(CacheEntryOptions options, CachePolicy policy, ICacheEntry<T?> kept, FillResult<T> filled)
    {
        var name = options.CacheKey.Name;
        if (filled.Expiration is not { } deadline || deadline <= _clock.GetUtcNow())
        {
            ApplyLocal(name, (Cache: this, Name: name, Kept: kept), static s => s.Cache.IsLocalEntry(s.Name, s.Kept) && s.Cache.RemoveHeld(s.Name));
            return;
        }

        var stored = _cacheEntryFactory.Create<T?>(filled.Existing, deadline);
        ApplyLocal(name, (Cache: this, Options: options with { Expiration = deadline }, Value: filled.Existing, Policy: policy, Kept: kept, Stored: stored), static s =>
            s.Cache.IsLocalEntry(s.Options.CacheKey.Name, s.Kept) && s.Cache.MemorySet(s.Options, s.Value, s.Cache.ConnectedLifetime(s.Policy), s.Stored, s.Policy, read: true));
    }

    private bool RemoveHeld(string name)
    {
        RemoveEntry(name);
        return true;
    }

    private readonly record struct FillArgs<T>(MultilayerCache Cache, CacheEntryOptions Options, T? Value, CachePolicy Policy, ICacheEntry<T?>? Entry, long Fill);

    private readonly record struct Filled<T>(ICacheEntry<T?>? Adopted, bool Won);
}
