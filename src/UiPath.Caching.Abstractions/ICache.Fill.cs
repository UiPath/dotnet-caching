#pragma warning disable S3060 // by design: a cache with the capability interface takes its path, any other (a mock) falls back to the CacheKey members
namespace UiPath.Caching;

public partial interface ICache
{
    /// <summary>Writes the value only if the key is absent, announcing nothing, and answers with the value already there otherwise.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="expiration"/> is not positive, or is a deadline that has passed.</exception>
    ValueTask<FillResult<T>> TryFillAsync<T>(CacheKey cacheKey, T? value, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="TryFillAsync{T}(CacheKey, T, TimeSpan, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<FillResult<T>> TryFillAsync<T>(ref CacheKeyHandler cacheKey, T? value, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) =>
        TryFillAsync(cacheKey.ToCacheKey(), value, expiration, policy, token);

    /// <inheritdoc cref="TryFillAsync{T}(CacheKey, T, TimeSpan, CachePolicy, CancellationToken)"/>
    ValueTask<FillResult<T>> TryFillAsync<T>(CacheKey cacheKey, T? value, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="TryFillAsync{T}(CacheKey, T, TimeSpan, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<FillResult<T>> TryFillAsync<T>(ref CacheKeyHandler cacheKey, T? value, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) =>
        TryFillAsync(cacheKey.ToCacheKey(), value, expiration, policy, token);

    /// <summary>Writes the value unless the key already holds one the comparer calls equal; a replacement is announced as a write is.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="expiration"/> is not positive, or is a deadline that has passed.</exception>
    ValueTask<ChangeResult<T>> SetIfChangedAsync<T>(CacheKey cacheKey, T? value, IEqualityComparer<T> comparer, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="SetIfChangedAsync{T}(CacheKey, T, IEqualityComparer{T}, TimeSpan, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<ChangeResult<T>> SetIfChangedAsync<T>(ref CacheKeyHandler cacheKey, T? value, IEqualityComparer<T> comparer, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) =>
        SetIfChangedAsync(cacheKey.ToCacheKey(), value, comparer, expiration, policy, token);

    /// <inheritdoc cref="SetIfChangedAsync{T}(CacheKey, T, IEqualityComparer{T}, TimeSpan, CachePolicy, CancellationToken)"/>
    ValueTask<ChangeResult<T>> SetIfChangedAsync<T>(CacheKey cacheKey, T? value, IEqualityComparer<T> comparer, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="SetIfChangedAsync{T}(CacheKey, T, IEqualityComparer{T}, TimeSpan, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<ChangeResult<T>> SetIfChangedAsync<T>(ref CacheKeyHandler cacheKey, T? value, IEqualityComparer<T> comparer, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) =>
        SetIfChangedAsync(cacheKey.ToCacheKey(), value, comparer, expiration, policy, token);
}
