using UiPath.Platform.Caching.Telemetry;

namespace UiPath.Platform.Caching.Memory;

public sealed class MemCache : MemCacheBase, IMemCache
{
    private readonly ILogger<MemCache> _logger;

    public MemCache(
        Func<MemCacheOptions, IMemoryCache> memoryCacheAccessor,
        IChangeTokenFactory changeTokenFactory,
        ICachingTelemetryProvider telemetryProvider,
        IOptions<MemCacheOptions> optionsAccessor,
        ILogger<MemCache> logger)
        : base(memoryCacheAccessor, changeTokenFactory, telemetryProvider, optionsAccessor)
    {
        _logger = logger;
    }

    public string? InstanceName => CacheOptions.InstanceName;

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
            LocalSet(cacheEntryOptions, ret);
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
            return await RemoveAsync(cacheEntryOptions).ConfigureAwait(false);
        }

        _logger.LogDebug("Replacing cached key {}", cacheEntryOptions.Key);
        LocalSet(cacheEntryOptions, value);
        return true;
    }

    public Task<bool> RemoveAsync<T>(string key, CancellationToken token = default) =>
        RemoveAsync(BuildEntryOptions(key, default, token));

    public Task<bool> RefreshAsync<T>(string key, CancellationToken token = default) =>
        RefreshAsync<T>(key, CacheOptions.DefaultExpiration, token);

    public Task<bool> RefreshAsync<T>(string key, TimeSpan? expiration = null, CancellationToken token = default) =>
        RefreshAsync<T>(key, ToDateTimeOffset(expiration), token);

    public Task<bool> RefreshAsync<T>(string key, DateTimeOffset? expiration = null, CancellationToken token = default)
    {
        var cacheEntryOptions = BuildEntryOptions(key, expiration, token);
        
        try
        {
            if (MemoryCache.TryGetValue(cacheEntryOptions.Key, out ICacheEntry<T?>? cacheEntry))
            {
                _logger.LogTrace("Refreshing cache key {} at expiration {}", cacheEntryOptions.Key, cacheEntryOptions.Expiration);
                cacheEntry = CacheEntryFactory.Create(cacheEntry!.Value, cacheEntryOptions.Expiration);
                MemoryCache.Set(cacheEntryOptions.Key, cacheEntry);
                return Task.FromResult(true);
            }
            return Task.FromResult(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cache refresh value for key {}", key);
            return Task.FromResult(false);
        }
    }

    public Task<bool> ContainsAsync(string key, CancellationToken token = default)
    {
        var cacheEntryOptions = BuildEntryOptions(key, default, token);
        return Task.FromResult(MemoryCache.TryGetValue(cacheEntryOptions.Key, out _));
    }

    public Task<TimeSpan?> TimeToLiveAsync(string key, CancellationToken token = default)
    {
        var ret = MemoryCache.TryGetValue(key, out ICacheEntry? value)
            ? value?.Expiration.Subtract(Clock.UtcNow)
            : null;
        return Task.FromResult(ret);
    }

    public Task<DateTimeOffset?> ExpireTimeAsync(string key, CancellationToken token = default)
    {
        var ret = MemoryCache.TryGetValue(key, out ICacheEntry? value)
            ? value?.Expiration
            : null;
        return Task.FromResult(ret);
    }

    private Task<bool> RemoveAsync(CacheEntryOptions options)
    {
        _logger.LogDebug("Clearing cache. Key {}", options.Key);
        MemoryCache.Remove(options.Key);
        return Task.FromResult(true);
    }

    private Task<T?> GetAsync<T>(CacheEntryOptions options)
    {
        T? ret = default;
        if (MemoryCache.TryGetValue(options.Key, out ICacheEntry<T>? entry))
        {
            _logger.LogTrace("Found local. {}", options.Key);
            ret = entry == null ? default : entry.Value;
        }

        return Task.FromResult(ret);
    }

    private void LocalSet<T>(CacheEntryOptions options, T value)
    {
        var tokenKey = GetInnerCacheKey(options);
        var token = ChangeTokenFactory.Create(options.Key, tokenKey);
        var item = CacheEntryFactory.Create(value, options.Expiration);

        var memOptions = new MemoryCacheEntryOptions();
        memOptions.SetAbsoluteExpiration(options.Expiration);
        if (token is IChangeToken changeToken)
        {
            memOptions.ExpirationTokens.Add(changeToken);
            memOptions.RegisterPostEvictionCallback(PostEviction, changeToken);
        }

        MemoryCache.Set(options.Key, item, memOptions);


        static void PostEviction(object key, object? value, EvictionReason reason, object? state)
        {
            if (state is IDisposable disposable)
            {
                disposable.Dispose();
            }
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

    private string GetInnerCacheKey(CacheEntryOptions options) =>
        CacheUtils.GetKey(options.Key, InstanceName);

    private static bool IsDefault<T>(T value) =>
        EqualityComparer<T>.Default.Equals(value, default);

    private record struct CacheEntryOptions(string Key, CancellationToken Token, DateTimeOffset Expiration = default);
}
