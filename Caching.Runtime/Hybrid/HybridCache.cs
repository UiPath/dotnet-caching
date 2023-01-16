using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using UiPath.Platform.Caching.Broadcast;
using UiPath.Platform.Caching.Redis;

namespace UiPath.Platform.Caching.Hybrid;

public sealed class HybridCache : HybridCacheBase, IHybridCache
{
    private readonly ILogger<HybridCache> _logger;
    private readonly ICache _innerCache;

    public HybridCache(
        ICache innerCache,
        Func<IMemoryCache> memoryCacheAccessor,
        IChangeTokenFactory changeTokenFactory,
        IChannelPublisher channelPublisher,
        IChannelResolver channelResolver,
        IOptions<HybridCacheOptions> optionsAccessor,
        ILogger<HybridCache> logger)
        : base(memoryCacheAccessor, changeTokenFactory, channelPublisher, channelResolver, optionsAccessor)
    {
        _innerCache = innerCache;
        _logger = logger;
    }

    public string? InstanceName => _innerCache.InstanceName;

    public Task<T?> GetAsync<T>(string key, CancellationToken token = default) =>
        GetAsync<T>(BuildEntryOptions(key, ToDateTimeOffset(CacheOptions.DefaultExpiration), token));

    public Task<T?> GetOrAddAsync<T>(string key, Func<Task<T?>> generator, CancellationToken token = default) =>
        GetOrAddAsync(key, generator, CacheOptions.DefaultExpiration, token);

    public Task<T?> GetOrAddAsync<T>(string key, Func<Task<T?>> generator, TimeSpan? expiration = null, CancellationToken token = default) =>
        GetOrAddAsync(key, generator, ToDateTimeOffset(expiration), token);

    public async Task<T?> GetOrAddAsync<T>(string key, Func<Task<T?>> generator, DateTimeOffset? expiration = null, CancellationToken token = default)
    {
        var cacheEntryOptions = BuildEntryOptions(key, expiration, token);

        var ret = await GetAsync<T>(cacheEntryOptions).ConfigureAwait(false);
        if (!IsDefault(ret))
        {
            return ret;
        }

        _logger.LogDebug("Cache missed. generating new {}", cacheEntryOptions.Key);
        ret = await generator().ConfigureAwait(false);

        if (!IsDefault(ret))
        {
            await LocalAndRegionalSetAsync(cacheEntryOptions, ret).ConfigureAwait(false);
        }
        return ret;
    }

    public Task<bool> SetAsync<T>(string key, T? value, CancellationToken token = default) =>
        SetAsync(key, value, CacheOptions.DefaultExpiration, token);

    public Task<bool> SetAsync<T>(string key, T? value, TimeSpan? expiration = null, CancellationToken token = default) =>
        SetAsync(key, value, ToDateTimeOffset(expiration), token);

    public async Task<bool> SetAsync<T>(string key, T? value, DateTimeOffset? expiration = null, CancellationToken token = default)
    {
        var cacheEntryOptions = BuildEntryOptions(key, expiration, token);
        if (IsDefault(value))
        {
            return await RemoveAsync<T>(cacheEntryOptions).ConfigureAwait(false);
        }

        _logger.LogDebug("Replacing cached key {}", cacheEntryOptions.Key);
        await RaiseClearCacheEventAsync<T>(cacheEntryOptions).ConfigureAwait(false);
        return await LocalAndRegionalSetAsync(cacheEntryOptions, value).ConfigureAwait(false);
    }

    public Task<bool> RemoveAsync<T>(string key, CancellationToken token = default) =>
        RemoveAsync<T>(BuildEntryOptions(key, default, token));

    public Task RefreshAsync<T>(string key, CancellationToken token = default) =>
        RefreshAsync<T>(key, CacheOptions.DefaultExpiration, token);

    public Task RefreshAsync<T>(string key, TimeSpan? expiration = null, CancellationToken token = default) =>
        RefreshAsync<T>(key, ToDateTimeOffset(expiration), token);

    public async Task RefreshAsync<T>(string key, DateTimeOffset? expiration = null, CancellationToken token = default)
    {
        var cacheEntryOptions = BuildEntryOptions(key, expiration, token);
        _logger.LogDebug("Clearing cached. Key {}", cacheEntryOptions.Key);
        MemoryCache.Remove(key);
        _logger.LogTrace("Refreshing inner cache key {} at expiration {}", cacheEntryOptions.Key, cacheEntryOptions.Expiration);
        try
        {
            await _innerCache.RefreshAsync<T>(cacheEntryOptions.Key, cacheEntryOptions.Expiration, token).ConfigureAwait(false);
            await RaiseClearCacheEventAsync<T>(cacheEntryOptions).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Inner cache refresh value for key {}", key);
        }
    }

