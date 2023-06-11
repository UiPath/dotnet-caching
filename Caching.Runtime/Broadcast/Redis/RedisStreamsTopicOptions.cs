namespace UiPath.Platform.Caching.Broadcast.Redis;

public class RedisStreamsTopicOptions
{
    public bool Enabled { get; set; } = true;

    public int? MaxLength { get; set; }

    public int PollBatchSize { get; set; } = 100;

    public string FieldName { get; set; } = "event";

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan ProcessingTimeout { get; set; } = TimeSpan.FromSeconds(60);
}
