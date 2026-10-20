namespace UiPath.Caching.Broadcast.Redis;

/// <summary>The stream's head and the server's clock, read together just before a group is made at the head.</summary>
internal readonly record struct StreamHead(long ServerMs, long RepliedAt, bool Exists, RedisValue LastId, long? LastMs, long? EntriesAdded, ReadCount? Start);
