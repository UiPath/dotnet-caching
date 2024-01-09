using UiPath.Platform.Caching.Telemetry;

namespace UiPath.Platform.Caching;

public sealed class MultilayerCache : MultilayerCacheBase, ICache
{
    private readonly ICache _innerCache;
    private readonly CacheEntryBuilder _entryBuilder;
    private readonly LocalMemorySetter _localMemorySetter;

    public MultilayerCache(
        string cacheName,
        ICache innerCache,
        Func<IMemoryCache> memoryCacheAccessor,
        IChangeTokenFactory changeTokenFactory,
        ITopicFactory topicFactory,
        ICacheEventFactory cacheEventFactory,
        ICachingTelemetryProvider telemetryProvider,
        IMultilayerCacheOptions multiLayerCacheOptions,
        CacheOptions cacheOptions,
        ILogger logger)
        : base(cacheName, innerCache, memoryCacheAccessor, topicFactory, cacheEventFactory, telemetryProvider, multiLayerCacheOptions, cacheOptions, logger)
    {
        _innerCache = innerCache;
        var cacheKeyStrategy = _multiLayerCacheOptions.CacheKeyStrategy ?? new DefaultCacheKeyStrategy();
        var topicKeyStrategy = _multiLayerCacheOptions.TopicKeyStrategy ?? new DefaultTopicKeyStrategy(cacheOptions.Separator);
        var topicProvider = topicFactory.Get(_multiLayerCacheOptions.Topic);
        _entryBuilder = new CacheEntryBuilder(cacheKeyStrategy, topicKeyStrategy, _clock);
        _localMemorySetter = new LocalMemorySetter(cacheName, changeTokenFactory, topicProvider, _memoryCache, logger, _clock, _multiLayerCacheOptions);
    }

