using System.Collections.Immutable;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using UiPath.Platform.Caching.Broadcast;
using UiPath.Platform.Caching.Redis;
using UiPath.Platform.Caching.Telemetry;

namespace UiPath.Platform.Caching.Hybrid;

public sealed class HybridRegionCache : HybridCacheBase, IHybridRegionCache
{
    private readonly ILogger<HybridRegionCache> _logger;
    private readonly IRegionCache _innerCache;

    public HybridRegionCache(
        IRegionCache innerCache,
        Func<HybridCacheOptions, IMemoryCache> memoryCacheAccessor,
        IChangeTokenFactory changeTokenFactory,
        IChannelPublisher channelPublisher,
        IChannelResolver channelResolver,
        IClearCacheEventFactory clearCacheEventFactory,
        ICachingTelemetryProvider telemetryProvider,
        IOptions<HybridCacheOptions> optionsAccessor,
        ILogger<HybridRegionCache> logger)
        : base(memoryCacheAccessor, changeTokenFactory, channelPublisher, channelResolver, clearCacheEventFactory, telemetryProvider, optionsAccessor)
    {
        _innerCache = innerCache;
        _logger = logger;
    }

    public string? InstanceName => _innerCache.InstanceName;

    public async Task<T?> GetItemAsync<T>(Region region, string key, CancellationToken token = default)
    {
        var cacheEntry = await GetCacheEntryAsync<T>(BuildEntryOptions(region, new[] { key }, default, token));
        if (cacheEntry.Value == null)
        {
            return default;
        }

        return cacheEntry.Value.TryGetValue(key, out var value) ? value : default;
    }

    public async Task<IDictionary<string, T?>> GetAsync<T>(Region region, CancellationToken token = default)
    {
        var cacheEntry = await GetCacheEntryAsync<T>(BuildEntryOptions(region, token));
        return cacheEntry.Value ?? Empty<T>();
    }

    public async Task<IDictionary<string, T?>> GetAsync<T>(Region region, string[] keys, CancellationToken token = default)
    {
        var cacheEntry = await GetCacheEntryAsync<T>(BuildEntryOptions(region, keys, default, token));
        return cacheEntry?.Value ?? Empty<T>();
    }

    public Task<ICacheEntry<IDictionary<string, T?>>> GetCacheEntryAsync<T>(Region region, CancellationToken token = default) =>
        GetCacheEntryAsync<T>(BuildEntryOptions(region, token));

    public Task<IDictionary<string, T?>> GetOrAddAsync<T>(Region region, Func<Task<IDictionary<string, T?>>> generator, CancellationToken token = default) =>
        GetOrAddAsync(region, generator, CacheOptions.DefaultExpiration, token);

    public Task<IDictionary<string, T?>> GetOrAddAsync<T>(Region region, Func<Task<IDictionary<string, T?>>> generator, TimeSpan? expiration = null, CancellationToken token = default) =>
        GetOrAddAsync(region, generator, ToDateTimeOffset(expiration), token);

    public async Task<IDictionary<string, T?>> GetOrAddAsync<T>(Region region, Func<Task<IDictionary<string, T?>>> generator, DateTimeOffset? expiration = null, CancellationToken token = default)
    {
        var cacheEntryOptions = BuildEntryOptions(region, expiration, token);
        var cacheEntry = await GetCacheEntryAsync<T>(cacheEntryOptions).ConfigureAwait(false);
        if (!IsDefault(cacheEntry.Value))
        {
            return cacheEntry.Value!;
        }

        _logger.LogDebug("Cache missed. generating new {}", cacheEntryOptions.Region);
        var ret = await generator().ConfigureAwait(false);

        if (!IsDefault(ret))
        {
            await LocalAndRegionalSetAsync(cacheEntryOptions, ret).ConfigureAwait(false);
        }
        return ret;
    }

    public Task<bool> SetAsync<T>(Region region, IDictionary<string, T?> values, CancellationToken token = default) =>
        SetAsync(region, values, CacheOptions.DefaultExpiration, token);

    public Task<bool> SetAsync<T>(Region region, IDictionary<string, T?> values, TimeSpan? expiration = null, CancellationToken token = default) =>
        SetAsync(region, values, ToDateTimeOffset(expiration), token);

