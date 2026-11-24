namespace UiPath.Caching.KeyMove;

/// <summary>One registered cache of a key move group: its traits and the key map of each side, in logical names with no connector prefix.</summary>
/// <param name="InPlacePattern">Glob that covers every in-place key of the cache.</param>
/// <param name="FormatInPlace">the caller key to the in-place key.</param>
/// <param name="ParseInPlace">the in-place key to the caller key, or null when the key is not one of the cache.</param>
/// <param name="NativePattern">Glob that covers every native key of the cache.</param>
/// <param name="FormatNative">Maps the caller key to the native key.</param>
/// <param name="ParseNative">Maps the native key to the caller key or returns null if the cache does not own it.</param>
/// <param name="OwnsInPlace">The ownership predicate for in-place keys; null accepts a key that <paramref name="ParseInPlace"/> reads.</param>
/// <param name="OwnsNative">The ownership predicate for native keys; null accepts a key that <paramref name="ParseNative"/> reads.</param>
/// <param name="Hash">The cache stores hashes. The mover has no hash promotion yet, so the drain check never passes for a group with such a cache (<see cref="MoverPasses.NoHashPromotion"/>).</param>
/// <param name="MoveMinTtl">The cache's own minimum remaining life for the mover; null follows the group's, which defaults to <see cref="IGroupKeySpace.DefaultMoveMinTtl"/>.</param>
internal sealed record KeyMoveCache(
    string Name,
    string Group,
    bool NoOrigin,
    bool AtMostOnce,
    bool AcceptRefill,
    TimeSpan? MaxSourceTtl,
    string InPlacePattern,
    Func<string, string> FormatInPlace,
    Func<string, string?> ParseInPlace,
    string NativePattern,
    Func<string, string> FormatNative,
    Func<string, string?> ParseNative,
    Func<string, bool>? OwnsInPlace = null,
    Func<string, bool>? OwnsNative = null,
    TimeSpan? MoveMinTtl = null,
    bool Hash = false)
{
    public string PatternOf(MoveSide side) => side == MoveSide.Native ? NativePattern : InPlacePattern;

    public bool Owns(MoveSide side, string key) => side == MoveSide.Native
        ? (OwnsNative ?? (k => ParseNative(k) is not null))(key)
        : (OwnsInPlace ?? (k => ParseInPlace(k) is not null))(key);

    /// <summary>The key maps to a caller key and back to itself, which is what a mover pass needs before it touches a key.</summary>
    public bool RoundTrips(MoveSide side, string key) => side == MoveSide.Native
        ? ParseNative(key) is { } n && FormatNative(n) == key
        : ParseInPlace(key) is { } l && FormatInPlace(l) == key;
}
