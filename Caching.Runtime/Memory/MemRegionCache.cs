using System.Collections.Immutable;
using UiPath.Platform.Caching.Telemetry;

namespace UiPath.Platform.Caching.Memory;

public sealed class MemRegionCache : MemCacheBase, IMemRegionCache
{
    private readonly ILogger<MemRegionCache> _logger;

    public MemRegionCache(
        Func<MemCacheOptions, IMemoryCache> memoryCacheAccessor,
        IChangeTokenFactory changeTokenFactory,
        ICachingTelemetryProvider telemetryProvider,
        IOptions<MemCacheOptions> optionsAccessor,
        ILogger<MemRegionCache> logger)
        : base(memoryCacheAccessor, changeTokenFactory, telemetryProvider, optionsAccessor)
    {
        _logger = logger;
    }

    public string? InstanceName => CacheOptions.InstanceName;

    public async Task<T?> GetItemAsync<T>(Region region, string key, CancellationToken token = default)
    {
        var cacheEntry = await GetCacheEntryAsync<T>(BuildEntryOptions(region, new[] { key }, default, token: token));
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
        var cacheEntry = await GetCacheEntryAsync<T>(BuildEntryOptions(region, keys, default, token: token));
        return cacheEntry?.Value ?? Empty<T>();
    }

    public Task<ICacheEntry<IDictionary<string, T?>>> GetCacheEntryAsync<T>(Region region, CancellationToken token = default) =>
        GetCacheEntryAsync<T>(BuildEntryOptions(region, token));

    public Task<IDictionary<string, T?>> GetOrAddAsync<T>(Region region, Func<Task<IDictionary<string, T?>>> generator, CancellationToken token = default) =>
        GetOrAddAsync(region, generator, CacheOptions.DefaultExpiration, token);

    public Task<IDictionary<string, T?>> GetOrAddAsync<T>(Region region, Func<Task<IDictionary<string, T?>>> generator, TimeSpan? expiration = null, CancellationToken token = default) =>
        GetOrAddAsync(region, generator, ToDateTimeOffset(expiration), RegionCacheSetOption.KeyReplace, token);

    public Task<IDictionary<string, T?>> GetOrAddAsync<T>(Region region, Func<Task<IDictionary<string, T?>>> generator, DateTimeOffset? expiration = null, CancellationToken token = default) =>
        GetOrAddAsync(region, generator, expiration, RegionCacheSetOption.KeyReplace, token);

    public async Task<IDictionary<string, T?>> GetOrAddAsync<T>(Region region, Func<Task<IDictionary<string, T?>>> generator, DateTimeOffset? expiration = null, RegionCacheSetOption setOption = RegionCacheSetOption.KeyReplace, CancellationToken token = default)
    {
        var cacheEntryOptions = BuildEntryOptions(region, expiration, setOption, token);
        var cacheEntry = await GetCacheEntryAsync<T>(cacheEntryOptions).ConfigureAwait(false);
        if (!IsDefault(cacheEntry.Value))
        {
            return cacheEntry.Value!;
        }

        _logger.LogDebug("Cache missed. generating new {}", cacheEntryOptions.Region);
        var ret = await generator().ConfigureAwait(false);

        if (!IsDefault(ret))
        {
            LocalSet(cacheEntryOptions, ret);
        }
        return ret;
    }

    public Task<bool> SetAsync<T>(Region region, IDictionary<string, T?> values, CancellationToken token = default) =>
        SetAsync(region, values, CacheOptions.DefaultExpiration, token);

    public Task<bool> SetAsync<T>(Region region, IDictionary<string, T?> values, TimeSpan? expiration = null, CancellationToken token = default) =>
        SetAsync(region, values, ToDateTimeOffset(expiration), token);

    public async Task<bool> SetAsync<T>(Region region, IDictionary<string, T?> values, DateTimeOffset? expiration = null, CancellationToken token = default)
    {
        var options = BuildEntryOptions(region, expiration, token: token);
        if (IsDefault(values))
        {
            await RemoveAsync(options).ConfigureAwait(false);
            return false;
        }

        _logger.LogDebug("Set cached region {}", options.Region);
        try
        {
            LocalSet(options, values);
            return true;
        }
        catch (Exception ex)
        {
            await RemoveAsync(options).ConfigureAwait(false);
            _logger.LogWarning(ex, "cache set value for {}", options.Region);
            return false;
        }
    }