    public async Task<bool> SetAsync<T>(Region region, IDictionary<string, T?> values, DateTimeOffset? expiration = null, CancellationToken token = default)
    {
        var options = BuildEntryOptions(region, expiration, token);
        if (IsDefault(values))
        {
            return await RemoveAsync<T>(options).ConfigureAwait(false);
        }

        _logger.LogDebug("Replacing cached region {}", options.Region);
        await RaiseClearCacheEventAsync<T>(options).ConfigureAwait(false);
        return await LocalAndRegionalSetAsync(options, values).ConfigureAwait(false);
    }

    public async Task<bool> SetAsync<T>(Region region, IDictionary<string, T?> values, RegionCacheEntryOptions options, CancellationToken token = default)
    {
        var expiration = options.ExpireTime.HasValue ? ToDateTimeOffset(options.ExpireTime) : ToDateTimeOffset(options.TimeToLive);
        var cacheEntryOptions = BuildEntryOptions(region, expiration, token);
        cacheEntryOptions.ExtendedProperties = options.ExtendedProperties;
        if (IsDefault(values))
        {
            return await RemoveAsync<T>(cacheEntryOptions).ConfigureAwait(false);
        }

        _logger.LogDebug("Replacing cached region {}", cacheEntryOptions.Region);
        await RaiseClearCacheEventAsync<T>(cacheEntryOptions).ConfigureAwait(false);
        return await LocalAndRegionalSetAsync(cacheEntryOptions, values).ConfigureAwait(false);
    }

    public Task<bool> RemoveAsync<T>(Region region, CancellationToken token = default) =>
        RemoveAsync<T>(BuildEntryOptions(region, default, token));

    public Task<bool> RefreshAsync<T>(Region region, CancellationToken token = default) =>
        RefreshAsync<T>(region, CacheOptions.DefaultExpiration, token);

    public Task<bool> RefreshAsync<T>(Region region, TimeSpan? expiration = null, CancellationToken token = default) =>
        RefreshAsync<T>(region, ToDateTimeOffset(expiration), token);

    public Task<bool> RefreshAsync<T>(Region region, DateTimeOffset? expiration = null, CancellationToken token = default) =>
        RefreshAsync<T>(region, new RegionCacheEntryOptions(expiration), token);

    public async Task<bool> RefreshAsync<T>(Region region, RegionCacheEntryOptions options, CancellationToken token = default)
    {
        var expiration = options.ExpireTime.HasValue ? ToDateTimeOffset(options.ExpireTime) : ToDateTimeOffset(options.TimeToLive);
        var cacheEntryOptions = BuildEntryOptions(region, expiration, token);
        cacheEntryOptions.ExtendedProperties = options.ExtendedProperties;
        _logger.LogDebug("Clearing cached. Region {}", cacheEntryOptions.Region);

        MemoryCache.Remove(cacheEntryOptions.Region);
        _logger.LogTrace("Refreshing inner cache Region {} at expiration {}", cacheEntryOptions.Region, cacheEntryOptions.Expiration);
        try
        {
            await _innerCache.RefreshAsync<T>(cacheEntryOptions.Region, options, token).ConfigureAwait(false);
            await RaiseClearCacheEventAsync<T>(cacheEntryOptions).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Inner cache refresh value for Region {}", cacheEntryOptions.Region);
            return false;
        }
    }

