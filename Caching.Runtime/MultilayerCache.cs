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
        _entryBuilder = new CacheEntryBuilder(cacheKeyStrategy, topicKeyStrategy, _clock);
        _localMemorySetter = new LocalMemorySetter(cacheName, changeTokenFactory, _topicProvider, _memoryCache, logger, _clock, _multiLayerCacheOptions);
    }

    public  ValueTask<T?> GetAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return GetInnerAsync<T>(_entryBuilder.BuildEntryOptions<T>(cacheKey, _clock.ToDateTimeOffset(_multiLayerCacheOptions.DefaultExpiration), token));
    }

    public ValueTask<KeyValuePair<CacheKey, T?>[]> GetAsync<T>(CacheKey[] cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var options = cacheKey.Select(k => _entryBuilder.BuildEntryOptions<T>(k, default, token)).ToArray();
        return GetInnerAsync<T>(options, token);
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

        _logger.LogDebug("Cache missed. generating new {CacheKey}", cacheEntryOptions.CacheKey);
        ret = await generator().ConfigureAwait(false);

        if (!IsDefault(ret))
        {
            await InternalSetAsync(cacheEntryOptions, ret).ConfigureAwait(false);
        }
        return ret;
    }

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, T? value, CancellationToken token = default) =>
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

        _logger.LogDebug("Replacing cached key {CacheKey}", cacheEntryOptions.CacheKey);
        var fired = await _eventPublisher.CacheSetAsync(cacheEntryOptions).ConfigureAwait(false);
        return fired && await InternalSetAsync(cacheEntryOptions, value).ConfigureAwait(false);
    }


    public ValueTask<bool> SetAsync<T>(KeyValuePair<CacheKey, T?>[] keyValues, CancellationToken token = default) =>
        SetAsync(keyValues, _multiLayerCacheOptions.DefaultExpiration, token);

    public ValueTask<bool> SetAsync<T>(KeyValuePair<CacheKey, T?>[] keyValues, TimeSpan? expiration = null, CancellationToken token = default) =>
        SetAsync(keyValues, _clock.ToDateTimeOffset(expiration), token);

    public async ValueTask<bool> SetAsync<T>(KeyValuePair<CacheKey, T?>[] keyValues, DateTimeOffset? expiration = null, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var removeEntries = new List<CacheEntryOptions>(); 
        var setEntries = new List<CacheEntryValue<T>>();
        foreach (var keyValue in keyValues)
        {
            if (IsDefault(keyValue.Value))
            {
                removeEntries.Add(_entryBuilder.BuildEntryOptions<T>(keyValue.Key, token: token));
            }
            else
            {
                setEntries.Add(new ( _entryBuilder.BuildEntryOptions<T>(keyValue.Key, expiration, token), keyValue.Value ));
            }
        }

        if (removeEntries.Count > 0)
        {
            var result = await RemoveAsync<T>(removeEntries.ToArray(), token).ConfigureAwait(false);
            if (!result)
            {
                return false;
            }
        }

        var internalSetResult = await InternalSetAsync<T>(setEntries.ToArray(), token).ConfigureAwait(false);
        if (!internalSetResult)
        {
            return false;
        }

        foreach (var cacheEntry in setEntries.Select(s => s.CacheEntry))
        {
            _logger.LogDebug("Replacing cached key {CacheKey}", cacheEntry.CacheKey);
            var fired = await _eventPublisher.CacheSetAsync(cacheEntry).ConfigureAwait(false);
            if (!fired)
            {
                return false;
            }
        }

        return true;
    }

    public ValueTask<bool> RemoveAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return RemoveAsync<T>(_entryBuilder.BuildEntryOptions<T>(cacheKey, default, token));
    }

    public ValueTask<bool> RemoveAsync<T>(CacheKey[] cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var options = cacheKey.Select(k => _entryBuilder.BuildEntryOptions<T>(k, default)).ToArray();
        return RemoveAsync<T>(options, token);
    }

    public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, CancellationToken token = default) =>
        RefreshAsync<T>(cacheKey, _multiLayerCacheOptions.DefaultExpiration, token);

    public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, TimeSpan? expiration = null, CancellationToken token = default) =>
        RefreshAsync<T>(cacheKey, _clock.ToDateTimeOffset(expiration), token);

    public async ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, DateTimeOffset? expiration = null, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var cacheEntryOptions = _entryBuilder.BuildEntryOptions<T>(cacheKey, expiration, token);
        _logger.LogDebug("Clearing cached. Key {CacheKey}", cacheEntryOptions.CacheKey);
        _memoryCache.Remove(cacheEntryOptions.CacheKey);
        _logger.LogTrace("Refreshing inner cache key {CacheKey} at expiration {Expiration}", cacheEntryOptions.CacheKey, cacheEntryOptions.Expiration);
        try
        {
            var fired = await _eventPublisher.CacheRefreshedAsync(cacheEntryOptions).ConfigureAwait(false);
            return fired && await _innerCache.RefreshAsync<T>(cacheEntryOptions.CacheKey, cacheEntryOptions.Expiration, token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Inner cache refresh value for cacheKey {CacheKey}", cacheKey);
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
            _logger.LogWarning(ex, "Inner cache contains for cacheKey {CacheKey}", cacheKey);
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
        _logger.LogDebug("Clearing local cached. cacheKey {CacheKey}", options.CacheKey);
        try
        {
            _memoryCache.Remove(options.CacheKey);
            var removed = await _innerCache.RemoveAsync<T>(options.CacheKey, options.Token).ConfigureAwait(false);
            var eventFired = await _eventPublisher.CacheRemovedAsync(options).ConfigureAwait(false);
            return removed && eventFired;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Inner cache remove cacheKey {CacheKey}", options.CacheKey);
            return false;
        }
    }

    private async ValueTask<bool> RemoveAsync<T>(CacheEntryOptions[] options, CancellationToken token = default)
    {
        try
        {
            foreach (var option in options)
            {
                _memoryCache.Remove(option.CacheKey);
            }

            var removeInnerResult = await _innerCache.RemoveAsync<T>(options.Select(o => o.CacheKey).ToArray(), token).ConfigureAwait(false);
            if (!removeInnerResult)
            {
                return false;
            }

            foreach (var option in options)
            {
                var removedEventPublished = await _eventPublisher.CacheRemovedAsync(option).ConfigureAwait(false);
                if (!removedEventPublished)
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Inner cache remove cacheKeys {CacheKeys}", string.Join(",", options.Select(o => o.CacheKey)));
            return false;
        }
    }

    private async ValueTask<KeyValuePair<CacheKey, T?>[]> GetInnerAsync<T>(CacheEntryOptions[] options, CancellationToken token = default)
    {
        List<KeyValuePair<CacheKey, T?>> results = [];
        List<CacheEntryOptions> cacheEntriesToFetch = [];
        foreach (var option in options)
        {
            if (_memoryCache.TryGetValue<ICacheEntry<T>>(option.CacheKey, out var entry))
            {
                _logger.LogTrace("Found local. {CacheKeys}.", option.CacheKey);
                if (_connectionState.IsConnected)
                {
                    results.Add(new KeyValuePair<CacheKey, T?>(option.CacheKey, entry!.Value));
                }
                else
                {
                    _logger.LogTrace("Inner cache is not connected. Returning default for cacheKey {CacheKey}", option.CacheKey);
                    _memoryCache.Remove(option.CacheKey);
                }
            }
            else
            {
                cacheEntriesToFetch.Add(option);
            }
        }

        var keys = cacheEntriesToFetch.Select(c => c.CacheKey).ToArray();
        if (keys.Length == 0)
        {
            return results.ToArray();
        }

        var fetched = await _innerCache.GetAsync<T>(keys, token).ConfigureAwait(false);

        for (int i = 0; i < keys.Length; i++)
        {
            var keyValue = fetched[i];

            if (IsDefault(keyValue.Value))
            {
                continue;
            }

            _logger.LogTrace("Found inner cache copy at cacheKey {CacheKey}", keyValue.Key);
            var option = cacheEntriesToFetch[i];
            option.Expiration = await _innerCache.ExpireTimeAsync<T>(keyValue.Key, token).ConfigureAwait(false) ?? _clock.DefaultDateTimeOffset();
            results.Add(keyValue);
            MemorySet(option, keyValue);

        }

        return results.ToArray();
    }

    private async ValueTask<T?> GetInnerAsync<T>(CacheEntryOptions options)
    {
        if (_memoryCache.TryGetValue<ICacheEntry<T>>(options.CacheKey, out var entry))
        {
            _logger.LogTrace("Found local. {CacheKey}.", options.CacheKey);
            if(_connectionState.IsConnected)
            {
                return entry!.Value;
            }
            else
            {
                _logger.LogTrace("Inner cache is not connected. Returning default for cacheKey {CacheKey}", options.CacheKey);
                _memoryCache.Remove(options.CacheKey);
                return default;
            }
        }

        var ret = await _innerCache.GetAsync<T>(options.CacheKey, options.Token).ConfigureAwait(false);

        if (IsDefault(ret))
        {
            return default;
        }

        _logger.LogTrace("Found inner cache copy at cacheKey {CacheKey}", options.CacheKey);
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
            _logger.LogWarning(ex, "Inner cache set value for {CacheKey}", options.CacheKey);
            return false;
        }
    }

    private async ValueTask<bool> InternalSetAsync<T>(CacheEntryValue<T>[] cacheEntries, CancellationToken token = default)
    {
        try
        {
            var cacheKeyValuePairs = cacheEntries.Select(c => new KeyValuePair<CacheKey, T?>(c.CacheEntry.CacheKey, c.Value)).ToArray();

            var set = await _innerCache.SetAsync<T?>(cacheKeyValuePairs, token).ConfigureAwait(false);

            if (!set)
            {
                return false;
            }

            foreach (var cacheEntry in cacheEntries)
            {
                set = MemorySet(cacheEntry.CacheEntry, cacheEntry.Value);
                if (!set)
                {
                    return false;
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Inner cache set value for {CacheKeys}", string.Join(",", cacheEntries.Select(o => o.CacheEntry.CacheKey)));
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

    private struct CacheEntryValue<T>
    {
        public CacheEntryValue(CacheEntryOptions cacheEntry, T? value)
        {
            CacheEntry = cacheEntry;
            Value = value;
        }

        public CacheEntryOptions CacheEntry { get; init; }
        public T? Value { get; init; }
    }
}
