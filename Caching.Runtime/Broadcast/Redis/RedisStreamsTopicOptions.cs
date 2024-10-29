using System.Threading.Channels;

namespace UiPath.Platform.Caching.Broadcast.Redis;

public class RedisStreamsTopicOptions
{
    public bool Enabled { get; set; } = true;

    public int? MaxLength { get; set; } = 32_768;

    public int PollBatchSize { get; set; } = 4096;

    public string FieldName { get; set; } = "event";

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    public IRedisStreamKeyStrategy? RedisStreamKeyStrategy { get; set; }

    public int ConsumerCapacity { get; set; } = 2048;

    public BoundedChannelFullMode FullMode { get; set; } = BoundedChannelFullMode.Wait;

    public bool? ConnectionMonitorEnabled { get; set; }

    public bool TrackStatistics { get; set; }

    public bool MaintainerEnabled { get; set; }

    public TimeSpan MaintainerCheckInterval { get; set; } = TimeSpan.FromMinutes(30);

    public TimeSpan MaintainerTrimInterval { get; set; } = TimeSpan.FromHours(1);

    public TimeSpan MaintainerQuarantineInterval { get; set; } = TimeSpan.FromHours(1);

    public string? MaintainerSearchPattern { get; set; }

    public bool? ProfilerEnabled { get; set; }
}
