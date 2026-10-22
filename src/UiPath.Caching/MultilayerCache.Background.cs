namespace UiPath.Caching;

internal sealed partial class MultilayerCache : IAsyncDisposable
{
    public async ValueTask DisposeAsync()
    {
        await DrainBackgroundAsync().ConfigureAwait(false);
        Dispose();
    }

    private ValueTask<bool> PublishSetAsync<T>(CacheEntryOptions options)
    {
        ValueTask<bool> publishing;
        try
        {
            publishing = _eventPublisher.CacheSetAsync(options, typeof(T));
        }
        catch (Exception ex)
        {
            LogSetEventError(ex, Logged(options, typeof(T)));
            return new(false);
        }

        // Most events complete at once (broadcast off, or a buffered write): no state machine for those.
        return publishing.IsCompletedSuccessfully ? publishing : AwaitPublishAsync<T>(publishing, options);
    }

    private async ValueTask<bool> AwaitPublishAsync<T>(ValueTask<bool> publishing, CacheEntryOptions options)
    {
        try
        {
            return await publishing.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogSetEventError(ex, Logged(options, typeof(T)));
            return false;
        }
    }

    /// <summary>Local at once for the disconnected cap; the inner write, the promotion and the event follow detached.</summary>
    private bool SetInBackground<T>(CacheEntryOptions options, T? value, CachePolicy policy)
    {
        var entry = NewEntry(options, value);
        return _background.Run(options.CacheKey.Name, (Cache: this, Options: options, Value: value, Policy: policy, Entry: entry), static s => s.Cache.MemorySet(s.Options, s.Value, s.Cache.DisconnectedLifetime(s.Policy), s.Entry), static (s, token) =>
            s.Cache.DetachedWriteAsync(s, static (s, token) => s.Cache.WriteDetachedAsync(s.Options, s.Value, s.Policy, token), static s => s.Cache.PromoteDetached(s.Options, s.Value, s.Policy, s.Entry), static s => s.Cache.PublishSetAsync<T>(s.Options), token));
    }

    private void FillInBackground<T>(CacheEntryOptions options, T? value, CachePolicy policy, ICacheEntry<T?> entry)
    {
        _background.Run(options.CacheKey.Name, (Cache: this, Options: options, Value: value, Policy: policy, Entry: entry), static s => s.Cache.MemorySet(s.Options, s.Value, s.Cache.DisconnectedLifetime(s.Policy), s.Entry), static (s, token) =>
            s.Cache.DetachedWriteAsync(s, static (s, token) => s.Cache.WriteDetachedAsync(s.Options, s.Value, s.Policy, token), static s => s.Cache.PromoteDetached(s.Options, s.Value, s.Policy, s.Entry), publish: null, token));
    }

    private ICacheEntry<T?> NewEntry<T>(CacheEntryOptions options, T? value)
    {
        try
        {
            return _cacheEntryFactory.Create<T?>(value, options.Expiration);
        }
        catch
        {
            _background.Release();
            throw;
        }
    }

    private async ValueTask<bool> WriteDetachedAsync<T>(CacheEntryOptions options, T? value, CachePolicy policy, CancellationToken token)
    {
        try
        {
            return await _innerCache.SetAsync<T?>(options.CacheKey, value, options.Expiration, policy, token).ConfigureAwait(false);
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

    private void PromoteDetached<T>(CacheEntryOptions options, T? value, CachePolicy policy, ICacheEntry<T?> entry)
    {
        ApplyLocal(options.CacheKey.Name, (Cache: this, Options: options, Value: value, Policy: policy, Entry: entry), static s =>
            s.Cache.IsLocalEntry(s.Options.CacheKey.Name, s.Entry) && s.Cache.MemorySet(s.Options, s.Value, s.Cache.ConnectedLifetime(s.Policy), s.Entry));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Set {CacheKey} is in the inner cache but its event was not published; other nodes keep their local copy until it expires.")]
    private partial void LogSetEventError(Exception ex, LoggedKey cacheKey);
}
