namespace UiPath.Caching.KeyMove;

/// <summary>What the registered caches of a group say about their keys, in logical names with no connector prefix.</summary>
internal interface IGroupKeySpace
{
    /// <summary>The minimum remaining life of a key the mover moves, when neither the cache nor its group says otherwise.</summary>
    static readonly TimeSpan DefaultMoveMinTtl = TimeSpan.FromMinutes(10);

    /// <summary>Glob patterns that together cover every key of the group's caches on <paramref name="side"/>, each starting with the library prefix of the keys.</summary>
    IReadOnlyList<string> Patterns(string group, MoveSide side);

    /// <summary>The glob patterns of one cache's keys on <paramref name="side"/>, each starting with the library prefix; empty for a cache that is not registered in the group.</summary>
    IReadOnlyList<string> PatternsOf(string group, string cacheId, MoveSide side);

    /// <summary>Every registered cache of the group, managed or not, whose ownership predicate accepts <paramref name="key"/>: none, one, or several, which is an error the caller refuses when one of them is managed.</summary>
    IReadOnlyList<string> OwnersOf(string group, MoveSide side, string key);

    /// <summary>False when the key's owner maps it to a destination outside the namespace or onto a reserved key, which is a defect of the registration, not a class a mover may skip.</summary>
    bool HasValidDestination(string group, MoveSide side, string cacheId, string key);

    /// <summary>The key maps to the other side and its class is not one the mover skips, so a pass would still move it; a live <paramref name="timeToLive"/> below the cache's minimum, going forward, is the short class the mover leaves.</summary>
    bool IsMovable(string group, MoveSide side, string cacheId, string key, TimeSpan? timeToLive);

    /// <summary>The group has a cache that stores hashes; the mover does not promote them yet, so the drain check fails for the group.</summary>
    bool HasHashCache(string group);

    /// <summary>K2 or K2N: the caller key that the cache reads from <paramref name="key"/> on <paramref name="side"/>; null when the cache does not read it.</summary>
    string? CallerKeyOf(string group, MoveSide side, string cacheId, string key);

    /// <summary>The longest <c>MaxSourceTtl</c> of the cache, for the canary; null when unknown.</summary>
    TimeSpan? MaxSourceTtl(string group, string cacheId);

    /// <summary>The remaining life below which the mover leaves a key of the cache to its next read: the cache's own value, else its group's, else <see cref="DefaultMoveMinTtl"/>; zero moves every key.</summary>
    TimeSpan MoveMinTtl(string group, string cacheId);
}
