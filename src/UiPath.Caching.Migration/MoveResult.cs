namespace UiPath.Caching.Migration;

/// <summary>What the mover did with one key.</summary>
/// <param name="Moved">The key was published to N and its L copy deleted or kept as a copy of N.</param>
/// <param name="Drained">N already held the key, so only the L copy was deleted; counted nowhere.</param>
/// <param name="SkipReason">The reason of a skipped key, counted as <c>skipped{reason}</c>; null when the key was moved or drained.</param>
internal readonly record struct MoveResult(bool Moved, bool Drained, string? SkipReason)
{
    /// <summary>The skip reason of a key the runner no longer owned: not counted, and the pass stops, since the next owner takes the key.</summary>
    public const string OwnershipLost = "ownership_lost";

    public static MoveResult Done => new(true, false, null);

    public static MoveResult Drain => new(false, true, null);

    public static MoveResult Skip(string reason) => new(false, false, reason);
}
