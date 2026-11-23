namespace UiPath.Caching;

internal sealed partial class MultilayerHashCache
{
    private enum FillState
    {
        Failed,
        Won,
        Existed,
        Superseded,
    }

    /// <summary>Listens for peers' writes of the key and watches it for changes until <see cref="EndFill"/>; untracked with no inner tier to fill.</summary>
    private long BeginFill(InternalHashCacheEntryOptions options)
    {
        if (!_sharedInner)
        {
            return BackgroundWrites.Untracked;
        }

        MarkInnerRead(options.CacheKey.Name, options.TopicKey);
        return _background.BeginFill(options.CacheKey.Name);
    }

    private void EndFill(string name, long fill)
    {
        if (fill != BackgroundWrites.Untracked)
        {
            _background.EndFill(name);
        }
    }

    /// <summary>Writes the generated hash only if the key is absent; answers the stored hash, kept here, when it was not.</summary>
    private async ValueTask<ICacheEntry<IDictionary<string, T?>>?> FillHashAsync<T>(InternalHashCacheEntryOptions options, IDictionary<string, T?> values, CachePolicy policy, ISharedHashTier tier, long fill, bool healed = false)
    {
        FillState state;
        try
        {
            state = await _background.FillAsync(
                options.CacheKey.Name,
                (Cache: this, Options: options, Values: values, Policy: policy, Tier: tier, Fill: fill),
                static (s, keeper) => s.Cache.FillInnerAsync(s.Options, s.Values, s.Policy, s.Tier, s.Fill, keeper),
                static (s, written, keeper) => s.Cache.KeepFilled(s.Options, s.Values, s.Policy, s.Fill, written, keeper),
                options.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (options.Token.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            LogInnerCacheSetError(ex, Logged(options, typeof(T)));
            return null;
        }

        return state == FillState.Existed
            ? await AdoptStoredAsync(options, values, policy, tier, fill, healed).ConfigureAwait(false)
            : null;
    }

    private async ValueTask<(FillState State, VersionedWrite Counted)> FillInnerAsync<T>(InternalHashCacheEntryOptions options, IDictionary<string, T?> values, CachePolicy policy, ISharedHashTier tier, long fill, BackgroundWrites.Keeper keeper)
    {
        VersionedWrite counted = default;
        try
        {
            // Just before the command: a write or removal since the generator started leaves the key to it.
            if (!_background.FillIntact(options.CacheKey.Name, fill))
            {
                return (FillState.Superseded, default);
            }

            counted = await tier.FillVersionedAsync<T?>(options.CacheKey, values, new HashCacheEntryOptions(options.Expiration, null, options.Metadata, options.SetOption), policy, _peerLimits, options.Token).ConfigureAwait(false);
            if (_versioned is not null)
            {
                keeper.Returned(options.CacheKey.Name, counted.Written ? counted.Version : 0);
            }
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && options.Token.IsCancellationRequested))
        {
            LogInnerCacheSetError(ex, Logged(options, typeof(T)));
        }

        if (counted.Written)
        {
            return (FillState.Won, counted);
        }

        return (counted.Existed ? FillState.Existed : FillState.Failed, counted);
    }

    private FillState KeepFilled<T>(InternalHashCacheEntryOptions options, IDictionary<string, T?> values, CachePolicy policy, long fill, (FillState State, VersionedWrite Counted) written, BackgroundWrites.Keeper keeper)
    {
        if (written.State == FillState.Superseded)
        {
            _background.FillSuperseded();
            return written.State;
        }

        switch (written.State)
        {
            case FillState.Won:
                KeepIfIntact(keeper, options, values, ConnectedLifetime(policy), _versioned is null ? 0 : written.Counted.Version, policy, fill);
                break;
            case FillState.Failed when !options.Token.IsCancellationRequested:
                // As a refused write is kept: for the disconnected cap.
                KeepIfIntact(keeper, options, values, DisconnectedLifetime(policy), 0, policy, fill);
                break;
        }

        return written.State;
    }

    /// <summary>The check and the keep are one step under the key's exclusion, so a removal cannot land between them.</summary>
    private void KeepIfIntact<T>(BackgroundWrites.Keeper keeper, InternalHashCacheEntryOptions options, IDictionary<string, T?> values, TimeSpan? max, long version, CachePolicy policy, long fill)
    {
        options.Version = version;
        keeper.Local(
            options.CacheKey.Name,
            (Cache: this, Options: options, Values: values, Max: max, Policy: policy, Fill: fill),
            static s =>
            {
                if (!s.Cache._background.FillIntact(s.Options.CacheKey.Name, s.Fill))
                {
                    s.Cache._background.FillSuperseded();
                    return false;
                }

                return s.Cache.MemorySet(s.Options, s.Values, s.Max, policy: s.Policy, read: true);
            },
            static s => s.Cache.DropSuperseded(s.Options.CacheKey.Name),
            options.Version);
    }

    private async ValueTask<ICacheEntry<IDictionary<string, T?>>?> AdoptStoredAsync<T>(InternalHashCacheEntryOptions options, IDictionary<string, T?> values, CachePolicy policy, ISharedHashTier tier, long fill, bool healed)
    {
        var shape = new HashShape<T>(this);
        // After the fill's own write, which counts as a change of the key, and before the read that no event may hide behind.
        var mark = MarkInnerRead(options.CacheKey.Name, options.TopicKey);
        ICacheEntry<IDictionary<string, T?>> stored;
        try
        {
            stored = await shape.ReadForAdoptionAsync(options, policy, new StampBox()).ConfigureAwait(false);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && options.Token.IsCancellationRequested))
        {
            LogInnerCacheSetError(ex, Logged(options, typeof(T)));
            return null;
        }

        if (stored is UnreadableEntry<IDictionary<string, T?>>)
        {
            // A field no serializer reads would stay unreadable under a fill that only writes an absent key: it is removed, only while it is what was read, and the fill goes once more.
            return !healed && _background.FillIntact(options.CacheKey.Name, fill) && await RemoveUnreadableAsync<T>(options, policy, tier).ConfigureAwait(false)
                ? await FillHashAsync(options, values, policy, tier, fill, healed: true).ConfigureAwait(false)
                : null;
        }

        if (!stored.Found)
        {
            return null;
        }

        var adopted = options;
        shape.Adopt(ref adopted, stored);
        Layered<T>().FillFromInnerRead(adopted, mark, stored.Value ?? Empty<T>(), policy);
        return stored;
    }

