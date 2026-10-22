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
        var entry = NewEntry(options, values);
        return _background.Run(options.CacheKey.Name, (Cache: this, Options: options, Value: values, Policy: policy, Entry: entry), static s => s.Cache.MemorySet(s.Options, s.Entry, s.Cache.DisconnectedLifetime(s.Policy)), static (s, token) =>
            s.Cache.DetachedWriteAsync(s, static (s, token) => s.Cache.WriteDetachedAsync(s.Options, s.Value, s.Policy, token), static s => s.Cache.PromoteDetached(s.Options, s.Policy, s.Entry), static s => s.Cache.PublishSetAsync<T>(s.Options), token));
    }

    private void FillInBackground<T>(InternalHashCacheEntryOptions options, IDictionary<string, T?> values, CachePolicy policy)
    {
        var entry = NewEntry(options, values);
        _background.Run(options.CacheKey.Name, (Cache: this, Options: options, Value: values, Policy: policy, Entry: entry), static s => s.Cache.MemorySet(s.Options, s.Entry, s.Cache.DisconnectedLifetime(s.Policy)), static (s, token) =>
            s.Cache.DetachedWriteAsync(s, static (s, token) => s.Cache.WriteDetachedAsync(s.Options, s.Value, s.Policy, token), static s => s.Cache.PromoteDetached(s.Options, s.Policy, s.Entry), publish: null, token));
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

    private async ValueTask<bool> WriteDetachedAsync<T>(InternalHashCacheEntryOptions options, IDictionary<string, T?> values, CachePolicy policy, CancellationToken token)
    {
        try
        {
            return await _innerCache.SetAsync<T?>(options.CacheKey, values, new HashCacheEntryOptions(options.Expiration, null, options.Metadata, options.SetOption), policy, token).ConfigureAwait(false);
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

    private void PromoteDetached<T>(InternalHashCacheEntryOptions options, CachePolicy policy, ICacheEntry<IDictionary<string, T?>> entry)
    {
        ApplyLocal(options.CacheKey.Name, (Cache: this, Options: options, Policy: policy, Entry: entry), static s =>
            s.Cache.IsLocalEntry(s.Options.CacheKey.Name, s.Entry) && s.Cache.MemorySet(s.Options, s.Entry, s.Cache.ConnectedLifetime(s.Policy)));
    }
}
