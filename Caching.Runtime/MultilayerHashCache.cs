using System.Collections.Immutable;
using UiPath.Platform.Caching.Locking;
using UiPath.Platform.Caching.Telemetry;

namespace UiPath.Platform.Caching;

public sealed partial class MultilayerHashCache : MultilayerCacheBase, IHashCache
{
    private readonly IHashCache _innerCache;
    private readonly HashCacheEntryBuilder _entryBuilder;
    private readonly HashLocalMemorySetter _localMemorySetter;

    public MultilayerHashCache(
        string cacheName,
        IHashCache innerCache,
        IMemoryCacheFactory memoryCacheFactory,
        IChangeTokenFactory changeTokenFactory,
        ITopicFactory topicFactory,
        ICacheEventFactory cacheEventFactory,
        ICachingTelemetryProvider telemetryProvider,
        IMultilayerCacheOptions multiLayerCacheOptions,
        IMemoryCacheOptions memoryCacheOptions,
        CacheOptions cacheOptions,
        ILocalLock localLock,
        IDistributedLock distributedLock,
        ILogger logger)
        : base(cacheName, innerCache, memoryCacheFactory, topicFactory, cacheEventFactory, telemetryProvider, multiLayerCacheOptions, memoryCacheOptions, cacheOptions, localLock, distributedLock, logger)
    {
        _innerCache = innerCache;
        var cacheKeyStrategy = _multiLayerCacheOptions.CacheKeyStrategy ?? new DefaultCacheKeyStrategy();
        var topicKeyStrategy = _multiLayerCacheOptions.TopicKeyStrategy ?? new DefaultTopicKeyStrategy(cacheOptions.Separator);
        _entryBuilder = new HashCacheEntryBuilder(cacheKeyStrategy, topicKeyStrategy, _clock);
        _localMemorySetter = new HashLocalMemorySetter(cacheName, changeTokenFactory, _topicProvider, _memoryCache, logger, _clock, _multiLayerCacheOptions, memoryCacheOptions, telemetryProvider);
    }

    public async ValueTask<T?> GetItemAsync<T>(CacheKey cacheKey, string field, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var cacheEntry = await GetCacheEntryAsync<T>(_entryBuilder.BuildEntryOptions<T>(cacheKey, new[] { field }, default, token: token));
        if (cacheEntry.Value == null)
        {
            return default;
        }

        return cacheEntry.Value.TryGetValue(field, out var value) ? value : default;
    }

