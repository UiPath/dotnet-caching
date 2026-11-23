namespace UiPath.Caching;

/// <summary>An inner tier whose removals leave a tombstone a fill can compare with its generator's age.</summary>
internal interface IFencedFillTier
{
    bool IsFenced { get; }

    /// <summary>A tombstone written less than <paramref name="generatorElapsed"/> ago is left in place and the fill is <see cref="FillOutcome.Removed"/>.</summary>
    ValueTask<FillResult<T>> TryFillAfterAsync<T>(CacheKey cacheKey, T? value, DateTimeOffset expiration, TimeSpan generatorElapsed, CachePolicy? policy, CancellationToken token);
}
