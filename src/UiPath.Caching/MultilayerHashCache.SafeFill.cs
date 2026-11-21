namespace UiPath.Caching;

internal sealed partial class MultilayerHashCache
{
    private enum FillState
    {
        Failed,
        Won,
        Existed,
    }

    /// <summary>Writes the generated hash only if the key is absent; answers the stored hash, kept here, when it was not.</summary>
    private async ValueTask<ICacheEntry<IDictionary<string, T?>>?> FillHashAsync<T>(InternalHashCacheEntryOptions options, IDictionary<string, T?> values, CachePolicy policy, ISharedHashTier tier)
    {
        FillState state;
        try
        {
            state = await _background.FillAsync(
                options.CacheKey.Name,
                (Cache: this, Options: options, Values: values, Policy: policy, Tier: tier),
                static (s, keeper) => s.Cache.FillInnerAsync(s.Options, s.Values, s.Policy, s.Tier, keeper),
                static (s, written, keeper) => s.Cache.KeepFilled(s.Options, s.Values, s.Policy, written, keeper),
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
            ? await AdoptStoredAsync(options, values, policy).ConfigureAwait(false)
            : null;
    }

    private async ValueTask<(FillState State, VersionedWrite Counted)> FillInnerAsync<T>(InternalHashCacheEntryOptions options, IDictionary<string, T?> values, CachePolicy policy, ISharedHashTier tier, BackgroundWrites.Keeper keeper)
    {
        VersionedWrite counted = default;
        try
        {
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

        return (counted.Written ? FillState.Won : counted.Existed ? FillState.Existed : FillState.Failed, counted);
    }

    private FillState KeepFilled<T>(InternalHashCacheEntryOptions options, IDictionary<string, T?> values, CachePolicy policy, (FillState State, VersionedWrite Counted) written, BackgroundWrites.Keeper keeper)
    {
        var shape = new HashShape<T>(this);
        switch (written.State)
        {
            case FillState.Won:
                shape.KeepWritten(keeper, options, values, ConnectedLifetime(policy), null, _versioned is null ? 0 : written.Counted.Version, policy);
                break;
            case FillState.Failed when !options.Token.IsCancellationRequested:
                // As a refused write is kept: for the disconnected cap.
                shape.KeepWritten(keeper, options, values, DisconnectedLifetime(policy), null, 0, policy);
                break;
        }

        return written.State;
    }

    private async ValueTask<ICacheEntry<IDictionary<string, T?>>?> AdoptStoredAsync<T>(InternalHashCacheEntryOptions options, IDictionary<string, T?> values, CachePolicy policy)
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
            // A field no serializer reads would stay unreadable under a fill that only writes an absent key, so the hash is replaced as it always was.
            await Layered<T>().InternalSetAsync(options, values, false, policy, keepRefused: true).ConfigureAwait(false);
            return null;
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

    private async ValueTask<bool> FillDetachedAsync<T>(InternalHashCacheEntryOptions options, IDictionary<string, T?> values, CachePolicy policy, LocalEntry<T> local, CancellationToken token)
    {
        if (_innerCache is not ISharedHashTier tier)
        {
            return await WriteDetachedAsync(options, values, policy, local, token).ConfigureAwait(false);
        }

        try
        {
            var counted = await tier.FillVersionedAsync<T?>(options.CacheKey, values, new HashCacheEntryOptions(options.Expiration, null, options.Metadata, options.SetOption), policy, _peerLimits, token).ConfigureAwait(false);
            local.Counted = counted;
            Returned(options.CacheKey.Name, local.Pending, counted.Written ? counted.Version : 0);
            if (counted.Existed)
            {
                // The stored hash is not the one kept ahead of the write: dropped, so the next read takes it from the inner tier.
                ApplyLocal(options.CacheKey.Name, (Cache: this, Name: options.CacheKey.Name, Entry: local.Entry), static s => s.Entry is not null && s.Cache.IsLocalEntry(s.Name, s.Entry) && s.Cache.RemoveHeld(s.Name));
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
