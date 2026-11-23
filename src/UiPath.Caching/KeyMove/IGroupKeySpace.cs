namespace UiPath.Caching.KeyMove;

/// <summary>What the registered caches of a group say about their keys, in logical names with no connector prefix.</summary>
internal interface IGroupKeySpace
{
    /// <summary>Glob patterns that together cover every key of the group's caches on <paramref name="side"/>, each starting with the library prefix of the keys.</summary>
    IReadOnlyList<string> Patterns(string group, MoveSide side);

    /// <summary>Every registered cache of the group, managed or not, whose ownership predicate accepts <paramref name="key"/>: none, one, or several, which is an error the caller refuses when one of them is managed.</summary>
    IReadOnlyList<string> OwnersOf(string group, MoveSide side, string key);

    /// <summary>False when the key's owner maps it to a destination outside the namespace or onto a reserved key, which is a defect of the registration, not a class a mover may skip.</summary>
    bool HasValidDestination(string group, MoveSide side, string cacheId, string key);

    /// <summary>The key maps to the other side and its class is not one the mover skips, so a pass would still move it.</summary>
    bool IsMovable(string group, MoveSide side, string cacheId, string key, TimeSpan? timeToLive);

    /// <summary>The longest <c>MaxSourceTtl</c> of the cache, for the canary; null when unknown.</summary>
    TimeSpan? MaxSourceTtl(string group, string cacheId);
}
