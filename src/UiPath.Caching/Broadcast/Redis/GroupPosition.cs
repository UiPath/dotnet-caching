namespace UiPath.Caching.Broadcast.Redis;

/// <summary>A group's last delivery and, when Redis tells it, how many entries it has read.</summary>
internal readonly record struct GroupPosition(RedisValue LastDelivered, long? EntriesRead);
