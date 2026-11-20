namespace UiPath.Caching;

internal sealed partial class MultilayerHashCache
{
    [OverloadResolutionPriority(1)]
    public async ValueTask<bool> ContainsAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        if (!token.IsCancellationRequested && TryGetLocal<T>(cacheKey, out _))
        {
            return true;
        }

        var cacheEntryOptions = _entryBuilder.BuildEntryOptions<T>(cacheKey, default, token: token);
        try
        {
            return _memoryCache.TryGetValue(cacheEntryOptions.CacheKey.Name, out _) || await _innerCache.ContainsAsync<T>(cacheEntryOptions.CacheKey, cacheEntryOptions.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && cacheEntryOptions.Token.IsCancellationRequested))
        {
            LogInnerCacheContainsError(ex, Logged(cacheEntryOptions, typeof(T)));
            return false;
        }
    }

    public ValueTask<bool> ContainsAsync<T>(Span<char> cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return !token.IsCancellationRequested && TryGetLocal<T>(cacheKey, out _)
            ? new ValueTask<bool>(true)
            : ContainsAsync<T>(new CacheKey(cacheKey), token);
    }

    public async ValueTask<TimeSpan?> TimeToLiveAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var cacheEntryOptions = _entryBuilder.BuildEntryOptions<T>(cacheKey, default, token: token);
        return _memoryCache.TryGetValue<ICacheEntry>(cacheEntryOptions.CacheKey.Name, out var value)
            ? value?.Expiration.Subtract(_clock.GetUtcNow())
            : await _innerCache.TimeToLiveAsync<T>(cacheEntryOptions.CacheKey, token);
    }

    public ValueTask<TimeSpan?> TimeToLiveAsync<T>(Span<char> cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return !token.IsCancellationRequested && TryGetLocal<T>(cacheKey, out var local)
            ? new ValueTask<TimeSpan?>(local.Expiration.Subtract(_clock.GetUtcNow()))
            : TimeToLiveAsync<T>(new CacheKey(cacheKey), token);
    }

    public async ValueTask<DateTimeOffset?> ExpireTimeAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var cacheEntryOptions = _entryBuilder.BuildEntryOptions<T>(cacheKey, default, token: token);
        return _memoryCache.TryGetValue<ICacheEntry>(cacheEntryOptions.CacheKey.Name, out var value)
            ? value?.Expiration
            : await _innerCache.ExpireTimeAsync<T>(cacheEntryOptions.CacheKey, token);
    }

    public ValueTask<DateTimeOffset?> ExpireTimeAsync<T>(Span<char> cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return !token.IsCancellationRequested && TryGetLocal<T>(cacheKey, out var local)
            ? new ValueTask<DateTimeOffset?>(local.Expiration)
            : ExpireTimeAsync<T>(new CacheKey(cacheKey), token);
    }

    public async ValueTask<IDictionary<string, string?>?> GetMetadataAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var options = _entryBuilder.BuildEntryOptions<T>(cacheKey, _clock.ToDateTimeOffset(_multiLayerCacheOptions.DefaultExpiration), token: token);
        return _memoryCache.TryGetValue<ICacheEntry>(options.CacheKey.Name, out var entry)
            ? (entry?.Metadata)
            : await _innerCache.GetMetadataAsync<T>(options.CacheKey, token).ConfigureAwait(false);
    }

    public ValueTask<IDictionary<string, string?>?> GetMetadataAsync<T>(Span<char> cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return !token.IsCancellationRequested && TryGetLocal<T>(cacheKey, out var local)
            ? new ValueTask<IDictionary<string, string?>?>(local.Metadata)
            : GetMetadataAsync<T>(new CacheKey(cacheKey), token);
    }

    public async ValueTask<bool> SetMetadataAsync<T>(CacheKey cacheKey, IDictionary<string, string?> metadata, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var cacheEntryOptions = _entryBuilder.BuildEntryOptions<T>(cacheKey, token);
        LogSetMetadata(Logged(cacheEntryOptions, typeof(T)));
        try
        {
            // Released before the event; the local removal of a failure stays inside it.
            var updated = await _background.WriteAsync(
                cacheEntryOptions.CacheKey.Name,
                (Cache: this, Options: cacheEntryOptions, Metadata: metadata),
                null,
                static async s =>
                {
                    try
                    {
                        if (!await s.Cache._innerCache.SetMetadataAsync<T>(s.Options.CacheKey, s.Metadata, s.Options.Token).ConfigureAwait(false))
                        {
                            s.Cache.LogInnerCacheSetMetadataFailed(s.Cache.Logged(s.Options, typeof(T)));
                            return false;
                        }

                        return true;
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException && s.Options.Token.IsCancellationRequested))
                    {
                        s.Cache.RemoveLocal(s.Options.CacheKey.Name);
                        s.Cache.LogInnerCacheRefreshError(ex, s.Cache.Logged(s.Options, typeof(T)));
                        return false;
                    }
                },
                cacheEntryOptions.Token).ConfigureAwait(false);
            if (!updated)
            {
                return false;
            }

            if(_memoryCache.TryGetValue<ICacheEntry>(cacheEntryOptions.CacheKey.Name, out var entry) && entry != null)
            {
                cacheEntryOptions.Expiration = entry.Expiration;
            }
            else
            {
                var expiration = await _innerCache.ExpireTimeAsync<T>(cacheEntryOptions.CacheKey, cacheEntryOptions.Token).ConfigureAwait(false);
                cacheEntryOptions.Expiration = _clock.ToDateTimeOffset(expiration);
            }

            cacheEntryOptions.Metadata = metadata;
            return await _eventPublisher.MetadataUpdatedAsync(cacheEntryOptions, typeof(T)).ConfigureAwait(false);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && cacheEntryOptions.Token.IsCancellationRequested))
        {
            RemoveLocal(cacheEntryOptions.CacheKey.Name);
            LogInnerCacheRefreshError(ex, Logged(cacheEntryOptions, typeof(T)));
            return false;
        }
    }
}
