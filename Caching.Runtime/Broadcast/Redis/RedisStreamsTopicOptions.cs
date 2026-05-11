using System.Threading.Channels;

namespace UiPath.Platform.Caching.Broadcast.Redis;

public class RedisStreamsTopicOptions
{
    public bool Enabled { get; set; } = true;

    public long? MaxLength { get; set; } = 32_768;

    public long? Limit { get; set; } = 1_024;

    public int PollBatchSize { get; set; } = 4096;

    public string FieldName { get; set; } = "event";

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    public IRedisStreamKeyStrategy? RedisStreamKeyStrategy { get; set; }

    public int ConsumerCapacity { get; set; } = 2048;

    public BoundedChannelFullMode FullMode { get; set; } = BoundedChannelFullMode.Wait;

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
}