    private async ValueTask<bool> RemoveUnreadableAsync<T>(InternalHashCacheEntryOptions options, CachePolicy policy, ISharedHashTier tier)
    {
        try
        {
            return await tier.RemoveIfUnreadableAsync<T>(options.CacheKey, policy, options.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && options.Token.IsCancellationRequested))
        {
            LogInnerCacheSetError(ex, Logged(options, typeof(T)));
            return false;
        }
    }

    private async ValueTask<bool> FillDetachedAsync<T>(InternalHashCacheEntryOptions options, IDictionary<string, T?> values, CachePolicy policy, LocalEntry<T> local, long fill, CancellationToken token)
    {
        try
        {
            return await FillDetachedCoreAsync(options, values, policy, local, fill, token).ConfigureAwait(false);
        }
        finally
        {
            EndFill(options.CacheKey.Name, fill);
        }
    }

    private async ValueTask<bool> FillDetachedCoreAsync<T>(InternalHashCacheEntryOptions options, IDictionary<string, T?> values, CachePolicy policy, LocalEntry<T> local, long fill, CancellationToken token)
    {
        if (_innerCache is not ISharedHashTier tier)
        {
            return await WriteDetachedAsync(options, values, policy, local, token).ConfigureAwait(false);
        }

        try
        {
            // The key may have changed while the write waited in the queue.
            if (!_background.FillIntact(options.CacheKey.Name, fill))
            {
                _background.FillSuperseded();
                Returned(options.CacheKey.Name, local.Pending, 0);
                return false;
            }

            var counted = await tier.FillVersionedAsync<T?>(options.CacheKey, values, new HashCacheEntryOptions(options.Expiration, null, options.Metadata, options.SetOption), policy, _peerLimits, token).ConfigureAwait(false);
            local.Counted = counted;
            Returned(options.CacheKey.Name, local.Pending, counted.Written ? counted.Version : 0);
            if (counted.Existed)
            {
                // The stored hash is not the one kept ahead of the write: dropped, so the next read takes it from the inner tier.
                ApplyLocal(options.CacheKey.Name, (Cache: this, options.CacheKey.Name, local.Entry), static s => s.Entry is not null && s.Cache.IsLocalEntry(s.Name, s.Entry) && s.Cache.RemoveHeld(s.Name));
                return true;
            }

            return counted.Written;
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

    private bool RemoveHeld(string name)
    {
        RemoveEntry(name);
        return true;
    }
}
