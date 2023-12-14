using System.Collections.Immutable;
using UiPath.Platform.Caching.Telemetry;

namespace UiPath.Platform.Caching;

public sealed class MultilayerHashCache : IHashCache
{
    private readonly ILogger _logger;
    private readonly IHashCache _innerCache;
    private readonly IMemoryCache _memoryCache;
    private readonly ICacheEntryFactory _cacheEntryFactory;
    private readonly IDisposable _monitor;
    private readonly IMultilayerCacheOptions _cacheOptions;
    private readonly CacheClock _clock;
    private readonly HashCacheEntryBuilder _entryBuilder;
    private readonly CacheEventPublisher _eventPublisher;
    private readonly HashLocalMemorySetter _localMemorySetter;


    public MultilayerHashCache(
        string cacheName,
        IHashCache innerCache,
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
        _monitor = _memoryCache.Monitor(_cacheOptions, telemetryProvider, GetType().Name);
        _clock = new CacheClock(_cacheOptions.Clock, _cacheOptions.DefaultExpiration);
        var cacheKeyStrategy = _cacheOptions.CacheKeyStrategy ?? new DefaultCacheKeyStrategy();
        var topicKeyStrategy = _cacheOptions.TopicKeyStrategy ?? new DefaultTopicKeyStrategy();
        _entryBuilder = new HashCacheEntryBuilder(cacheKeyStrategy, topicKeyStrategy, _clock);
        _eventPublisher = new CacheEventPublisher(cacheName, _cacheOptions.Topic, topicFactory, cacheEventFactory, logger);
        _localMemorySetter = new HashLocalMemorySetter(cacheName, changeTokenFactory, topicFactory, _memoryCache, logger, _clock, _cacheOptions);
    }

