namespace UiPath.Caching;

internal sealed partial class MultilayerCache : IAsyncDisposable
{
    public async ValueTask DisposeAsync()
    {
        await DrainBackgroundAsync().ConfigureAwait(false);
        Dispose();
    }

    /// <summary>Local at once for the disconnected cap; the inner write, the promotion and the event follow detached.</summary>
    private bool SetInBackground<T>(CacheEntryOptions options, T? value, CachePolicy policy)
    {
        var entry = NewEntry(options, value);
        return _background.Run(options.CacheKey.Name, (Cache: this, Options: options, Value: value, Policy: policy, Entry: entry), static s => s.Cache.MemorySet(s.Options, s.Value, s.Cache.DisconnectedLifetime(s.Policy), s.Entry), static (s, token) =>
            s.Cache.DetachedWriteAsync(s, static (s, token) => s.Cache.WriteDetachedAsync(s.Options, s.Value, s.Policy, token), static s => s.Cache.PromoteDetached(s.Options, s.Value, s.Policy, s.Entry), static s => s.Cache.Layered<T>().PublishSetAsync(s.Options), token));
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
            s.Cache.IsLocalEntry(s.Options.CacheKey.Name, s.Entry) && s.Cache.MemorySet(s.Options, s.Value, s.Cache.ConnectedLifetime(s.Policy), s.Entry, s.Policy));
    }

}
