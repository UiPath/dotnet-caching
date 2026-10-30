namespace UiPath.Caching;

internal sealed partial class MultilayerHashCache : IAsyncDisposable
{
    public async ValueTask DisposeAsync()
    {
        await DrainBackgroundAsync().ConfigureAwait(false);
        Dispose();
    }

    /// <summary>Local at once for the disconnected cap; the inner write, the promotion and the event follow detached.</summary>
    private bool SetInBackground<T>(InternalHashCacheEntryOptions options, IDictionary<string, T?> values, CachePolicy policy)
    {
        var local = new LocalEntry<T>();
        if (_versioned is not null)
        {
            local.Pending = new OwnWrite([.. values.Keys], replace: !options.Partial);
        }

        if (!options.Partial)
        {
            local.Entry = NewEntry(options, values);
        }

        return _background.Run(options.CacheKey.Name, (Cache: this, Options: options, Value: values, Policy: policy, Local: local), static s => s.Cache.KeepBackground(s.Options, s.Value, s.Policy, s.Local), static (s, token) =>
            s.Cache.DetachedWriteAsync(s, static (s, token) => s.Cache.WriteDetachedAsync(s.Options, s.Value, s.Policy, s.Local, token), static s => s.Cache.PromoteDetached(s.Options, s.Policy, s.Local), static s => s.Cache.Layered<T>().PublishSetAsync(s.Options, s.Local.Counted), token));
    }

    /// <summary>A merge builds it under the merge lock from the hash held here, or leaves none.</summary>
    private bool KeepBackground<T>(InternalHashCacheEntryOptions options, IDictionary<string, T?> values, CachePolicy policy, LocalEntry<T> local)
    {
        if (!options.Partial)
        {
            options.Own = local.Pending is null ? null : [local.Pending];
            return MemorySet(options, local.Entry!, DisconnectedLifetime(policy));
        }

        options.Pending = local.Pending;
        lock (MergeLock(options.CacheKey.Name))
        {
            if (TryMergeLocal(ref options, values, out var merged) || OnlyTier)
            {
                local.Entry = CreateEntry(merged ?? values, options);
                return MemorySet(options, local.Entry, DisconnectedLifetime(policy));
            }
        }

        DropAwaitingRefresh(options.CacheKey.Name);
        return true;
    }

    private void FillInBackground<T>(InternalHashCacheEntryOptions options, IDictionary<string, T?> values, CachePolicy policy)
    {
        var local = new LocalEntry<T> { Entry = NewEntry(options, values) };
        if (_versioned is not null)
        {
            local.Pending = new OwnWrite([.. values.Keys], replace: true);
            options.Own = [local.Pending];
        }

        _background.Run(options.CacheKey.Name, (Cache: this, Options: options, Value: values, Policy: policy, Local: local), static s => s.Cache.MemorySet(s.Options, s.Local.Entry!, s.Cache.DisconnectedLifetime(s.Policy)), static (s, token) =>
            s.Cache.DetachedWriteAsync(s, static (s, token) => s.Cache.WriteDetachedAsync(s.Options, s.Value, s.Policy, s.Local, token), static s => s.Cache.PromoteDetached(s.Options, s.Policy, s.Local), publish: null, token));
    }

    private ICacheEntry<IDictionary<string, T?>> NewEntry<T>(InternalHashCacheEntryOptions options, IDictionary<string, T?> values)
    {
        try
        {
            return CreateEntry(values, options);
        }
        catch
        {
            _background.Release();
            throw;
        }
    }

    private async ValueTask<bool> WriteDetachedAsync<T>(InternalHashCacheEntryOptions options, IDictionary<string, T?> values, CachePolicy policy, LocalEntry<T> local, CancellationToken token)
    {
        try
        {
            if (_versioned is { } versioned)
            {
                local.Counted = await versioned.SetVersionedAsync<T?>(options.CacheKey, values, new HashCacheEntryOptions(options.Expiration, null, options.Metadata, options.SetOption), policy, _peerLimits, token).ConfigureAwait(false);
                Returned(options.CacheKey.Name, local.Pending, local.Counted.Written ? local.Counted.Version : 0);
                return local.Counted.Written;
            }

            return await _innerCache.SetAsync<T?>(options.CacheKey, values, new HashCacheEntryOptions(options.Expiration, null, options.Metadata, options.SetOption), policy, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Returned(options.CacheKey.Name, local.Pending, 0);
            throw;
        }
        catch (Exception ex)
        {
            Returned(options.CacheKey.Name, local.Pending, 0);
            LogInnerCacheSetError(ex, Logged(options, typeof(T)));
            return false;
        }
    }

    /// <summary>Tells every entry that holds the write's fields which version it took; a write with none left them standing on nothing.</summary>
    private void Returned(string name, OwnWrite? write, long version)
    {
        if (write is null)
        {
            return;
        }

        lock (MergeLock(name))
        {
            if (version > 0)
            {
                write.Version = version;
            }
            else
            {
                write.Failed = true;
            }
        }
    }

    private void PromoteDetached<T>(InternalHashCacheEntryOptions options, CachePolicy policy, LocalEntry<T> local)
    {
        ApplyLocal(options.CacheKey.Name, (Cache: this, Options: options, Policy: policy, local.Entry), static s =>
            s.Entry is not null && s.Cache.PromoteLocal(s.Options, s.Entry, s.Cache.ConnectedLifetime(s.Policy), s.Policy));
    }

    // The entry is still the held one, and a peer's update waits, for as long as the lock is held.
    private bool PromoteLocal<T>(InternalHashCacheEntryOptions options, ICacheEntry<IDictionary<string, T?>> entry, TimeSpan? maxExpiration, CachePolicy policy)
    {
        lock (MergeLock(options.CacheKey.Name))
        {
            if (!IsLocalEntry(options.CacheKey.Name, entry))
            {
                return false;
            }

            // The entry keeps what it stands on, now that the write it waited on has told its version.
            options.Version = 0;
            options.Own = null;
            if (HashVersions.TryGet(entry, out var stamp) && stamp.TryResolve(out var number, out var ahead))
            {
                options.Version = number;
                options.Own = ahead;
            }

            return MemorySet(options, entry, maxExpiration, policy);
        }
    }

    private sealed class LocalEntry<T>
    {
        public ICacheEntry<IDictionary<string, T?>>? Entry { get; set; }

        public VersionedWrite Counted { get; set; }

        public OwnWrite? Pending { get; set; }
    }
}
