using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Tests.KeyMove;

// Keys are "<side prefix><cache>:<name>"; a key of a cache outside the managed set is not owned.
internal sealed class FakeKeySpace(string nativePrefix, string legacyPrefix) : IGroupKeySpace
{
    public Func<string, bool> Movable { get; set; } = key => key.Contains(":mv", StringComparison.Ordinal);

    public Func<string, bool>? InvalidDestination { get; set; }

    public Dictionary<string, TimeSpan> MaxTtl { get; } = [];

    // Runs on every ownership question, which a scan asks once per key it finds.
    public Action<string>? OnOwnerOf { get; set; }

    public string PrefixOf(MoveSide side) => side == MoveSide.Native ? nativePrefix : legacyPrefix;

    public IReadOnlyList<string> Patterns(string group, MoveSide side) => [PrefixOf(side) + "*"];

    public IReadOnlyList<string> PatternsOf(string group, string cacheId, MoveSide side) => [$"{PrefixOf(side)}{cacheId}:*"];

    public IReadOnlyList<string> OwnersOf(string group, MoveSide side, string key)
    {
        OnOwnerOf?.Invoke(key);
        var prefix = PrefixOf(side);
        if (!key.StartsWith(prefix, StringComparison.Ordinal))
        {
            return [];
        }

        var rest = key[prefix.Length..];
        var cut = rest.IndexOf(':', StringComparison.Ordinal);
        return cut < 0 ? [] : [rest[..cut]];
    }

    public bool HasValidDestination(string group, MoveSide side, string cacheId, string key) => InvalidDestination?.Invoke(key) != true;

    public bool IsMovable(string group, MoveSide side, string cacheId, string key, TimeSpan? timeToLive) => Movable(key);

    public TimeSpan? MaxSourceTtl(string group, string cacheId) => MaxTtl.TryGetValue(cacheId, out var ttl) ? ttl : null;

    public bool HasHashCache(string group) => false;

    public string? CallerKeyOf(string group, MoveSide side, string cacheId, string key) => null;

    public TimeSpan MoveMinTtl(string group, string cacheId) => IGroupKeySpace.DefaultMoveMinTtl;
}
