namespace UiPath.Caching.Migration;

/// <summary>What a dry run found for one cache.</summary>
/// <param name="Counts">The keys by what the move would do with them: moved, or skipped and why.</param>
/// <param name="SizeBuckets">The keys by stored size, one count per bucket of <see cref="SizeBucketLimits"/> and one for the larger ones.</param>
public sealed record KeyMoveDryRunCache(IReadOnlyDictionary<string, long> Counts, IReadOnlyList<long> SizeBuckets)
{
    /// <summary>The upper limits, in bytes, of the size buckets.</summary>
    public static IReadOnlyList<long> SizeBucketLimits => CacheInventory.BucketLimits;
}
