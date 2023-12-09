namespace UiPath.Platform.Caching.Redis;

[ExcludeFromCodeCoverage]
public class RedisConnectionOptions
{
    public bool Enabled => !string.IsNullOrWhiteSpace(ConnectionString);

    public string ConnectionString { get; set; } = string.Empty;

    public int BackOffMilliseconds { get; set; } = 1000;

    public TimeSpan? HeartbeatInterval { get; set; }

    public bool ProfilerEnabled { get; set; }

    public string ProfilerFeatureFlagKey { get; set; } = "RedisProfiler.Enabled";

    public bool PlannedMaintenanceEnabled { get; set; } = true;

    public bool LogConnectionFailedEvents { get; set; } = true;

    public bool LogConnectionRestoredEvents { get; set; } = true;

    public bool EnableHangDetection { get; set; } = true;

    public int LastWriteIntervalThresholdMilliseconds { get; set; } = 15000;

    public int LastReadIntervalThresholdMilliseconds { get; set; } = 15000;
}