    public  ValueTask<T?> GetAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return GetInnerAsync<T>(_entryBuilder.BuildEntryOptions<T>(cacheKey, _clock.ToDateTimeOffset(_multiLayerCacheOptions.DefaultExpiration), token));
    }

    public ValueTask<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<ValueTask<T?>> generator, CancellationToken token = default)=>
        GetOrAddAsync(cacheKey, generator, _multiLayerCacheOptions.DefaultExpiration, token);

    public ValueTask<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<ValueTask<T?>> generator, TimeSpan? expiration = null, CancellationToken token = default)=>
        GetOrAddAsync(cacheKey, generator, _clock.ToDateTimeOffset(expiration), token);

    public async ValueTask<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<ValueTask<T?>> generator, DateTimeOffset? expiration = null, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var cacheEntryOptions = _entryBuilder.BuildEntryOptions<T>(cacheKey, expiration, token);

        var ret = await GetInnerAsync<T>(cacheEntryOptions).ConfigureAwait(false);
        if (!IsDefault(ret))
        {
            return ret;
        }

        _logger.LogDebug("Cache missed. generating new {}", cacheEntryOptions.CacheKey);
        ret = await generator().ConfigureAwait(false);

        if (!IsDefault(ret))
        {
            await InternalSetAsync(cacheEntryOptions, ret).ConfigureAwait(false);
        }
        return ret;
    }

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, T? value, CancellationToken token = default)=>
        SetAsync(cacheKey, value, _multiLayerCacheOptions.DefaultExpiration, token);

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, T? value, TimeSpan? expiration = null, CancellationToken token = default) =>
        SetAsync(cacheKey, value, _clock.ToDateTimeOffset(expiration), token);

    public async ValueTask<bool> SetAsync<T>(CacheKey cacheKey, T? value, DateTimeOffset? expiration = null, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var cacheEntryOptions = _entryBuilder.BuildEntryOptions<T>(cacheKey, expiration, token);
        if (IsDefault(value))
        {
            return await RemoveAsync<T>(cacheEntryOptions).ConfigureAwait(false);
        }

        _logger.LogDebug("Replacing cached key {}", cacheEntryOptions.CacheKey);
        var fired = await _eventPublisher.CacheSetAsync(cacheEntryOptions).ConfigureAwait(false);
        return fired && await InternalSetAsync(cacheEntryOptions, value).ConfigureAwait(false);
    }

    public ValueTask<bool> RemoveAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return RemoveAsync<T>(_entryBuilder.BuildEntryOptions<T>(cacheKey, default, token));
    }

    public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, CancellationToken token = default) =>
        RefreshAsync<T>(cacheKey, _multiLayerCacheOptions.DefaultExpiration, token);

    public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, TimeSpan? expiration = null, CancellationToken token = default) =>
        RefreshAsync<T>(cacheKey, _clock.ToDateTimeOffset(expiration), token);

    public async ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, DateTimeOffset? expiration = null, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var cacheEntryOptions = _entryBuilder.BuildEntryOptions<T>(cacheKey, expiration, token);
        _logger.LogDebug("Clearing cached. Key {}", cacheEntryOptions.CacheKey);
        _memoryCache.Remove(cacheEntryOptions.CacheKey);
        _logger.LogTrace("Refreshing inner cache key {} at expiration {}", cacheEntryOptions.CacheKey, cacheEntryOptions.Expiration);
        try
        {
            var fired = await _eventPublisher.CacheRefreshedAsync(cacheEntryOptions).ConfigureAwait(false);
            return fired && await _innerCache.RefreshAsync<T>(cacheEntryOptions.CacheKey, cacheEntryOptions.Expiration, token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Inner cache refresh value for cacheKey {}", cacheKey);
            return false;
        }
    }

    public async ValueTask<bool> ContainsAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var cacheEntryOptions = _entryBuilder.BuildEntryOptions<T>(cacheKey, default, token);
        try
        {
            return _memoryCache.TryGetValue(cacheEntryOptions.CacheKey, out _) || await _innerCache.ContainsAsync<T>(cacheEntryOptions.CacheKey, cacheEntryOptions.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Inner cache contains for cacheKey {}", cacheKey);
            return false;
        }
    }

    public async ValueTask<TimeSpan?> TimeToLiveAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var cacheEntryOptions = _entryBuilder.BuildEntryOptions<T>(cacheKey, default, token);
        return _memoryCache.TryGetValue<ICacheEntry>(cacheEntryOptions.CacheKey, out var value)
            ? value?.Expiration.Subtract(_clock.UtcNow)
            : await _innerCache.TimeToLiveAsync<T>(cacheEntryOptions.CacheKey, token);
    }

    public async ValueTask<DateTimeOffset?> ExpireTimeAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var cacheEntryOptions = _entryBuilder.BuildEntryOptions<T>(cacheKey, default, token);

        return _memoryCache.TryGetValue<ICacheEntry>(cacheEntryOptions.CacheKey, out var value)
            ? value?.Expiration
            : await _innerCache.ExpireTimeAsync<T>(cacheEntryOptions.CacheKey, token);
    }

    private async ValueTask<bool> RemoveAsync<T>(CacheEntryOptions options)
    {
        _logger.LogDebug("Clearing local cached. cacheKey {}", options.CacheKey);
        try
        {
            _memoryCache.Remove(options.CacheKey);
            var removed = await _innerCache.RemoveAsync<T>(options.CacheKey, options.Token).ConfigureAwait(false);
            var eventFired = await _eventPublisher.CacheRemovedAsync(options).ConfigureAwait(false);
            return removed && eventFired;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Inner cache remove cacheKey {}", options.CacheKey);
            return false;
        }
    }

    private async ValueTask<T?> GetInnerAsync<T>(CacheEntryOptions options)
    {
        if (_memoryCache.TryGetValue<ICacheEntry<T>>(options.CacheKey, out var entry))
        {
            _logger.LogTrace("Found local. {}.", options.CacheKey);
            if(_connectionEventSource.IsConnected)
            {
                return entry!.Value;
            }
            else
            {
                _logger.LogTrace("Inner cache is not connected. Returning default for cacheKey {}", options.CacheKey);
                _memoryCache.Remove(options.CacheKey);
                return default;
            }
        }

        var ret = await _innerCache.GetAsync<T>(options.CacheKey, options.Token).ConfigureAwait(false);

        if (IsDefault(ret))
        {
            return default;
        }

        _logger.LogTrace("Found inner cache copy at cacheKey {}", options.CacheKey);
        options.Expiration = await _innerCache.ExpireTimeAsync<T>(options.CacheKey, options.Token).ConfigureAwait(false) ?? _clock.DefaultDateTimeOffset();
        MemorySet(options, ret);
        return ret;
    }

    private async ValueTask<bool> InternalSetAsync<T>(CacheEntryOptions options, T? value)
    {
        try
        {
            var ret = await _innerCache.SetAsync<T?>(options.CacheKey, value, options.Expiration, options.Token).ConfigureAwait(false);
            return ret ? MemorySet(options, value) : ret;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Inner cache set value for {}", options.CacheKey);
            return false;
        }
    }

    private bool MemorySet<T>(CacheEntryOptions options, T value)
    {
        var item = _cacheEntryFactory.Create(value, options.Expiration);
        return _localMemorySetter.Set(options, item, typeof(T), _multiLayerCacheOptions.PrimaryMaxExpiration);
    }

    private static bool IsDefault<T>(T value) =>
        EqualityComparer<T>.Default.Equals(value, default);
}
