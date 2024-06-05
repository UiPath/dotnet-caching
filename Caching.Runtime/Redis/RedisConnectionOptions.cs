namespace UiPath.Platform.Caching.Redis;

[ExcludeFromCodeCoverage]
public class RedisConnectionOptions
{
    public bool Enabled => !string.IsNullOrWhiteSpace(ConnectionString);

    public string ConnectionString { get; set; } = string.Empty;

    public string? ConnectionStringExtraParams { get; set; }

    public int BackOffMilliseconds { get; set; } = 1000;

    public bool? HeartbeatConsistencyChecks { get; set; }

    public TimeSpan? HeartbeatInterval { get; set; }

    public bool ProfilerEnabled { get; set; }

    public string ProfilerFeatureFlagKey { get; set; } = "RedisProfiler.Enabled";

    public bool PlannedMaintenanceEnabled { get; set; } = true;

    public bool LogConnectionFailedEvents { get; set; } = true;

    public bool LogConnectionRestoredEvents { get; set; } = true;

    public bool EnableHangDetection { get; set; } = true;

    public int LastWriteIntervalThresholdMilliseconds { get; set; } = 15000;

    public int LastReadIntervalThresholdMilliseconds { get; set; } = 15000;

    public string? Version { get; set; } = new Version(6, 0).ToString();

    public TimeSpan? HangDetectionDueTime { get; set; }

    public TimeSpan? HangDetectionPeriod { get; set; }

    public bool? FailFastBacklogPolicy { get; set; }

    public bool? ThreadPoolSocketManager { get; set; }

    public ConfigurationOptions CreateConfigurationOptions()
    {
        string cnn = string.IsNullOrWhiteSpace(ConnectionStringExtraParams) ? ConnectionString : ComposedConnectionString;

        if (string.IsNullOrWhiteSpace(cnn))
        {
            return new ConfigurationOptions();
        }

        var config = ConfigurationOptions.Parse(cnn);
        config.AbortOnConnectFail = false; // if the connection fails, the multiplexer will silently retry in the background
        config.ChannelPrefix = default;
        if (System.Version.TryParse(Version, out var version))
        {
            config.DefaultVersion = version;
        }
        if (BackOffMilliseconds > 0)
        {
            config.ReconnectRetryPolicy = new ExponentialRetry(BackOffMilliseconds);
        }

        if(HeartbeatConsistencyChecks.HasValue)
        {
            config.HeartbeatConsistencyChecks = HeartbeatConsistencyChecks.Value;
        }

        if (HeartbeatInterval.HasValue)
        {
            config.HeartbeatInterval = HeartbeatInterval.Value;
        }

        if (FailFastBacklogPolicy.GetValueOrDefault())
        {
            config.BacklogPolicy = BacklogPolicy.FailFast;
        }

        if(ThreadPoolSocketManager.GetValueOrDefault())
        {
            config.SocketManager = SocketManager.ThreadPool;
        }

        return config;
    }

    public string ComposedConnectionString =>
        string.IsNullOrWhiteSpace(ConnectionString) ? string.Empty : string.Concat(ConnectionString, ",", ConnectionStringExtraParams);
}
