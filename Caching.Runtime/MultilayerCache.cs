using UiPath.Platform.Caching.Telemetry;

namespace UiPath.Platform.Caching;

public sealed class MultilayerCache : ICache
{
    private readonly ILogger _logger;
    private readonly IMemoryCache _memoryCache;
    private readonly ICache _innerCache;
    private readonly ICacheEntryFactory _cacheEntryFactory;
    private readonly IMultilayerCacheOptions _cacheOptions;
    private readonly IDisposable _monitor;
    private readonly CacheClock _clock;
    private readonly CacheEntryBuilder _entryBuilder;
    private readonly CacheEventPublisher _eventPublisher;
    private readonly LocalMemorySetter _localMemorySetter;


    public MultilayerCache(
        string cacheName,
        ICache innerCache,
        Func<IMemoryCache> memoryCacheAccessor,
        IChangeTokenFactory changeTokenFactory,
        ITopicFactory topicFactory,
        ICacheEventFactory cacheEventFactory,
        ICachingTelemetryProvider telemetryProvider,
        IMultilayerCacheOptions cacheOptions,
        ILogger logger)
    {
        _innerCache = innerCache;
        _logger = logger;
        _cacheOptions = cacheOptions;
        _memoryCache = memoryCacheAccessor();
        _cacheEntryFactory = _cacheOptions.EntryFactory ?? new CacheEntryFactory();
        _monitor = _memoryCache.Monitor(cacheOptions, telemetryProvider, GetType().Name);
        _clock = new CacheClock(_cacheOptions.Clock, _cacheOptions.DefaultExpiration);
        var cacheKeyStrategy = _cacheOptions.CacheKeyStrategy ?? new DefaultCacheKeyStrategy();
        var topicKeyStrategy = _cacheOptions.TopicKeyStrategy ?? new DefaultTopicKeyStrategy();
        _entryBuilder = new CacheEntryBuilder(cacheKeyStrategy, topicKeyStrategy, _clock);
        _eventPublisher = new CacheEventPublisher(cacheName, _cacheOptions.Topic, topicFactory, cacheEventFactory, logger);
        _localMemorySetter = new LocalMemorySetter(cacheName, changeTokenFactory, topicFactory, _memoryCache, logger, _clock, _cacheOptions);
    }

    public async ValueTask<T?> GetAsync<T>(CacheKey cacheKey, T? defaultValue = null, CancellationToken token = default) where T : class
    {
        var ret = await GetInnerAsync<T>(_entryBuilder.BuildEntryOptions<T>(cacheKey, _clock.ToDateTimeOffset(_cacheOptions.DefaultExpiration), token));
        return ret ?? defaultValue;
    }

    public async ValueTask<T?> GetAsync<T>(CacheKey cacheKey, T? defaultValue = null, CancellationToken token = default) where T : struct
    {
        var ret = await GetStructInnerAsync<T>(_entryBuilder.BuildEntryOptions<T>(cacheKey, _clock.ToDateTimeOffset(_cacheOptions.DefaultExpiration), token));
        return ret ?? defaultValue;
    }

