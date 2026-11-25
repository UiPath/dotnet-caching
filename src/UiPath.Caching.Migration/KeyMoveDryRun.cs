namespace UiPath.Caching.Migration;

/// <summary>The report of a dry run, which the start of a move needs.</summary>
/// <param name="Scanned">The keys the dry run scanned.</param>
/// <param name="EstimatedNativeBytes">The memory the native keys of the group will take once moved.</param>
/// <param name="EstimatedDuration">How long the mover is expected to take at its configured rate.</param>
/// <param name="Caches">Per cache, what the dry run found.</param>
public sealed record KeyMoveDryRun(long Scanned, long EstimatedNativeBytes, TimeSpan EstimatedDuration, IReadOnlyDictionary<string, KeyMoveDryRunCache> Caches);