    public async ValueTask<IDictionary<string, T?>> GetAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var cacheEntry = await GetCacheEntryAsync<T>(_entryBuilder.BuildEntryOptions<T>(cacheKey, token));
        return cacheEntry.Value ?? Empty<T>();
    }

    public async ValueTask<IDictionary<string, T?>> GetAsync<T>(CacheKey cacheKey, string[] fields, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var cacheEntry = await GetCacheEntryAsync<T>(_entryBuilder.BuildEntryOptions<T>(cacheKey, fields, default, token: token));
        return cacheEntry?.Value ?? Empty<T>();
    }

    public ValueTask<ICacheEntry<IDictionary<string, T?>>> GetCacheEntryAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return GetCacheEntryAsync<T>(_entryBuilder.BuildEntryOptions<T>(cacheKey, token));
    }

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, CancellationToken token = default) =>
        GetOrAddAsync(cacheKey, generator, _multiLayerCacheOptions.DefaultExpiration, token);

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan? expiration = null, CancellationToken token = default) =>
        GetOrAddAsync(cacheKey, generator, _clock.ToDateTimeOffset(expiration), HashCacheSetOption.KeyReplace, token);

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset? expiration = null, CancellationToken token = default) =>
        GetOrAddAsync(cacheKey, generator, expiration, HashCacheSetOption.KeyReplace, token);

    public async ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset? expiration = null, HashCacheSetOption? setOption = null, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var cacheEntryOptions = _entryBuilder.BuildEntryOptions<T>(cacheKey, expiration, setOption ?? HashCacheSetOption.KeyReplace, token);
        var cacheEntry = await GetCacheEntryAsync<T>(cacheEntryOptions).ConfigureAwait(false);
        if (!IsNullOrEmpty(cacheEntry.Value))
        {
            return cacheEntry.Value!;
        }

        return await RunUnderLocksAsync<IDictionary<string, T?>>(
            cacheEntryOptions.CacheKey,
            async () => (await GetCacheEntryAsync<T>(cacheEntryOptions).ConfigureAwait(false)).Value ?? Empty<T>(),
            v => !IsNullOrEmpty(v),
            ct => RunHashGeneratorAndStoreAsync(cacheEntryOptions, generator, ct),
            token).ConfigureAwait(false);
    }

    private async ValueTask<IDictionary<string, T?>> RunHashGeneratorAndStoreAsync<T>(InternalHashCacheEntryOptions cacheEntryOptions, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, CancellationToken token)
    {
        LogCacheMissed(cacheEntryOptions.CacheKey);
        var ret = await generator(token).ConfigureAwait(false);

        if (!IsNullOrEmpty(ret))
        {
            var innerCacheDisconnected = GetInnerCacheDisconnected();
            await InternalSetAsync(cacheEntryOptions, ret, innerCacheDisconnected).ConfigureAwait(false);
        }
        return ret;
    }

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, CancellationToken token = default) =>
        SetAsync(cacheKey, values, _multiLayerCacheOptions.DefaultExpiration, token);

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, TimeSpan? expiration = null, CancellationToken token = default) =>
        SetAsync(cacheKey, values, _clock.ToDateTimeOffset(expiration), token);

    public async ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, DateTimeOffset? expiration = null, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var options = _entryBuilder.BuildEntryOptions<T>(cacheKey, expiration, token: token);
        if (IsNullOrEmpty(values))
        {
            return await RemoveAsync<T>(options).ConfigureAwait(false);
        }

        LogReplacingCachedKey(options.CacheKey);
        var innerCacheDisconnected = GetInnerCacheDisconnected();
        if (innerCacheDisconnected)
        {
            LogSettingLocalOnly(options.CacheKey);
            return await InternalSetAsync(options, values, innerCacheDisconnected).ConfigureAwait(false);
        }
        else
        {
            var fired = await _eventPublisher.CacheSetAsync(options).ConfigureAwait(false);
            return fired && await InternalSetAsync(options, values, innerCacheDisconnected).ConfigureAwait(false);
        }
    }

    public async ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, HashCacheEntryOptions options, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var expiration = options.ExpireTime.HasValue ? _clock.ToDateTimeOffset(options.ExpireTime) : _clock.ToDateTimeOffset(options.TimeToLive);
        var cacheEntryOptions = _entryBuilder.BuildEntryOptions<T>(cacheKey, expiration, options.SetOption, token);
        cacheEntryOptions.Metadata = options.Metadata;
        if (IsNullOrEmpty(values))
        {
            return await RemoveAsync<T>(cacheEntryOptions).ConfigureAwait(false);
        }

        LogReplacingCachedKey(cacheEntryOptions.CacheKey);
        var innerCacheDisconnected = GetInnerCacheDisconnected();
        if (innerCacheDisconnected)
        {
            LogSettingLocalOnly(cacheEntryOptions.CacheKey);
            return await InternalSetAsync(cacheEntryOptions, values, innerCacheDisconnected).ConfigureAwait(false);
        }

        var fired = await _eventPublisher.CacheSetAsync(cacheEntryOptions).ConfigureAwait(false);
        return fired && await InternalSetAsync(cacheEntryOptions, values, innerCacheDisconnected).ConfigureAwait(false);
    }

    public ValueTask<bool> RemoveAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return RemoveAsync<T>(_entryBuilder.BuildEntryOptions<T>(cacheKey, default, token: token));
    }

    public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, CancellationToken token = default) =>
        RefreshAsync<T>(cacheKey, _multiLayerCacheOptions.DefaultExpiration, token);

    public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, TimeSpan? expiration = null, CancellationToken token = default) =>
        RefreshAsync<T>(cacheKey, _clock.ToDateTimeOffset(expiration), token);

    public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, DateTimeOffset? expiration = null, CancellationToken token = default) =>
        RefreshAsync<T>(cacheKey, new HashCacheEntryOptions(expiration), token);

    public async ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, HashCacheEntryOptions options, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var expiration = options.ExpireTime.HasValue ? _clock.ToDateTimeOffset(options.ExpireTime) : _clock.ToDateTimeOffset(options.TimeToLive);
        var cacheEntryOptions = _entryBuilder.BuildEntryOptions<T>(cacheKey, expiration, token: token);
        cacheEntryOptions.Metadata = options.Metadata;
        LogClearingCached(cacheEntryOptions.CacheKey);

        _memoryCache.Remove(cacheEntryOptions.CacheKey);
        LogRefreshingInnerCacheKey(cacheEntryOptions.CacheKey, cacheEntryOptions.Expiration);
        try
        {
            var fired = await _eventPublisher.CacheRefreshedAsync(cacheEntryOptions).ConfigureAwait(false);
            return fired && await _innerCache.RefreshAsync<T>(cacheEntryOptions.CacheKey, options, token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogInnerCacheRefreshError(ex, cacheEntryOptions.CacheKey);
            return false;
        }
    }

    public async ValueTask<bool> ContainsAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var cacheEntryOptions = _entryBuilder.BuildEntryOptions<T>(cacheKey, default, token: token);
        try
        {
            return _memoryCache.TryGetValue(cacheEntryOptions.CacheKey, out _) || await _innerCache.ContainsAsync<T>(cacheEntryOptions.CacheKey, cacheEntryOptions.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogInnerCacheContainsError(ex, cacheEntryOptions.CacheKey);
            return false;
        }
    }

    public async ValueTask<TimeSpan?> TimeToLiveAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var cacheEntryOptions = _entryBuilder.BuildEntryOptions<T>(cacheKey, default, token: token);
        return _memoryCache.TryGetValue<ICacheEntry>(cacheEntryOptions.CacheKey, out var value)
            ? value?.Expiration.Subtract(_clock.UtcNow)
            : await _innerCache.TimeToLiveAsync<T>(cacheEntryOptions.CacheKey, token);
    }

    public async ValueTask<DateTimeOffset?> ExpireTimeAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var cacheEntryOptions = _entryBuilder.BuildEntryOptions<T>(cacheKey, default, token: token);
        return _memoryCache.TryGetValue<ICacheEntry>(cacheEntryOptions.CacheKey, out var value)
            ? value?.Expiration
            : await _innerCache.ExpireTimeAsync<T>(cacheEntryOptions.CacheKey, token);
    }

    public async ValueTask<IDictionary<string, string?>?> GetMetadataAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var options = _entryBuilder.BuildEntryOptions<T>(cacheKey, _clock.ToDateTimeOffset(_multiLayerCacheOptions.DefaultExpiration), token: token);
        return _memoryCache.TryGetValue<ICacheEntry>(options.CacheKey, out var entry)
            ? (entry?.Metadata)
            : await _innerCache.GetMetadataAsync<T>(options.CacheKey, token).ConfigureAwait(false);
    }

    public async ValueTask<bool> SetMetadataAsync<T>(CacheKey cacheKey, IDictionary<string, string?> metadata, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var cacheEntryOptions = _entryBuilder.BuildEntryOptions<T>(cacheKey, token);
        LogSetMetadata(cacheEntryOptions.CacheKey);
        try
        {
            var response = await _innerCache.SetMetadataAsync<T>(cacheEntryOptions.CacheKey, metadata, cacheEntryOptions.Token).ConfigureAwait(false);
            if (!response)
            {
                LogInnerCacheSetMetadataFailed(cacheEntryOptions.CacheKey);
                return false;
            }

            if(_memoryCache.TryGetValue<ICacheEntry>(cacheEntryOptions.CacheKey, out var entry) && entry != null)
            {
                cacheEntryOptions.Expiration = entry.Expiration;
            }
            else
            {
                var expiration = await _innerCache.ExpireTimeAsync<T>(cacheEntryOptions.CacheKey, cacheEntryOptions.Token).ConfigureAwait(false);
                cacheEntryOptions.Expiration = _clock.ToDateTimeOffset(expiration);
            }

            cacheEntryOptions.Metadata = metadata;
            return await _eventPublisher.MetadataUpdatedAsync(cacheEntryOptions).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _memoryCache.Remove(cacheEntryOptions.CacheKey);
            LogInnerCacheRefreshError(ex, cacheEntryOptions.CacheKey);
            return false;
        }
    }

    private async ValueTask<bool> RemoveAsync<T>(InternalHashCacheEntryOptions options)
    {
        LogClearingLocalCached(options.CacheKey);
        try
        {
            _memoryCache.Remove(options.CacheKey);
            var removed = await _innerCache.RemoveAsync<T>(options.CacheKey, options.Token).ConfigureAwait(false);
            var eventFired = await _eventPublisher.CacheRemovedAsync(options).ConfigureAwait(false);
            return removed && eventFired;
        }
        catch (Exception ex)
        {
            LogInnerCacheRemoveError(ex, options.CacheKey);
            return false;
        }
    }

    private async ValueTask<ICacheEntry<IDictionary<string, T?>>> GetCacheEntryAsync<T>(InternalHashCacheEntryOptions options)
    {
        if (_memoryCache.TryGetValue<ICacheEntry<IDictionary<string, T?>>>(options.CacheKey, out var cacheEntry))
        {
            LogFoundLocal(options.CacheKey);
            if (_connectionState.IsConnected)
            {
                return Filter(cacheEntry!, options);
            }
            else if (_usePrimaryOnlyWhenDisconnected)
            {
                LogUsingLocalCopyDisconnected(options.CacheKey);
                return Filter(cacheEntry!, options);
            }
            else
            {
                _memoryCache.Remove(options.CacheKey);
                LogReturningDefaultDisconnected(options.CacheKey);
                return _cacheEntryFactory.Create<IDictionary<string, T?>>(Empty<T>(), default, default);
            }
        }

        cacheEntry = await _innerCache.GetCacheEntryAsync<T>(options.CacheKey, options.Token).ConfigureAwait(false);

        if (IsNullOrEmpty(cacheEntry.Value))
        {
            return cacheEntry!;
        }

        LogFoundInnerCopy(options.CacheKey);
        options.Expiration = cacheEntry.Expiration;
        options.Metadata = cacheEntry.Metadata;
        var values = cacheEntry.Value!;
        MemorySet(options, values, _multiLayerCacheOptions.PrimaryMaxExpiration);

        return Filter(cacheEntry!, options);
    }

    private bool MemorySet<T>(InternalHashCacheEntryOptions options, IDictionary<string, T?> value, TimeSpan? maxExpiration)
    {
        var item = CreateEntry(value, options);
        return _localMemorySetter.Set(options, item, typeof(T), maxExpiration);
    }

    private async ValueTask<bool> InternalSetAsync<T>(InternalHashCacheEntryOptions options, IDictionary<string, T?> value, bool disconnected)
    {
        try
        {
            if (disconnected)
            {
                LogSettingLocalOnly(options.CacheKey);
                return MemorySet(options, value, _multiLayerCacheOptions.PrimaryMaxExpirationDisconnected);
            }

            var ret = await _innerCache.SetAsync<T?>(options.CacheKey, value, new HashCacheEntryOptions(options.Expiration, null, options.Metadata, options.SetOption), options.Token).ConfigureAwait(false);
            return ret && MemorySet(options, value, _multiLayerCacheOptions.PrimaryMaxExpiration);
        }
        catch (Exception ex)
        {
            LogInnerCacheSetError(ex, options.CacheKey);
            return false;
        }
    }

    private ICacheEntry<IDictionary<string, T?>> Filter<T>(ICacheEntry<IDictionary<string, T?>> cacheEntry, InternalHashCacheEntryOptions options)
    {
        if (options.Fields == null || cacheEntry.Value == null)
        {
            return cacheEntry;
        }
        var allFields = options.Fields.ToHashSet(StringComparer.InvariantCultureIgnoreCase);
        var values = cacheEntry.Value.Where(kv => allFields.Contains(kv.Key)).ToImmutableDictionary(kv => kv.Key, kv => kv.Value);
        return CreateEntry(values, options);
    }

    private ICacheEntry<IDictionary<string, T?>> CreateEntry<T>(IDictionary<string, T?> values, InternalHashCacheEntryOptions options) =>
        _cacheEntryFactory.Create<IDictionary<string, T?>>(values.ToImmutableDictionary(), options.Expiration, options.Metadata?.ToImmutableDictionary());

    private static bool IsNullOrEmpty<T>(IDictionary<string, T?>? value) =>
        value is null || value.Count == 0;

    private static ImmutableDictionary<string, T?> Empty<T>() =>
        ImmutableDictionary<string, T?>.Empty;

    [LoggerMessage(Level = LogLevel.Debug, Message = "Cache missed. generating new {CacheKey}")]
    private partial void LogCacheMissed(CacheKey cacheKey);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Replacing cached cacheKey {CacheKey}")]
    private partial void LogReplacingCachedKey(CacheKey cacheKey);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Inner cache is not connected. Setting local only for cacheKey {CacheKey}")]
    private partial void LogSettingLocalOnly(CacheKey cacheKey);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Clearing cached. cacheKey {CacheKey}")]
    private partial void LogClearingCached(CacheKey cacheKey);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Refreshing inner cache cacheKey {CacheKey} at expiration {Expiration}")]
    private partial void LogRefreshingInnerCacheKey(CacheKey cacheKey, DateTimeOffset? expiration);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inner cache refresh value for cacheKey {CacheKey}")]
    private partial void LogInnerCacheRefreshError(Exception ex, CacheKey cacheKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inner cache contains for cacheKey {CacheKey}")]
    private partial void LogInnerCacheContainsError(Exception ex, CacheKey cacheKey);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Set metadata for cacheKey {CacheKey}")]
    private partial void LogSetMetadata(CacheKey cacheKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inner cache set metadata for cacheKey {CacheKey} failed")]
    private partial void LogInnerCacheSetMetadataFailed(CacheKey cacheKey);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Clearing local cached. cacheKey {CacheKey}")]
    private partial void LogClearingLocalCached(CacheKey cacheKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inner cache remove cacheKey {CacheKey}")]
    private partial void LogInnerCacheRemoveError(Exception ex, CacheKey cacheKey);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Found local. {CacheKey}")]
    private partial void LogFoundLocal(CacheKey cacheKey);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Inner cache is not connected. Using local copy for cacheKey {CacheKey}")]
    private partial void LogUsingLocalCopyDisconnected(CacheKey cacheKey);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Inner cache is not connected. Returning default for cacheKey {CacheKey}")]
    private partial void LogReturningDefaultDisconnected(CacheKey cacheKey);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Found inner copy at cacheKey {CacheKey}")]
    private partial void LogFoundInnerCopy(CacheKey cacheKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inner cache set value for {CacheKey}")]
    private partial void LogInnerCacheSetError(Exception ex, CacheKey cacheKey);
}
