using System.Collections.Concurrent;

namespace UiPath.Caching.KeyMove;

/// <summary>The key space of the registered caches: scan patterns, the per-cache ownership predicate and the round-trip guard of the key map.</summary>
/// <remarks>Ownership is judged across every registration, whatever the group, so a key two caches accept is reported as such and never resolved; keys of the move itself are never owned.</remarks>
/// <param name="libraryPrefix">The application's namespace in native keys, and the prefix of every key of the move.</param>
/// <param name="excluded">Glob patterns of the keyspaces of caches the app uses directly; a key that matches one is nobody's, whatever a delegate says.</param>
/// <param name="alert">Told once for each cache whose mapping lands outside its own namespace, until the mapping is valid again.</param>
internal sealed class RegisteredKeySpace(IReadOnlyList<KeyMoveCache> caches, string libraryPrefix, Action<KeyMoveAlert>? alert = null, IReadOnlyList<string>? excluded = null) : IGroupKeySpace
{
    private readonly ConcurrentDictionary<string, byte> _alerted = new(StringComparer.Ordinal);

    public IReadOnlyList<string> Patterns(string group, MoveSide side) =>
        [.. caches.Where(c => c.Group == group).Select(c => c.PatternOf(side)).Distinct(StringComparer.Ordinal)];

    public IReadOnlyList<string> OwnersOf(string group, MoveSide side, string key) =>
        Accepting(side, key).Select(c => c.Name).Order(StringComparer.Ordinal).ToArray();

    public bool IsMovable(string group, MoveSide side, string cacheId, string key, TimeSpan? timeToLive) =>
        Accepting(side, key).ToArray() is [{ } only] && only.Name == cacheId && only.RoundTrips(side, key) && DestinationIsOwned(group, only, side, key);

    public bool HasValidDestination(string group, MoveSide side, string cacheId, string key) =>
        Accepting(side, key).ToArray() is not [{ } only] || only.Name != cacheId || !CanBeParsed(only, side, key) || DestinationIsOwned(group, only, side, key);

    public TimeSpan? MaxSourceTtl(string group, string cacheId) => caches.FirstOrDefault(c => c.Group == group && c.Name == cacheId)?.MaxSourceTtl;

    private static bool CanBeParsed(KeyMoveCache cache, MoveSide side, string key)
    {
        try
        {
            return (side == MoveSide.Legacy ? cache.ParseInPlace(key) : cache.ParseNative(key)) is not null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    // The key the move would write, checked as it is made: a function cannot be proved for every key at build time. It must be a key of the same cache, in its own namespace, that reads back as the same caller key.
    private bool DestinationIsOwned(string group, KeyMoveCache cache, MoveSide side, string key)
    {
        var owned = false;
        try
        {
            var caller = side == MoveSide.Legacy ? cache.ParseInPlace(key)! : cache.ParseNative(key)!;
            var destination = side == MoveSide.Legacy ? cache.FormatNative(caller) : cache.FormatInPlace(caller);
            var other = side == MoveSide.Legacy ? MoveSide.Native : MoveSide.Legacy;
            owned = destination is not null
                && !ReservedKeys.IsReserved(libraryPrefix, destination)
                && Accepting(other, destination).ToArray() is [{ } landing]
                && landing.Name == cache.Name
                && (other == MoveSide.Native ? cache.ParseNative(destination) : cache.ParseInPlace(destination)) == caller;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            owned = false;
        }

        if (owned)
        {
            _alerted.TryRemove(cache.Name, out _);
        }
        else if (_alerted.TryAdd(cache.Name, 0))
        {
            alert?.Invoke(new KeyMoveAlert(group, "unmovable_destination", $"{cache.Name}: {key} maps outside its own namespace, onto a reserved key, or onto a key that does not read back"));
        }

        return owned;
    }

    private bool IsExcluded(string key) => excluded is { Count: > 0 } && excluded.Any(pattern => GlobPatterns.Matches(pattern, key));

    private IEnumerable<KeyMoveCache> Accepting(MoveSide side, string key)
    {
        if (ReservedKeys.IsReserved(libraryPrefix, key) || IsExcluded(key) || (side == MoveSide.Native && !key.StartsWith(libraryPrefix, StringComparison.Ordinal)))
        {
            return [];
        }

        // An in-place key under the library prefix is the library's own, unless the cache says in so many words that it owns such keys.
        var underLibrary = side == MoveSide.Legacy && key.StartsWith(libraryPrefix, StringComparison.Ordinal);
        return caches.Where(c => (!underLibrary || c.OwnsInPlace is not null) && c.Owns(side, key));
    }
}
