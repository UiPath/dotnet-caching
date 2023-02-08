using System.Collections.Immutable;

namespace UiPath.Platform.Caching.Redis;

public sealed class NullRegionCache : IRedisRegionCache, IHybridRegionCache
{
    public static readonly NullRegionCache Instance = new();

    public string? InstanceName => default;

    public Task<bool> ContainsAsync(Region region, CancellationToken token = default) =>
        Task.FromResult(false);

    public Task<DateTimeOffset?> ExpireTimeAsync(Region region, CancellationToken token = default) =>
        Task.FromResult(default(DateTimeOffset?));

    public Task<IDictionary<string, T?>> GetAsync<T>(Region region, CancellationToken token = default) =>
        Task.FromResult((IDictionary<string, T?>)ImmutableDictionary<string, T?>.Empty);

    public Task<IDictionary<string, T?>> GetAsync<T>(Region region, string[] keys, CancellationToken token = default) =>
        Task.FromResult((IDictionary<string, T?>)ImmutableDictionary<string, T?>.Empty);

    public Task<ICacheEntry<IDictionary<string, T?>>> GetCacheEntryAsync<T>(Region region, CancellationToken token = default) =>
        Task.FromResult(NullCacheEntry<IDictionary<string, T?>>.Instance);

    public Task<IDictionary<string, string?>?> GetExtendedPropertiesAsync(Region region, CancellationToken token = default) =>
        Task.FromResult<IDictionary<string, string?>?>(default);

    public Task<T?> GetItemAsync<T>(Region region, string key, CancellationToken token = default) =>
        Task.FromResult(default(T?));

    public Task<IDictionary<string, T?>> GetOrAddAsync<T>(Region region, Func<Task<IDictionary<string, T?>>> generator, CancellationToken token = default) =>
        Task.FromResult((IDictionary<string, T?>)ImmutableDictionary<string, T?>.Empty);

    public Task<IDictionary<string, T?>> GetOrAddAsync<T>(Region region, Func<Task<IDictionary<string, T?>>> generator, TimeSpan? expiration = null, CancellationToken token = default) =>
        Task.FromResult((IDictionary<string, T?>)ImmutableDictionary<string, T?>.Empty);

    public Task<IDictionary<string, T?>> GetOrAddAsync<T>(Region region, Func<Task<IDictionary<string, T?>>> generator, DateTimeOffset? expiration = null, CancellationToken token = default) =>
        Task.FromResult((IDictionary<string, T?>)ImmutableDictionary<string, T?>.Empty);

    public Task<IDictionary<string, T?>> GetOrAddAsync<T>(Region region, Func<Task<IDictionary<string, T?>>> generator, DateTimeOffset? expiration = null, RegionCacheSetOption setOption = RegionCacheSetOption.KeyReplace, CancellationToken token = default) =>
        Task.FromResult((IDictionary<string, T?>)ImmutableDictionary<string, T?>.Empty);

    public Task<bool> RefreshAsync<T>(Region region, CancellationToken token = default) =>
        Task.FromResult(false);

    public Task<bool> RefreshAsync<T>(Region region, TimeSpan? expiration = null, CancellationToken token = default) =>
        Task.FromResult(false);

    public Task<bool> RefreshAsync<T>(Region region, DateTimeOffset? expiration = null, CancellationToken token = default) =>
        Task.FromResult(false);

    public Task<bool> RefreshAsync<T>(Region region, RegionCacheEntryOptions options, CancellationToken token = default) =>
        Task.FromResult(false);

    public Task<bool> RemoveAsync<T>(Region region, CancellationToken token = default) =>
        Task.FromResult(false);

    public Task<bool> SetAsync<T>(Region region, IDictionary<string, T?> values, CancellationToken token = default) =>
        Task.FromResult(false);

    public Task<bool> SetAsync<T>(Region region, IDictionary<string, T?> values, TimeSpan? expiration = null, CancellationToken token = default) =>
        Task.FromResult(false);

    public Task<bool> SetAsync<T>(Region region, IDictionary<string, T?> values, DateTimeOffset? expiration = null, CancellationToken token = default) =>
        Task.FromResult(false);

    public Task<bool> SetAsync<T>(Region region, IDictionary<string, T?> values, RegionCacheEntryOptions options, CancellationToken token = default) =>
        Task.FromResult(false);

    public Task<bool> SetExtendedPropertiesAsync<T>(Region region, IDictionary<string, string?> extendedProperties, CancellationToken token = default) =>
        Task.FromResult(false);

    public Task<TimeSpan?> TimeToLiveAsync(Region region, CancellationToken token = default) =>
        Task.FromResult(default(TimeSpan?));

    private sealed record NullCacheEntry<T> : ICacheEntry<T>
    {
        public static readonly ICacheEntry<T> Instance = new NullCacheEntry<T>();

        public T? Value => default;

        public DateTimeOffset Expiration => DateTimeOffset.MinValue;

        public IDictionary<string, string?>? ExtendedProperties => default;

        public ICacheEntry NewEntry(DateTimeOffset? expiration = null, IDictionary<string, string?>? extendedProperties = null) =>
            NullCacheEntry<T>.Instance;
    }
}