    public async Task<bool> SetAsync<T>(Region region, IDictionary<string, T?> values, RegionCacheEntryOptions options, CancellationToken token = default)
    {
        var expiration = options.ExpireTime.HasValue ? ToDateTimeOffset(options.ExpireTime) : ToDateTimeOffset(options.TimeToLive);
        var cacheEntryOptions = BuildEntryOptions(region, expiration, options.SetOption, token);
        cacheEntryOptions.ExtendedProperties = options.ExtendedProperties;
        if (IsDefault(values))
        {
            await RemoveAsync(cacheEntryOptions).ConfigureAwait(false);
            return false;
        }

        _logger.LogDebug("Replacing cached region {}", cacheEntryOptions.Region);
        LocalSet(cacheEntryOptions, values);
        return true;
    }

    public Task<bool> RemoveAsync<T>(Region region, CancellationToken token = default) =>
        RemoveAsync(BuildEntryOptions(region, default, token: token));

    public Task<bool> RefreshAsync<T>(Region region, CancellationToken token = default) =>
        RefreshAsync<T>(region, CacheOptions.DefaultExpiration, token);

    public Task<bool> RefreshAsync<T>(Region region, TimeSpan? expiration = null, CancellationToken token = default) =>
        RefreshAsync<T>(region, ToDateTimeOffset(expiration), token);

    public Task<bool> RefreshAsync<T>(Region region, DateTimeOffset? expiration = null, CancellationToken token = default) =>
        RefreshAsync<T>(region, new RegionCacheEntryOptions(expiration), token);

