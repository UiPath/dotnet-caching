using StackExchange.Redis;

namespace UiPath.Caching.KeyMove;

/// <summary>A controlled hash write: the fields, how they land, and the version count the hash cache keeps.</summary>
/// <param name="ExpireAtMilliseconds">Unix milliseconds, or -1 for none (a merge then drops an earlier expiry).</param>
/// <param name="VersionField">The field that counts writes, or null to count none.</param>
internal sealed record ControlledHashWrite(
    IReadOnlyList<HashEntry> Entries,
    HashCacheSetOption Mode,
    long ExpireAtMilliseconds,
    string? DropField = null,
    string? VersionField = null,
    long VersionSeed = 0);
