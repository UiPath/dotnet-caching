namespace UiPath.Platform.Caching.Redis;

[ExcludeFromCodeCoverage]
public class RedisConnectionOptions
{
    public bool Enabled { get; set; } = true;

    public string ConnectionString { get; set; } = default!;

    public int? BackOffMilliseconds { get; set; }

    public TimeSpan? HeartbeatInterval { get; set; }

    public bool ProfilerEnabled { get; set; }

    public bool PlannedMaintenanceEnabled { get; set; } = true;

    public bool LogConnectionFailedEvents { get; set; } = true;

    public bool LogConnectionRestoredEvents { get; set; } = true;

    public bool EnableHangDetection { get; set; } = false;

    public int LastWriteIntervalThresholdMilliseconds { get; set; } = 15000;

    public int LastReadIntervalThresholdMilliseconds { get; set; } = 15000;
}