    public Task<bool> RefreshAsync<T>(Region region, RegionCacheEntryOptions options, CancellationToken token = default)
    {
        var expiration = options.ExpireTime.HasValue ? ToDateTimeOffset(options.ExpireTime) : ToDateTimeOffset(options.TimeToLive);
        var cacheEntryOptions = BuildEntryOptions(region, expiration, token: token);
        cacheEntryOptions.ExtendedProperties = options.ExtendedProperties;
        try
        {
            if (MemoryCache.TryGetValue(cacheEntryOptions.Region, out ICacheEntry<IDictionary<string, T?>>? cacheEntry))
            {
                _logger.LogTrace("Refreshing cache Region {} at expiration {}", cacheEntryOptions.Region, cacheEntryOptions.Expiration);
                cacheEntry = CacheEntryFactory.Create(cacheEntry!.Value!, cacheEntryOptions.Expiration, cacheEntryOptions.ExtendedProperties);
                MemoryCache.Set(cacheEntryOptions.Region, cacheEntry);
                return Task.FromResult(true);
            }
            return Task.FromResult(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Inner cache refresh value for Region {}", cacheEntryOptions.Region);
            return Task.FromResult(false);
        }
    }

    public Task<bool> ContainsAsync(Region region, CancellationToken token = default)
    {
        var cacheEntryOptions = BuildEntryOptions(region, default, token: token);
        return Task.FromResult(MemoryCache.TryGetValue(cacheEntryOptions.Region, out _));
    }

    public Task<TimeSpan?> TimeToLiveAsync(Region region, CancellationToken token = default)
    {
        var ret = MemoryCache.TryGetValue(region, out ICacheEntry? value)
            ? value?.Expiration.Subtract(Clock.UtcNow)
            : null;
        return Task.FromResult(ret);
    }

    public Task<DateTimeOffset?> ExpireTimeAsync(Region region, CancellationToken token = default)
    {
        var ret = MemoryCache.TryGetValue(region, out ICacheEntry? value)
            ? value?.Expiration
            : null;
        return Task.FromResult(ret);
    }

    private Task<bool> RemoveAsync(CacheEntryOptions options)
    {
        _logger.LogDebug("Clearing local cached. Region {}", options.Region);
        MemoryCache.Remove(options.Region);
        return Task.FromResult(true);
    }

    private Task<ICacheEntry<IDictionary<string, T?>>> GetCacheEntryAsync<T>(CacheEntryOptions options)
    {
        ICacheEntry<IDictionary<string, T?>>? result = null;
        if (MemoryCache.TryGetValue(options.Region, out ICacheEntry<IDictionary<string, T?>>? cacheEntry))
        {
            _logger.LogTrace("Found local. {}", options.Region);
            result = Filter(cacheEntry!, options);
        }
        else
        {
            result = EmptyCacheEntry<T>();
        }
        return Task.FromResult(result);
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
        var memOptions = new MemoryCacheEntryOptions();
        memOptions.SetAbsoluteExpiration(options.Expiration);
        var token = ChangeTokenFactory.Create(entryType.Name, tokenKey);
        if (token is IChangeToken changeToken)
        {
            memOptions.ExpirationTokens.Add(changeToken);
            memOptions.RegisterPostEvictionCallback(PostEviction, changeToken);
        }

        MemoryCache.Set(options.Region, item, memOptions);

        static void PostEviction(object key, object? value, EvictionReason reason, object? state)
        {
            if (state is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }

    private ICacheEntry<IDictionary<string, T?>> CreateEntry<T>(IDictionary<string, T?> values, CacheEntryOptions options) =>
        CacheEntryFactory.Create<IDictionary<string, T?>>(values.ToImmutableDictionary(), options.Expiration, options.ExtendedProperties?.ToImmutableDictionary());

    private CacheEntryOptions BuildEntryOptions(Region region, CancellationToken token = default)
        => BuildEntryOptions(region, default, token: token);

    private CacheEntryOptions BuildEntryOptions(Region region, DateTimeOffset? expiration, RegionCacheSetOption setOption = RegionCacheSetOption.KeyReplace, CancellationToken token = default)
        => BuildEntryOptions(region, default, expiration, setOption, token);

    private CacheEntryOptions BuildEntryOptions(Region region, string[]? keys, DateTimeOffset? expiration, RegionCacheSetOption setOption = RegionCacheSetOption.KeyReplace, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        return new CacheEntryOptions(region, keys, token, ToDateTimeOffset(expiration), default, setOption);
    }

    private static bool IsDefault<T>(IDictionary<string, T?>? value) =>
        value == null || value.Count == 0;

    private static IDictionary<string, T?> Empty<T>() =>
        ImmutableDictionary<string, T?>.Empty;

    private ICacheEntry<IDictionary<string, T?>> EmptyCacheEntry<T>() =>
        CacheEntryFactory.Create(Empty<T>(), DateTimeOffset.MinValue);


    public Task<IDictionary<string, string?>?> GetExtendedPropertiesAsync(Region region, CancellationToken token = default)
    {
        var options = BuildEntryOptions(region, ToDateTimeOffset(CacheOptions.DefaultExpiration), token: token);
        var ret =  MemoryCache.TryGetValue(options.Region, out ICacheEntry? entry)
            ? (entry?.ExtendedProperties)
            : null;
        return Task.FromResult(ret);
    }

    public Task<bool> SetExtendedPropertiesAsync<T>(Region region, IDictionary<string, string?> extendedProperties, CancellationToken token = default)
    {
        var cacheEntryOptions = BuildEntryOptions(region, token);
        _logger.LogTrace("Set ExtendedProperties for Region {}", cacheEntryOptions.Region);
        try
        {
            if (MemoryCache.TryGetValue(cacheEntryOptions.Region, out ICacheEntry? entry))
            {
                var newEntry = entry!.NewEntry(entry.Expiration, extendedProperties);
                LocalSet(cacheEntryOptions, newEntry, typeof(T));
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        }
        catch (Exception ex)
        {
            MemoryCache.Remove(cacheEntryOptions.Region);
            _logger.LogWarning(ex, "Cache refresh value for Region {}", cacheEntryOptions.Region);
            return Task.FromResult(false);
        }
    }

    private string GetInnerCacheKey(Region region) =>
        CacheUtils.GetKey(region.ToString(), InstanceName);

    private record struct CacheEntryOptions(Region Region, string[]? Keys, CancellationToken Token, DateTimeOffset Expiration = default, IDictionary<string, string?>? ExtendedProperties = default, RegionCacheSetOption SetOption = RegionCacheSetOption.KeyReplace);
}
