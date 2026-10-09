namespace UiPath.Caching.Broadcast.Redis;

/// <summary>
/// Entries removed past a group's last delivery: on Redis 7.0, by the newest entry deleted and the count removed; below
/// it, by the oldest entry left. An equal gap found later lost nothing more.
/// </summary>
internal readonly record struct StreamGap(RedisValue LastDelivered, RedisValue Evidence, long Removed);