    public ValueTask<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<ValueTask<T?>> generator, CancellationToken token = default) where T : class =>
        GetOrAddAsync(cacheKey, generator, _cacheOptions.DefaultExpiration, token);

    public ValueTask<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<ValueTask<T?>> generator, CancellationToken token = default) where T : struct =>
        GetOrAddAsync(cacheKey, generator, _cacheOptions.DefaultExpiration, token);

    public ValueTask<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<ValueTask<T?>> generator, TimeSpan? expiration, CancellationToken token = default) where T : class =>
        GetOrAddAsync(cacheKey, generator, _clock.ToDateTimeOffset(expiration), token);

    public ValueTask<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<ValueTask<T?>> generator, TimeSpan? expiration, CancellationToken token = default) where T : struct =>
        GetOrAddAsync(cacheKey, generator, _clock.ToDateTimeOffset(expiration), token);

    public async ValueTask<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<ValueTask<T?>> generator, DateTimeOffset? expiration, CancellationToken token = default) where T : class
    {
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

    public async ValueTask<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<ValueTask<T?>> generator, DateTimeOffset? expiration, CancellationToken token = default) where T : struct
    {
        var cacheEntryOptions = _entryBuilder.BuildEntryOptions<T>(cacheKey, expiration, token);

        var ret = await GetStructInnerAsync<T>(cacheEntryOptions).ConfigureAwait(false);
        if (!IsDefault(ret))
        {
            return ret;
        }

        _logger.LogDebug("Cache missed. generating new {}", cacheEntryOptions.CacheKey);
        ret = await generator().ConfigureAwait(false);

        if (!IsDefault(ret))
        {
            await InternalStructSetAsync(cacheEntryOptions, ret).ConfigureAwait(false);
        }
        return ret;
    }

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, T? value, CancellationToken token = default) where T : class =>
        SetAsync(cacheKey, value, _cacheOptions.DefaultExpiration, token);

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, T? value, CancellationToken token = default) where T : struct =>
        SetAsync(cacheKey, value, _cacheOptions.DefaultExpiration, token);

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, T? value, TimeSpan? expiration, CancellationToken token = default) where T : class =>
        SetAsync(cacheKey, value, _clock.ToDateTimeOffset(expiration), token);

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, T? value, TimeSpan? expiration, CancellationToken token = default) where T : struct =>
        SetAsync(cacheKey, value, _clock.ToDateTimeOffset(expiration), token);

    public async ValueTask<bool> SetAsync<T>(CacheKey cacheKey, T? value, DateTimeOffset? expiration, CancellationToken token = default) where T : class
    {
        var cacheEntryOptions = _entryBuilder.BuildEntryOptions<T>(cacheKey, expiration, token);
        if (IsDefault(value))
        {
            return await RemoveAsync<T>(cacheEntryOptions).ConfigureAwait(false);
        }

        _logger.LogDebug("Replacing cached key {}", cacheEntryOptions.CacheKey);
        await _eventPublisher.CacheSetAsync<T>(cacheEntryOptions).ConfigureAwait(false);
        return await InternalSetAsync(cacheEntryOptions, value).ConfigureAwait(false);
    }

    public async ValueTask<bool> SetAsync<T>(CacheKey cacheKey, T? value, DateTimeOffset? expiration, CancellationToken token = default) where T : struct
    {
        var cacheEntryOptions = _entryBuilder.BuildEntryOptions<T>(cacheKey, expiration, token);
        if (IsDefault(value))
        {
            return await RemoveAsync<T>(cacheEntryOptions).ConfigureAwait(false);
        }

        _logger.LogDebug("Replacing cached key {}", cacheEntryOptions.CacheKey);
        await _eventPublisher.CacheSetAsync<T>(cacheEntryOptions).ConfigureAwait(false);
        return await InternalStructSetAsync(cacheEntryOptions, value).ConfigureAwait(false);
    }

    public ValueTask<bool> RemoveAsync<T>(CacheKey cacheKey, CancellationToken token = default) =>
        RemoveAsync<T>(_entryBuilder.BuildEntryOptions<T>(cacheKey, default, token));

    public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, CancellationToken token = default) =>
        RefreshAsync<T>(cacheKey, _cacheOptions.DefaultExpiration, token);

    public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, TimeSpan? expiration, CancellationToken token = default) =>
        RefreshAsync<T>(cacheKey, _clock.ToDateTimeOffset(expiration), token);

    public async ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, DateTimeOffset? expiration, CancellationToken token = default)
    {
        var cacheEntryOptions = _entryBuilder.BuildEntryOptions<T>(cacheKey, expiration, token);
        _logger.LogDebug("Clearing cached. Key {}", cacheEntryOptions.CacheKey);
        _memoryCache.Remove(cacheEntryOptions.CacheKey);
        _logger.LogTrace("Refreshing inner cache key {} at expiration {}", cacheEntryOptions.CacheKey, cacheEntryOptions.Expiration);
        try
        {
            var ret = await _innerCache.RefreshAsync<T>(cacheEntryOptions.CacheKey, cacheEntryOptions.Expiration, token).ConfigureAwait(false);
            await _eventPublisher.CacheRefreshedAsync<T>(cacheEntryOptions).ConfigureAwait(false);
            return ret;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Inner cache refresh value for cacheKey {}", cacheKey);
            return false;
        }
    }

    public async ValueTask<bool> ContainsAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
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
        var cacheEntryOptions = _entryBuilder.BuildEntryOptions<T>(cacheKey, default, token);
        return _memoryCache.TryGetValue(cacheEntryOptions.CacheKey, out ICacheEntry? value)
            ? value?.Expiration.Subtract(_clock.UtcNow)
            : await _innerCache.TimeToLiveAsync<T>(cacheEntryOptions.CacheKey, token);
    }

    public async ValueTask<DateTimeOffset?> ExpireTimeAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        var cacheEntryOptions = _entryBuilder.BuildEntryOptions<T>(cacheKey, default, token);

        return _memoryCache.TryGetValue(cacheEntryOptions.CacheKey, out ICacheEntry? value)
            ? value?.Expiration
            : await _innerCache.ExpireTimeAsync<T>(cacheEntryOptions.CacheKey, token);
    }

    private async ValueTask<bool> RemoveAsync<T>(CacheEntryOptions options)
    {
        _logger.LogDebug("Clearing local cached. cacheKey {}", options.CacheKey);
        try
        {
            _memoryCache.Remove(options.CacheKey);
            var ret = await _innerCache.RemoveAsync<T>(options.CacheKey, options.Token).ConfigureAwait(false);
            await _eventPublisher.CacheRemovedAsync<T>(options).ConfigureAwait(false);
            return ret;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Inner cache remove cacheKey {}", options.CacheKey);
            return false;
        }
    }

    private async ValueTask<T?> GetInnerAsync<T>(CacheEntryOptions options)
        where T : class
    {
        if (_memoryCache.TryGetValue(options.CacheKey, out ICacheEntry<T>? entry))
        {
            _logger.LogTrace("Found local. {}", options.CacheKey);
            return entry?.Value;
        }

        var ret = await _innerCache.GetAsync<T>(options.CacheKey, null, options.Token).ConfigureAwait(false);

        if (IsDefault(ret))
        {
            return default;
        }

        _logger.LogTrace("Found inner cache copy at cacheKey {}", options.CacheKey);
        options.Expiration = await _innerCache.ExpireTimeAsync<T>(options.CacheKey, options.Token).ConfigureAwait(false) ?? _clock.DefaultDateTimeOffset();
        MemorySet(options, ret);
        return ret;
    }

    private async ValueTask<T?> GetStructInnerAsync<T>(CacheEntryOptions options)
        where T : struct
    {
        if (_memoryCache.TryGetValue(options.CacheKey, out ICacheEntry<T>? entry))
        {
            _logger.LogTrace("Found local. {}", options.CacheKey);
            return entry?.Value;
        }

        var ret = await _innerCache.GetAsync<T>(options.CacheKey, null, options.Token).ConfigureAwait(false);

        if (IsDefault(ret))
        {
            return default;
        }

        _logger.LogTrace("Found inner cache copy at cacheKey {}", options.CacheKey);
        options.Expiration = await _innerCache.ExpireTimeAsync<T>(options.CacheKey, options.Token).ConfigureAwait(false) ?? _clock.DefaultDateTimeOffset();
        MemorySet(options, ret);
        return ret;
    }

    private async ValueTask<bool> InternalSetAsync<T>(CacheEntryOptions options, T? value) where T : class
    {
        try
        {
            MemorySet(options, value);
            return await _innerCache.SetAsync(options.CacheKey, value, options.Expiration, options.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Inner cache set value for {}", options.CacheKey);
            return false;
        }
    }

    private async ValueTask<bool> InternalStructSetAsync<T>(CacheEntryOptions options, T? value) where T : struct
    {
        try
        {
            MemorySet(options, value);
            return await _innerCache.SetAsync(options.CacheKey, value, options.Expiration, options.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Inner cache set value for {}", options.CacheKey);
            return false;
        }
    }

    private void MemorySet<T>(CacheEntryOptions options, T value)
    {
        var item = _cacheEntryFactory.Create(value, options.Expiration);
        _localMemorySetter.Set(options, item, typeof(T), _cacheOptions.PrimaryMaxExpiration);
    }

    private static bool IsDefault<T>(T value) =>
        EqualityComparer<T>.Default.Equals(value, default);

    public void Dispose()
    {
        _monitor.Dispose();
        _memoryCache.Dispose();
    }
}
