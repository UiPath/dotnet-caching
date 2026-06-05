namespace UiPath.Platform.Caching;

// Source-compatibility default interface methods for pre-CachePolicy call sites. Each forwards
// to the policy-bearing overload with policy=null. Excluded from coverage — these are forwarders
// with no behavior of their own; the policy-bearing impls are what tests exercise.
public partial interface ISetCache
{
    [ExcludeFromCodeCoverage]
    ValueTask<bool> AddAsync<T>(CacheKey cacheKey, T item, CancellationToken token = default)
        => AddAsync<T>(cacheKey, item, (CachePolicy?)null, token);

    [ExcludeFromCodeCoverage]
    ValueTask<long> AddAsync<T>(CacheKey cacheKey, IEnumerable<T> items, CancellationToken token = default)
        => AddAsync<T>(cacheKey, items, (CachePolicy?)null, token);

    [ExcludeFromCodeCoverage]
    ValueTask<long> AddAsync<T>(CacheKey cacheKey, IEnumerable<T> items, TimeSpan? expiration, CancellationToken token = default)
        => AddAsync<T>(cacheKey, items, expiration, null, token);

    [ExcludeFromCodeCoverage]
    ValueTask<long> AddAsync<T>(CacheKey cacheKey, IEnumerable<T> items, DateTimeOffset? expiration, CancellationToken token = default)
        => AddAsync<T>(cacheKey, items, expiration, null, token);

    [ExcludeFromCodeCoverage]
    ValueTask<T?> PopAsync<T>(CacheKey cacheKey, CancellationToken token = default)
        => PopAsync<T>(cacheKey, (CachePolicy?)null, token);

    [ExcludeFromCodeCoverage]
    ValueTask<IReadOnlyCollection<T?>> PopAsync<T>(CacheKey cacheKey, long count, CancellationToken token = default)
        => PopAsync<T>(cacheKey, count, null, token);

    [ExcludeFromCodeCoverage]
    ValueTask<IReadOnlyCollection<T?>> MembersAsync<T>(CacheKey cacheKey, CancellationToken token = default)
        => MembersAsync<T>(cacheKey, (CachePolicy?)null, token);
}
