namespace UiPath.Platform.Caching.Broadcast.Redis;

internal sealed record RedisStreamContext(
    RedisKey Topic,
    RedisValue FieldName,
    RedisValue ConsumerName,
    RedisValue ConsumerGroup,
    Uri SourceUri,
    int PollBatchSize,
    TimeSpan PollInterval,
    bool ProfilerEnabled,
    bool EmitStreamReceivedEvent
);