    public async Task<bool> ContainsAsync(string key, CancellationToken token = default)
    {
        var cacheEntryOptions = BuildEntryOptions(key, default, token);
        try
        {
            return MemoryCache.TryGetValue(cacheEntryOptions.Key, out _) || await _innerCache.ContainsAsync(cacheEntryOptions.Key, cacheEntryOptions.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Inner cache contains for key {}", key);
            return false;
        }
    }

    public async Task<TimeSpan?> TimeToLiveAsync(string key, CancellationToken token = default) =>
        MemoryCache.TryGetValue(key, out ICacheEntry? value)
            ? value?.Expiration.Subtract(Clock.UtcNow)
            : await _innerCache.TimeToLiveAsync(key, token);

    public async Task<DateTimeOffset?> ExpireTimeAsync(string key, CancellationToken token = default) =>
        MemoryCache.TryGetValue(key, out ICacheEntry? value)
            ? value?.Expiration
            : await _innerCache.ExpireTimeAsync(key, token);

    private async Task<bool> RemoveAsync<T>(CacheEntryOptions options)
    {
        _logger.LogDebug("Clearing local cached. Key {}", options.Key);
        MemoryCache.Remove(options.Key);
        try
        {
            var ret = await _innerCache.RemoveAsync<T>(options.Key, options.Token).ConfigureAwait(false);
            await RaiseClearCacheEventAsync<T>(options).ConfigureAwait(false);
            return ret;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Inner cache remove key {}", options.Key);
            return false;
        }
    }

    private async Task<T?> GetAsync<T>(CacheEntryOptions options)
    {
        if (MemoryCache.TryGetValue(options.Key, out ICacheEntry<T>? entry))
        {
            _logger.LogTrace("Found local. {}", options.Key);
            return entry == null ? default : entry.Value;
        }

        var ret = await _innerCache.GetAsync<T>(options.Key, options.Token).ConfigureAwait(false);

        if (IsDefault(ret))
        {
            // we don't keep default values in IRegionCache => don't need a remove call;
            return default;
        }

        _logger.LogTrace("Found regional copy at key {}", options.Key);
        options.Expiration = await _innerCache.ExpireTimeAsync(options.Key, options.Token).ConfigureAwait(false) ?? DefaultDateTimeOffset();
        LocalSet(options, ret);

        return ret;
    }

    private void LocalSet<T>(CacheEntryOptions options, T value)
    {
        var tokenKey = GetInnerCacheKey(options);
        var channel = ChannelResolver.GetFor<T>(options.Key);
        var token = ChangeTokenFactory.Create(channel, tokenKey, CacheOptions.SourceUri);
        var item = CacheEntryFactory.Create(value, options.Expiration);

        var memOptions = new MemoryCacheEntryOptions();
        memOptions.SetAbsoluteExpiration(options.Expiration);
        memOptions.ExpirationTokens.Add(token);
        memOptions.RegisterPostEvictionCallback(PostEviction, token);

        MemoryCache.Set(options.Key, item, memOptions);

        static void PostEviction(object key, object? value, EvictionReason reason, object? state)
        {
            if (state is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }

    private async Task<bool> LocalAndRegionalSetAsync<T>(CacheEntryOptions options, T? value)
    {
        LocalSet(options, value);
        try
        {
            return await _innerCache.SetAsync(options.Key, value, options.Expiration, options.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Inner cache set value for {}", options.Key);
            return false;
        }
    }

    private CacheEntryOptions BuildEntryOptions(string key, DateTimeOffset? expiration, CancellationToken token = default)
    {
        if (key == null)
        {
            throw new ArgumentNullException(nameof(key));
        }
        token.ThrowIfCancellationRequested();
        return new CacheEntryOptions(key, token, ToDateTimeOffset(expiration));
    }

    private async Task RaiseClearCacheEventAsync<T>(CacheEntryOptions options)
    {
        var channel = ChannelResolver.GetFor<T>(options.Key);
        _logger.LogDebug("Raise clear cache event on channel {} for key {}", channel, options.Key);
        await ChannelPublisher.PublishAsync(channel, GetCloudEvent(options), options.Token).ConfigureAwait(false);
    }

    private CloudEvent GetCloudEvent(CacheEntryOptions options) =>
        GetCloudEvent(new ClearCacheEventData(GetInnerCacheKey(options)));

    private string GetInnerCacheKey(CacheEntryOptions options) =>
        CacheUtils.GetKey(options.Key, InstanceName);

    private static bool IsDefault<T>(T value) =>
        EqualityComparer<T>.Default.Equals(value, default);

    private record struct CacheEntryOptions(string Key, CancellationToken Token, DateTimeOffset Expiration = default);
}
