using StackExchange.Redis;

namespace UiPath.Caching.Migration;

/// <summary>A key whose capture answered with a live supersession marker (CAP -4).</summary>
/// <param name="Marker">The exact marker value of the CAP reply, which the handler's unmark compares and never reads again.</param>
/// <param name="Identity">The identity the mover's admitted unit runs under: the unit's id.</param>
internal sealed record SupersededKey(string Group, string CacheId, string Key, RedisValue Marker, string Identity);
