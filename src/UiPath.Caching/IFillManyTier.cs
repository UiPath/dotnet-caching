namespace UiPath.Caching;

/// <summary>An inner tier that fills several keys in one call, the keys of a slot in one pipeline.</summary>
internal interface IFillManyTier
{
    /// <summary>Each key is filled as <see cref="ICache.TryFillAsync{T}(CacheKey, T, DateTimeOffset, CachePolicy?, CancellationToken)"/> does, independently of the others; the results are in the order of <paramref name="entries"/>.</summary>
    /// <param name="generatorElapsed">How long the generators have run, when the tier leaves tombstones for it to compare with; null for none.</param>
    ValueTask<FillResult<T>[]> TryFillManyAsync<T>(KeyValuePair<CacheKey, T?>[] entries, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token, TimeSpan? generatorElapsed = null);
}
