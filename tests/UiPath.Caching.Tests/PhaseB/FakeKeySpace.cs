using UiPath.Caching.PhaseB;

namespace UiPath.Caching.Tests.PhaseB;

// Keys are "<side prefix><cache>:<name>"; a key of a cache outside the managed set is not owned.
internal sealed class FakeKeySpace(string nativePrefix, string legacyPrefix) : IGroupKeySpace
{
    public Func<string, bool> Movable { get; set; } = key => key.Contains(":mv", StringComparison.Ordinal);

    public Dictionary<string, TimeSpan> MaxTtl { get; } = [];

    // Runs on every ownership question, which a scan asks once per key it finds.
    public Action<string>? OnOwnerOf { get; set; }

    public string PrefixOf(MoveSide side) => side == MoveSide.Native ? nativePrefix : legacyPrefix;

    public IReadOnlyList<string> Patterns(string group, MoveSide side) => [PrefixOf(side) + "*"];

    public string? OwnerOf(string group, IReadOnlyList<string> cacheIds, MoveSide side, string key)
    {
        OnOwnerOf?.Invoke(key);
        var prefix = PrefixOf(side);
        if (!key.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var rest = key[prefix.Length..];
        var cut = rest.IndexOf(':', StringComparison.Ordinal);
        return cut < 0 || !cacheIds.Contains(rest[..cut]) ? null : rest[..cut];
    }

    public bool IsMovable(string group, MoveSide side, string cacheId, string key, TimeSpan? timeToLive) => Movable(key);

    public TimeSpan? MaxSourceTtl(string group, string cacheId) => MaxTtl.TryGetValue(cacheId, out var ttl) ? ttl : null;
}
