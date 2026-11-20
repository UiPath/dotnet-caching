#pragma warning disable S3060 // by design: a cache with the capability interface takes its path, any other (a mock) falls back to the CacheKey members
namespace UiPath.Caching;

public partial interface ICache<T>
{
    /// <summary>Typed facade over <see cref="ICache.TryFillAsync{T}(CacheKey, T, TimeSpan, CachePolicy, CancellationToken)"/>.</summary>
    ValueTask<FillResult<T>> TryFillAsync(CacheKey cacheKey, T? value, TimeSpan expiration, CancellationToken token = default);

    /// <inheritdoc cref="TryFillAsync(CacheKey, T, TimeSpan, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<FillResult<T>> TryFillAsync(ref CacheKeyHandler cacheKey, T? value, TimeSpan expiration, CancellationToken token = default) =>
        TryFillAsync(cacheKey.ToCacheKey(), value, expiration, token);

    /// <inheritdoc cref="TryFillAsync(CacheKey, T, TimeSpan, CancellationToken)"/>
    ValueTask<FillResult<T>> TryFillAsync(CacheKey cacheKey, T? value, DateTimeOffset expiration, CancellationToken token = default);

    /// <inheritdoc cref="TryFillAsync(CacheKey, T, TimeSpan, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<FillResult<T>> TryFillAsync(ref CacheKeyHandler cacheKey, T? value, DateTimeOffset expiration, CancellationToken token = default) =>
        TryFillAsync(cacheKey.ToCacheKey(), value, expiration, token);

    /// <summary>Typed facade over <see cref="ICache.SetIfChangedAsync{T}(CacheKey, T, IEqualityComparer{T}, TimeSpan, CachePolicy, CancellationToken)"/>.</summary>
    ValueTask<ChangeResult<T>> SetIfChangedAsync(CacheKey cacheKey, T? value, IEqualityComparer<T> comparer, TimeSpan expiration, CancellationToken token = default);

    /// <inheritdoc cref="SetIfChangedAsync(CacheKey, T, IEqualityComparer{T}, TimeSpan, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<ChangeResult<T>> SetIfChangedAsync(ref CacheKeyHandler cacheKey, T? value, IEqualityComparer<T> comparer, TimeSpan expiration, CancellationToken token = default) =>
        SetIfChangedAsync(cacheKey.ToCacheKey(), value, comparer, expiration, token);

    /// <inheritdoc cref="SetIfChangedAsync(CacheKey, T, IEqualityComparer{T}, TimeSpan, CancellationToken)"/>
    ValueTask<ChangeResult<T>> SetIfChangedAsync(CacheKey cacheKey, T? value, IEqualityComparer<T> comparer, DateTimeOffset expiration, CancellationToken token = default);

    /// <inheritdoc cref="SetIfChangedAsync(CacheKey, T, IEqualityComparer{T}, TimeSpan, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<ChangeResult<T>> SetIfChangedAsync(ref CacheKeyHandler cacheKey, T? value, IEqualityComparer<T> comparer, DateTimeOffset expiration, CancellationToken token = default) =>
        SetIfChangedAsync(cacheKey.ToCacheKey(), value, comparer, expiration, token);
}