    public async ValueTask<T?> GetItemAsync<T>(CacheKey cacheKey, string field, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var cacheEntry = await GetCacheEntryAsync<T>(_entryBuilder.BuildEntryOptions<T>(cacheKey, new[] { field }, default, token: token));
        if (cacheEntry.Value == null)
        {
            return default;
        }

        return cacheEntry.Value.TryGetValue(field, out var value) ? value : default(T?);
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

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<ValueTask<IDictionary<string, T?>>> generator, CancellationToken token = default) =>
        GetOrAddAsync(cacheKey, generator, _cacheOptions.DefaultExpiration, token);

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<ValueTask<IDictionary<string, T?>>> generator, TimeSpan? expiration = null, CancellationToken token = default) =>
        GetOrAddAsync(cacheKey, generator, _clock.ToDateTimeOffset(expiration), HashCacheSetOption.KeyReplace, token);

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<ValueTask<IDictionary<string, T?>>> generator, DateTimeOffset? expiration = null, CancellationToken token = default) =>
        GetOrAddAsync(cacheKey, generator, expiration, HashCacheSetOption.KeyReplace, token);

    public async ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<ValueTask<IDictionary<string, T?>>> generator, DateTimeOffset? expiration = null, HashCacheSetOption? setOption = null, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var cacheEntryOptions = _entryBuilder.BuildEntryOptions<T>(cacheKey, expiration, setOption ?? HashCacheSetOption.KeyReplace, token);
        var cacheEntry = await GetCacheEntryAsync<T>(cacheEntryOptions).ConfigureAwait(false);
        if (!IsDefault(cacheEntry.Value))
        {
            return cacheEntry.Value!;
        }

        _logger.LogDebug("Cache missed. generating new {}", cacheEntryOptions.CacheKey);
        var ret = await generator().ConfigureAwait(false);

        if (!IsDefault(ret))
        {
            await InternalSetAsync(cacheEntryOptions, ret).ConfigureAwait(false);
        }
        return ret;
    }

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, CancellationToken token = default) =>
        SetAsync(cacheKey, values, _cacheOptions.DefaultExpiration, token);

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, TimeSpan? expiration = null, CancellationToken token = default) =>
        SetAsync(cacheKey, values, _clock.ToDateTimeOffset(expiration), token);

    public async ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, DateTimeOffset? expiration = null, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var options = _entryBuilder.BuildEntryOptions<T>(cacheKey, expiration, token: token);
        if (IsDefault(values))
        {
            return await RemoveAsync<T>(options).ConfigureAwait(false);
        }

        _logger.LogDebug("Replacing cached cacheKey {}", options.CacheKey);
        await _eventPublisher.CacheSetAsync<T>(options).ConfigureAwait(false);
        return await InternalSetAsync(options, values).ConfigureAwait(false);
    }

    public async ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, HashCacheEntryOptions options, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var expiration = options.ExpireTime.HasValue ? _clock.ToDateTimeOffset(options.ExpireTime) : _clock.ToDateTimeOffset(options.TimeToLive);
        var cacheEntryOptions = _entryBuilder.BuildEntryOptions<T>(cacheKey, expiration, options.SetOption, token);
        cacheEntryOptions.Metadata = options.Metadata;
        if (IsDefault(values))
        {
            return await RemoveAsync<T>(cacheEntryOptions).ConfigureAwait(false);
        }

        _logger.LogDebug("Replacing cached cacheKey {}", cacheEntryOptions.CacheKey);
        await _eventPublisher.CacheSetAsync<T>(cacheEntryOptions).ConfigureAwait(false);
        return await InternalSetAsync(cacheEntryOptions, values).ConfigureAwait(false);
    }

    public ValueTask<bool> RemoveAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return RemoveAsync<T>(_entryBuilder.BuildEntryOptions<T>(cacheKey, default, token: token));
    }

    public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, CancellationToken token = default) =>
        RefreshAsync<T>(cacheKey, _cacheOptions.DefaultExpiration, token);

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
        _logger.LogDebug("Clearing cached. cacheKey {}", cacheEntryOptions.CacheKey);

        _memoryCache.Remove(cacheEntryOptions.CacheKey);
        _logger.LogTrace("Refreshing inner cache cacheKey {} at expiration {}", cacheEntryOptions.CacheKey, cacheEntryOptions.Expiration);
        try
        {
            await _innerCache.RefreshAsync<T>(cacheEntryOptions.CacheKey, options, token).ConfigureAwait(false);
            await _eventPublisher.CacheRefreshedAsync<T>(cacheEntryOptions).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Inner cache refresh value for cacheKey {}", cacheEntryOptions.CacheKey);
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
            _logger.LogWarning(ex, "Inner cache contains for cacheKey {}", cacheEntryOptions.CacheKey);
            return false;
        }
    }

    public async ValueTask<TimeSpan?> TimeToLiveAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var cacheEntryOptions = _entryBuilder.BuildEntryOptions<T>(cacheKey, default, token: token);
        return _memoryCache.TryGetValue(cacheEntryOptions.CacheKey, out ICacheEntry? value)
            ? value?.Expiration.Subtract(_clock.UtcNow)
            : await _innerCache.TimeToLiveAsync<T>(cacheEntryOptions.CacheKey, token);
    }

    public async ValueTask<DateTimeOffset?> ExpireTimeAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var cacheEntryOptions = _entryBuilder.BuildEntryOptions<T>(cacheKey, default, token: token);
        return _memoryCache.TryGetValue(cacheEntryOptions.CacheKey, out ICacheEntry? value)
            ? value?.Expiration
            : await _innerCache.ExpireTimeAsync<T>(cacheEntryOptions.CacheKey, token);
    }

    public async ValueTask<IDictionary<string, string?>?> GetMetadataAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var options = _entryBuilder.BuildEntryOptions<T>(cacheKey, _clock.ToDateTimeOffset(_cacheOptions.DefaultExpiration), token: token);
        return _memoryCache.TryGetValue(options.CacheKey, out ICacheEntry? entry)
            ? (entry?.Metadata)
            : await _innerCache.GetMetadataAsync<T>(options.CacheKey, token).ConfigureAwait(false);
    }

    public async ValueTask<bool> SetMetadataAsync<T>(CacheKey cacheKey, IDictionary<string, string?> metadata, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var cacheEntryOptions = _entryBuilder.BuildEntryOptions<T>(cacheKey, token);
        _logger.LogTrace("Set metadata for cacheKey {}", cacheEntryOptions.CacheKey);
        try
        {
            var response = await _innerCache.SetMetadataAsync<T>(cacheEntryOptions.CacheKey, metadata, cacheEntryOptions.Token).ConfigureAwait(false);
            if (!response)
            {
                _logger.LogWarning("Inner cache set metadata for cacheKey {} failed", cacheEntryOptions.CacheKey);
                return false;
            }

            if(_memoryCache.TryGetValue(cacheEntryOptions.CacheKey, out ICacheEntry? entry) && entry != null)
            {
                cacheEntryOptions.Expiration = entry.Expiration;
            }
            else
            {
                var expiration = await _innerCache.ExpireTimeAsync<T>(cacheEntryOptions.CacheKey, cacheEntryOptions.Token).ConfigureAwait(false);
                cacheEntryOptions.Expiration = _clock.ToDateTimeOffset(expiration);
            }

            cacheEntryOptions.Metadata = metadata;
            await _eventPublisher.MetadataUpdatedAsync<T>(cacheEntryOptions).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _memoryCache.Remove(cacheEntryOptions.CacheKey);
            _logger.LogWarning(ex, "Inner cache refresh value for cacheKey {}", cacheEntryOptions.CacheKey);
            return false;
        }
    }

    public void Dispose()
    {
        _monitor.Dispose();
        _memoryCache.Dispose();
    }

    private async ValueTask<bool> RemoveAsync<T>(InternalHashCacheEntryOptions options)
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

    private async ValueTask<ICacheEntry<IDictionary<string, T?>>> GetCacheEntryAsync<T>(InternalHashCacheEntryOptions options)
    {
        if (_memoryCache.TryGetValue<ICacheEntry<IDictionary<string, T?>>>(options.CacheKey, out var cacheEntry))
        {
            _logger.LogTrace("Found local. {}", options.CacheKey);
            return Filter(cacheEntry!, options);
        }

        cacheEntry = await _innerCache.GetCacheEntryAsync<T>(options.CacheKey, options.Token).ConfigureAwait(false);

        if (IsDefault(cacheEntry.Value))
        {
            return cacheEntry!;
        }

        _logger.LogTrace("Found inner copy at cacheKey {}", options.CacheKey);
        options.Expiration = await _innerCache.ExpireTimeAsync<T>(options.CacheKey, options.Token).ConfigureAwait(false) ?? _clock.DefaultDateTimeOffset();
        options.Metadata = cacheEntry.Metadata;
        var values = cacheEntry.Value!;
        MemorySet(options, values);

        return Filter(cacheEntry!, options);
    }

    private void MemorySet<T>(InternalHashCacheEntryOptions options, IDictionary<string, T?> value)
    {
        var item = CreateEntry(value, options);
        _localMemorySetter.Set(options, item, typeof(T), _cacheOptions.PrimaryMaxExpiration);
    }

    private async ValueTask<bool> InternalSetAsync<T>(InternalHashCacheEntryOptions options, IDictionary<string, T?> value)
    {
        try
        {
            MemorySet(options, value);
            return await _innerCache.SetAsync<T?>(options.CacheKey, value, new HashCacheEntryOptions(options.Expiration, null, options.Metadata, options.SetOption), options.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Inner cache set value for {}", options.CacheKey);
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

    private static bool IsDefault<T>(IDictionary<string, T?>? value) =>
        value == null || value.Count == 0;

    private static ImmutableDictionary<string, T?> Empty<T>() =>
        ImmutableDictionary<string, T?>.Empty;
}