    public async Task<bool> ContainsAsync(Region region, CancellationToken token = default)
    {
        var cacheEntryOptions = BuildEntryOptions(region, default, token);
        try
        {
            return MemoryCache.TryGetValue(cacheEntryOptions.Region, out _) || await _innerCache.ContainsAsync(cacheEntryOptions.Region, cacheEntryOptions.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Inner cache contains for Region {}", cacheEntryOptions.Region);
            return false;
        }
    }

    public async Task<TimeSpan?> TimeToLiveAsync(Region region, CancellationToken token = default) =>
        MemoryCache.TryGetValue(region, out ICacheEntry? value)
            ? value?.Expiration.Subtract(Clock.UtcNow)
            : await _innerCache.TimeToLiveAsync(region, token);

    public async Task<DateTimeOffset?> ExpireTimeAsync(Region region, CancellationToken token = default) =>
        MemoryCache.TryGetValue(region, out ICacheEntry? value)
            ? value?.Expiration
            : await _innerCache.ExpireTimeAsync(region, token);

    private async Task<bool> RemoveAsync<T>(CacheEntryOptions options)
    {
        _logger.LogDebug("Clearing local cached. Region {}", options.Region);
        MemoryCache.Remove(options.Region);
        try
        {
            var ret = await _innerCache.RemoveAsync<T>(options.Region, options.Token).ConfigureAwait(false);
            await RaiseClearCacheEventAsync<T>(options).ConfigureAwait(false);
            return ret;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Inner cache remove Region {}", options.Region);
            return false;
        }
    }

    private async Task<ICacheEntry<IDictionary<string, T?>>> GetCacheEntryAsync<T>(CacheEntryOptions options)
    {
        if (MemoryCache.TryGetValue(options.Region, out ICacheEntry<IDictionary<string, T?>>? cacheEntry))
        {
            _logger.LogTrace("Found local. {}", options.Region);
            return Filter(cacheEntry!, options);
        }

        cacheEntry = await _innerCache.GetCacheEntryAsync<T>(options.Region, options.Token).ConfigureAwait(false);

        if (IsDefault(cacheEntry.Value))
        {
            return cacheEntry!;
        }

        _logger.LogTrace("Found regional copy at Region {}", options.Region);
        options.Expiration = await _innerCache.ExpireTimeAsync(options.Region, options.Token).ConfigureAwait(false) ?? DefaultDateTimeOffset();
        options.ExtendedProperties = cacheEntry.ExtendedProperties;
        var values = cacheEntry.Value!;
        LocalSet(options, values);

        return Filter(cacheEntry!, options);
    }

    private ICacheEntry<IDictionary<string, T?>> Filter<T>(ICacheEntry<IDictionary<string, T?>> cacheEntry, CacheEntryOptions options)
    {
        if (options.Keys == null || cacheEntry.Value == null)
        {
            return cacheEntry;
        }
        var allKeys = options.Keys.ToHashSet(StringComparer.InvariantCultureIgnoreCase);
        var values = cacheEntry.Value.Where(kv => allKeys.Contains(kv.Key)).ToImmutableDictionary(kv => kv.Key, kv => kv.Value);
        return CreateEntry(values, options);
    }

    private void LocalSet<T>(CacheEntryOptions options, IDictionary<string, T?> value)
    {
        var item = CreateEntry(value, options);
        LocalSet(options, item, typeof(T));
    }

    private void LocalSet(CacheEntryOptions options, ICacheEntry item, Type entryType)
    {
        var tokenKey = GetInnerCacheKey(options.Region);
        var channel = ChannelResolver.GetFor(entryType, tokenKey);
        var token = ChangeTokenFactory.Create(channel, tokenKey, CacheOptions.SourceUri);

        if (token is IExtendedPropertiesChangeToken channelChangeToken && (item.ExtendedProperties?.Any() ?? false))
        {
            var state = new RefreshExtendedPropertiesState(options.Region, item, channelChangeToken, entryType);
            token.RegisterChangeCallback(RefreshExtendedProperties, state);
        }
        var memOptions = new MemoryCacheEntryOptions();
        memOptions.SetAbsoluteExpiration(options.Expiration);
        memOptions.ExpirationTokens.Add(token);
        memOptions.RegisterPostEvictionCallback(PostEviction, token);

        MemoryCache.Set(options.Region, item, memOptions);

        static void PostEviction(object key, object? value, EvictionReason reason, object? state)
        {
            if (state is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }

    private void RefreshExtendedProperties(object? state)
    {
        if (state is RefreshExtendedPropertiesState propertiesState && propertiesState.Token.ExtendedPropertiesHasChanged)
        {
            try
            {
                using var cts = new CancellationTokenSource();
                cts.CancelAfter(CacheOptions.Timeout);
                var extendeProps = _innerCache.GetExtendedPropertiesAsync(propertiesState.Region, cts.Token).GetAwaiter().GetResult();
                if (extendeProps == null)
                {
                    return;
                }

                var expiration = _innerCache.ExpireTimeAsync(propertiesState.Region, cts.Token).GetAwaiter().GetResult();
                var options = new CacheEntryOptions(propertiesState.Region, null, cts.Token, ToDateTimeOffset(expiration), extendeProps);
                var newEntry = propertiesState.CacheEntity.NewEntry(options.Expiration, options.ExtendedProperties);

                LocalSet(options, newEntry, propertiesState.EntryType);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unable to refresh cache region {}", propertiesState.Region);
            }
        }
    }

    private ICacheEntry<IDictionary<string, T?>> CreateEntry<T>(IDictionary<string, T?> values, CacheEntryOptions options) =>
        CacheEntryFactory.Create<IDictionary<string, T?>>(values.ToImmutableDictionary(), options.Expiration, options.ExtendedProperties?.ToImmutableDictionary());

    private async Task<bool> LocalAndRegionalSetAsync<T>(CacheEntryOptions options, IDictionary<string, T?> value)
    {
        LocalSet(options, value);
        try
        {
            return await _innerCache.SetAsync(options.Region, value, new RegionCacheEntryOptions(options.Expiration, null, options.ExtendedProperties), options.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Inner cache set value for {}", options.Region);
            return false;
        }
    }

    private CacheEntryOptions BuildEntryOptions(Region region, CancellationToken token = default)
        => BuildEntryOptions(region, default, token);

    private CacheEntryOptions BuildEntryOptions(Region region, DateTimeOffset? expiration, CancellationToken token = default)
        => BuildEntryOptions(region, default, expiration, token);

    private CacheEntryOptions BuildEntryOptions(Region region, string[]? keys, DateTimeOffset? expiration, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        return new CacheEntryOptions(region, keys, token, ToDateTimeOffset(expiration));
    }

    private static bool IsDefault<T>(IDictionary<string, T?>? value) =>
        value == null || value.Count == 0;

    private static IDictionary<string, T?> Empty<T>() =>
        ImmutableDictionary<string, T?>.Empty;

    public async Task<IDictionary<string, string?>?> GetExtendedPropertiesAsync(Region region, CancellationToken token = default)
    {
        var options = BuildEntryOptions(region, ToDateTimeOffset(CacheOptions.DefaultExpiration), token);
        return MemoryCache.TryGetValue(options.Region, out ICacheEntry? entry)
            ? (entry?.ExtendedProperties)
            : await _innerCache.GetExtendedPropertiesAsync(options.Region, token).ConfigureAwait(false);
    }

    public async Task<bool> SetExtendedPropertiesAsync<T>(Region region, IDictionary<string, string?> extendedProperties, CancellationToken token = default)
    {
        var cacheEntryOptions = BuildEntryOptions(region, token);
        _logger.LogTrace("Set ExtendedProperties for Region {}", cacheEntryOptions.Region);
        try
        {
            var response = await _innerCache.SetExtendedPropertiesAsync<T>(cacheEntryOptions.Region, extendedProperties, cacheEntryOptions.Token).ConfigureAwait(false);
            await RaiseClearExtendedPropertiesCacheEventAsync<T>(cacheEntryOptions).ConfigureAwait(false);

            return response;
        }
        catch (Exception ex)
        {
            MemoryCache.Remove(cacheEntryOptions.Region);
            _logger.LogWarning(ex, "Inner cache refresh value for Region {}", cacheEntryOptions.Region);
            return false;
        }
    }

    private async Task RaiseClearExtendedPropertiesCacheEventAsync<T>(CacheEntryOptions options)
    {
        var channel = ChannelResolver.GetFor<T>(options.Region);
        _logger.LogDebug("Raise clear extended properties cache event on channel {} for region {}", channel, options.Region);

        await ChannelPublisher.PublishAsync(channel, GetCloudEvent(options, new[] { CacheConstants.ExtendedPropertiesKey }), options.Token).ConfigureAwait(false);
    }

    private async Task RaiseClearCacheEventAsync<T>(CacheEntryOptions options)
    {
        var channel = ChannelResolver.GetFor<T>(options.Region);
        _logger.LogDebug("Raise clear cache event on channel {} for region {}", channel, options.Region);
        await ChannelPublisher.PublishAsync(channel, GetCloudEvent(options), options.Token).ConfigureAwait(false);
    }

    private IClearCacheEvent GetCloudEvent(CacheEntryOptions options, string[]? fields = null) =>
        CreateEvent(new ClearCacheEventData(GetInnerCacheKey(options.Region), fields));

    private string GetInnerCacheKey(Region region) =>
        CacheUtils.GetKey(region.ToString(), InstanceName);

    private record struct CacheEntryOptions(Region Region, string[]? Keys, CancellationToken Token, DateTimeOffset Expiration = default, IDictionary<string, string?>? ExtendedProperties = default);

    private record struct RefreshExtendedPropertiesState(Region Region, ICacheEntry CacheEntity, IExtendedPropertiesChangeToken Token, Type EntryType);
}
