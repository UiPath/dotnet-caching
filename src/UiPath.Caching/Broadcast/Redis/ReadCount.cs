namespace UiPath.Caching.Broadcast.Redis;

/// <summary>How many entries a group had read when its last delivery was <see cref="LastDelivered"/>.</summary>
internal readonly record struct ReadCount(RedisValue LastDelivered, long EntriesRead);
