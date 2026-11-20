using System.Collections.Immutable;

namespace UiPath.Caching;

internal sealed partial class MultilayerHashCache
{
    [OverloadResolutionPriority(1)]
    public async ValueTask<T?> GetItemAsync<T>(CacheKey cacheKey, string field, CachePolicy? policy, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        if (!token.IsCancellationRequested && TryGetLocal<T>(cacheKey, out var local) && TryGetItem(local, field, out var item))
        {
            return item;
        }

        policy ??= _defaultPolicy;
        var cacheEntry = await GetCacheEntryAsync<T>(_entryBuilder.BuildEntryOptions<T>(cacheKey, new[] { field }, default, token: token), policy);
        if (cacheEntry.Value == null)
        {
            return default;
        }

        return cacheEntry.Value.TryGetValue(field, out var value) ? value : default;
    }

    public ValueTask<T?> GetItemAsync<T>(Span<char> cacheKey, string field, CachePolicy? policy, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        // The hash key path observes cancellation first, before the key and the strategy.
        token.ThrowIfCancellationRequested();
        if (TryGetLocal<T>(cacheKey, out var local) && TryGetItem(local, field, out var item))
        {
            return new ValueTask<T?>(item);
        }

        return GetItemAsync<T>(new CacheKey(cacheKey), field, policy, token);
    }

    [OverloadResolutionPriority(1)]
    public async ValueTask<IDictionary<string, T?>> GetAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        if (!token.IsCancellationRequested && TryGetLocal<T>(cacheKey, out var local))
        {
            return local.Value ?? Empty<T>();
        }

        policy ??= _defaultPolicy;
        var cacheEntry = await GetCacheEntryAsync<T>(_entryBuilder.BuildEntryOptions<T>(cacheKey, token), policy);
        return cacheEntry.Value ?? Empty<T>();
    }

    public ValueTask<IDictionary<string, T?>> GetAsync<T>(Span<char> cacheKey, CachePolicy? policy, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        token.ThrowIfCancellationRequested();
        if (TryGetLocal<T>(cacheKey, out var local))
        {
            return new ValueTask<IDictionary<string, T?>>(local.Value ?? Empty<T>());
        }

        return GetAsync<T>(new CacheKey(cacheKey), policy, token);
    }

    public async ValueTask<IDictionary<string, T?>> GetAsync<T>(CacheKey cacheKey, string[] fields, CachePolicy? policy, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        policy ??= _defaultPolicy;
        var cacheEntry = await GetCacheEntryAsync<T>(_entryBuilder.BuildEntryOptions<T>(cacheKey, fields, default, token: token), policy);
        return cacheEntry?.Value ?? Empty<T>();
    }

    public ValueTask<IDictionary<string, T?>> GetAsync<T>(Span<char> cacheKey, string[] fields, CachePolicy? policy, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        token.ThrowIfCancellationRequested();
        if (TryGetLocal<T>(cacheKey, out var local))
        {
            return new ValueTask<IDictionary<string, T?>>(local.Value is null ? Empty<T>() : SelectFields(local.Value, fields));
        }

        return GetAsync<T>(new CacheKey(cacheKey), fields, policy, token);
    }

    [OverloadResolutionPriority(1)]
    public ValueTask<ICacheEntry<IDictionary<string, T?>>> GetCacheEntryAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        if (!token.IsCancellationRequested && TryGetLocal<T>(cacheKey, out var local))
        {
            return new ValueTask<ICacheEntry<IDictionary<string, T?>>>(local);
        }

        policy ??= _defaultPolicy;
        return GetCacheEntryAsync<T>(_entryBuilder.BuildEntryOptions<T>(cacheKey, token), policy);
    }

    public ValueTask<ICacheEntry<IDictionary<string, T?>>> GetCacheEntryAsync<T>(Span<char> cacheKey, CachePolicy? policy, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        token.ThrowIfCancellationRequested();
        if (TryGetLocal<T>(cacheKey, out var local))
        {
            return new ValueTask<ICacheEntry<IDictionary<string, T?>>>(local);
        }

        return GetCacheEntryAsync<T>(new CacheKey(cacheKey), policy, token);
    }

    private async ValueTask<ICacheEntry<IDictionary<string, T?>>> GetCacheEntryAsync<T>(InternalHashCacheEntryOptions options, CachePolicy policy, bool localMissed = false)
    {
        if (!localMissed && _memoryCache.TryGetValue<ICacheEntry<IDictionary<string, T?>>>(options.CacheKey.Name, out var cacheEntry))
        {
            LogFoundLocal(Logged(options, typeof(T)));
            if (_connectionState.IsConnected)
            {
                MarkRead(cacheEntry);
                return Filter(cacheEntry!, options);
            }
            else if (_useLocalOnlyWhenDisconnected)
            {
                LogUsingLocalCopyDisconnected(Logged(options, typeof(T)));
                MarkRead(cacheEntry);
                return Filter(cacheEntry!, options);
            }
            else
            {
                RemoveLocal(options.CacheKey.Name);
                LogReturningDefaultDisconnected(Logged(options, typeof(T)));
                return _cacheEntryFactory.Create<IDictionary<string, T?>>(Empty<T>(), default, default);
            }
        }

        var fetched = await Layered<T>().FetchInnerAsync(options, policy).ConfigureAwait(false);
        if (!fetched.Found)
        {
            return fetched;
        }

        options.Expiration = fetched.Expiration;
        options.Metadata = fetched.Metadata;
        return Filter(fetched, options);
    }

    private ICacheEntry<IDictionary<string, T?>> Filter<T>(ICacheEntry<IDictionary<string, T?>> cacheEntry, InternalHashCacheEntryOptions options)
    {
        if (options.Fields == null || cacheEntry.Value == null)
        {
            return cacheEntry;
        }
        return CreateEntry(SelectFields(cacheEntry.Value, options.Fields), options);
    }

    private ICacheEntry<IDictionary<string, T?>> CreateEntry<T>(IDictionary<string, T?> values, InternalHashCacheEntryOptions options) =>
        _cacheEntryFactory.Create<IDictionary<string, T?>>(values.ToImmutableDictionary(), options.Expiration, options.Metadata?.ToImmutableDictionary());
}
