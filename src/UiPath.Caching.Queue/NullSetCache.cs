namespace UiPath.Caching;

[ExcludeFromCodeCoverage]
public sealed class NullSetCache : ISetCache
{
    public static readonly NullSetCache Instance = new();

    public string Name => "Null";

    public ValueTask<bool> AddAsync<T>(CacheKey cacheKey, T item, CachePolicy? policy = null, CancellationToken token = default) => ReturnFalseAsync<T>();

    public ValueTask<long> AddAsync<T>(CacheKey cacheKey, IEnumerable<T> items, CachePolicy? policy = null, CancellationToken token = default) => ReturnZeroAsync<T>();

    public ValueTask<long> AddAsync<T>(CacheKey cacheKey, IEnumerable<T> items, TimeSpan? expiration = null, CachePolicy? policy = null, CancellationToken token = default) => ReturnZeroAsync<T>();

    public ValueTask<long> AddAsync<T>(CacheKey cacheKey, IEnumerable<T> items, DateTimeOffset? expiration = null, CachePolicy? policy = null, CancellationToken token = default) => ReturnZeroAsync<T>();

    public ValueTask<T?> PopAsync<T>(CacheKey cacheKey, CachePolicy? policy = null, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return ValueTask.FromResult(default(T?));
    }

    public ValueTask<IReadOnlyCollection<T?>> PopAsync<T>(CacheKey cacheKey, long count, CachePolicy? policy = null, CancellationToken token = default) => EmptyAsync<T>();

    public ValueTask<IReadOnlyCollection<T?>> MembersAsync<T>(CacheKey cacheKey, CachePolicy? policy = null, CancellationToken token = default) => EmptyAsync<T>();

    public ValueTask<bool> ContainsItemAsync<T>(CacheKey cacheKey, T item, CancellationToken token = default) => ReturnFalseAsync<T>();

    public ValueTask<long> CountAsync<T>(CacheKey cacheKey, CancellationToken token = default) => ReturnZeroAsync<T>();

    public ValueTask<bool> RemoveItemAsync<T>(CacheKey cacheKey, T item, CancellationToken token = default) => ReturnFalseAsync<T>();

    public ValueTask<long> RemoveItemsAsync<T>(CacheKey cacheKey, IEnumerable<T> items, CancellationToken token = default) => ReturnZeroAsync<T>();

    public ValueTask<bool> RemoveAsync<T>(CacheKey cacheKey, CancellationToken token = default) => ReturnFalseAsync<T>();

    public ValueTask<bool> ContainsAsync<T>(CacheKey cacheKey, CancellationToken token = default) => ReturnFalseAsync<T>();

    public void Dispose()
    {
    }

    private static ValueTask<bool> ReturnFalseAsync<T>()
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return ValueTask.FromResult(false);
    }

    private static ValueTask<long> ReturnZeroAsync<T>()
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return ValueTask.FromResult(0L);
    }

    private static ValueTask<IReadOnlyCollection<T?>> EmptyAsync<T>()
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return ValueTask.FromResult<IReadOnlyCollection<T?>>([]);
    }
}
