namespace UiPath.Caching.PhaseB;

/// <summary>What the registered caches of a group say about their keys, in logical names (no connector prefix): where to scan, which keys the group owns, and which of them the mover could still move.</summary>
internal interface IGroupKeySpace
{
    /// <summary>Glob patterns that together cover every key of the group's caches on <paramref name="side"/>; each starts with the library prefix of the keys and none overlaps another.</summary>
    IReadOnlyList<string> Patterns(string group, MoveSide side);

    /// <summary>The cache of <paramref name="cacheIds"/> that owns <paramref name="key"/>, or null when the ownership predicate of none of them accepts it.</summary>
    string? OwnerOf(string group, IReadOnlyList<string> cacheIds, MoveSide side, string key);

    /// <summary>The key maps to the other side and its class is not one the mover skips, so a pass would still move it.</summary>
    bool IsMovable(string group, MoveSide side, string cacheId, string key, TimeSpan? timeToLive);

    /// <summary>The longest <c>MaxSourceTtl</c> of the cache, for the canary; null when unknown.</summary>
    TimeSpan? MaxSourceTtl(string group, string cacheId);
}
