using System.Threading.Channels;

namespace UiPath.Platform.Caching.Broadcast.Redis;

public class RedisStreamsTopicOptions
{
    public bool Enabled { get; set; } = true;

    public int? MaxLength { get; set; } = 32_768;

    public int PollBatchSize { get; set; } = 4096;

    public string FieldName { get; set; } = "event";

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    public TimeSpan ProcessingTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public IRedisStreamKeyStrategy? RedisStreamKeyStrategy { get; set; }

    public int ConsumerCapacity { get; set; } = 2048;

    public BoundedChannelFullMode FullMode { get; set; } = BoundedChannelFullMode.Wait;

    public bool? ConnectionMonitorEnabled { get; set; }
}
