namespace UiPath.Caching.Broadcast.Redis;

/// <summary>What a check reads from a raw <c>XINFO STREAM</c> reply: the oldest entry is the newest id when the stream is empty.</summary>
internal readonly record struct StreamState(long Length, RedisValue Oldest, RedisValue LastGenerated, RedisValue MaxDeleted, long EntriesAdded);
