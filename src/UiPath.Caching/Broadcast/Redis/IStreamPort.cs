namespace UiPath.Caching.Broadcast.Redis;

/// <summary>The operations <see cref="RedisStreamSubjectWriter{T}"/> needs of its stream, for its topic, group and consumer.</summary>
internal interface IStreamPort
{
    Task<StreamEntry[]> ReadGroupAsync(RedisValue position, bool noAck);

    /// <summary>Checks the stream and the group, and reads, in one script; <paramref name="full"/> includes the group's position.</summary>
    Task<RedisResult> CheckAndReadAsync(bool full);

    /// <summary>Checks the stream, and with <paramref name="full"/> the group, with the read sent behind them to the same node on the same connection, so no round trip is added.</summary>
    CheckedRead CheckThenRead(bool full, bool noAck);

    Task<StreamEntry[]> RangeAsync(RedisValue minId, RedisValue maxId, int count);

    Task<StreamHead?> HeadAsync();

    Task<bool> CreateGroupAsync(RedisValue position, StreamHead? head);

    Task<long> AcknowledgeAsync(RedisValue[] ids);
}
