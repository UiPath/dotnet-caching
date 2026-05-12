using System.Threading.Channels;

namespace UiPath.Platform.Caching.Broadcast.Redis;

public class RedisStreamsTopicOptions
{
    /// <summary>
    /// Enables or disables the Redis Streams topic provider for the whole app.
    /// Per-topic overrides of this property in appsettings or via the builder API are ignored;
    /// provider enablement is app-wide only.
    /// </summary>
    public bool Enabled { get; set; } = true;

    public long? MaxLength { get; set; } = 32_768;

    public long? Limit { get; set; } = 1_024;

    public int PollBatchSize { get; set; } = 4096;

    public string FieldName { get; set; } = "event";

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    public IRedisStreamKeyStrategy? RedisStreamKeyStrategy { get; set; }

    public int ConsumerCapacity { get; set; } = 2048;

    public BoundedChannelFullMode FullMode { get; set; } = BoundedChannelFullMode.Wait;

    public TimeSpan SlowObserverThreshold { get; set; } = TimeSpan.FromMilliseconds(250);

    public bool? ConnectionMonitorEnabled { get; set; }

    public bool TrackStatistics { get; set; }

    public bool MaintainerEnabled { get; set; } = true;

    public TimeSpan MaintainerCheckInterval { get; set; } = TimeSpan.FromMinutes(30);

    public TimeSpan MaintainerTrimInterval { get; set; } = TimeSpan.FromHours(1);

    public TimeSpan MaintainerQuarantineInterval { get; set; } = TimeSpan.FromHours(1);

    public string? MaintainerSearchPattern { get; set; }

    public bool ProfilerEnabled { get; set; }

    public bool EmitStreamReceivedEvent { get; set; }

    public bool NotifyEnabled { get; set; }

    public IRedisChannelStrategy? NotifyChannelStrategy { get; set; }

    public string NotifyChannelName { get; set; } = StreamSuffixChannelStrategy.DefaultName;

    public bool NotifyShardedPubSub { get; set; }

    public TimeSpan? NotifySubscriberTimeout { get; set; }

    public TimeSpan? NotifySubscriberDueTime { get; set; }

    internal RedisStreamsTopicOptions Clone() => new()
    {
        Enabled                      = Enabled,
        MaxLength                    = MaxLength,
        Limit                        = Limit,
        PollBatchSize                = PollBatchSize,
        FieldName                    = FieldName,
        PollInterval                 = PollInterval,
        RedisStreamKeyStrategy       = RedisStreamKeyStrategy,
        ConsumerCapacity             = ConsumerCapacity,
        FullMode                     = FullMode,
        SlowObserverThreshold        = SlowObserverThreshold,
        ConnectionMonitorEnabled     = ConnectionMonitorEnabled,
        TrackStatistics              = TrackStatistics,
        MaintainerEnabled            = MaintainerEnabled,
        MaintainerCheckInterval      = MaintainerCheckInterval,
        MaintainerTrimInterval       = MaintainerTrimInterval,
        MaintainerQuarantineInterval = MaintainerQuarantineInterval,
        MaintainerSearchPattern      = MaintainerSearchPattern,
        ProfilerEnabled              = ProfilerEnabled,
        EmitStreamReceivedEvent      = EmitStreamReceivedEvent,
        NotifyEnabled                = NotifyEnabled,
        NotifyChannelStrategy        = NotifyChannelStrategy,
        NotifyChannelName            = NotifyChannelName,
        NotifyShardedPubSub          = NotifyShardedPubSub,
        NotifySubscriberTimeout      = NotifySubscriberTimeout,
        NotifySubscriberDueTime      = NotifySubscriberDueTime,
    };
}
