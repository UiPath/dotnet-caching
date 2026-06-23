using StackExchange.Redis.Profiling;

namespace UiPath.Caching.Redis;

[ExcludeFromCodeCoverage]
public class RedisConnectionOptions
{
    public string ConnectionString { get; set; } = string.Empty;

    public string? ConnectionStringExtraParams { get; set; }

    public int BackOffMilliseconds { get; set; } = 1000;

    public bool? HeartbeatConsistencyChecks { get; set; }

    public TimeSpan? HeartbeatInterval { get; set; }


    public string ProfilerFeatureFlagKey { get; set; } = "RedisProfiler.Enabled";

    public bool PlannedMaintenanceEnabled { get; set; } = true;

    public bool LogConnectionFailedEvents { get; set; } = true;

    public bool LogConnectionRestoredEvents { get; set; } = true;

    public bool EnableHangDetection { get; set; } = true;

    public int LastWriteIntervalThresholdMilliseconds { get; set; } = 15000;

    public int LastReadIntervalThresholdMilliseconds { get; set; } = 15000;

    public string? DefaultVersion { get; set; } = new Version(6, 0).ToString();

    public TimeSpan? HangDetectionDueTime { get; set; }

    public TimeSpan? HangDetectionPeriod { get; set; }

    public bool? FailFastBacklogPolicy { get; set; }

    public bool? ThreadPoolSocketManager { get; set; }

    public bool ProfilerEnabled { get; set; }

    public bool ProfilerHasDefaultSession { get; set; } = true;

    public TimeSpan ProfilerFlushInterval { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan? ProfilerSessionMaxLifespan { get; set; } = TimeSpan.FromMinutes(1);

    public int? ProfilerSessionMaxChecks { get; set; } = 100;

    public bool ProfilerTrackMetricEnabled { get; set; } = true;

    public IReadOnlyList<string> ProfilerCommandDenyList { get; set; } = [];

    public Func<ProfilingSession?>? ProfilingSessionFactory { get; set; }

    public ISystemClock? Clock { get; set; }

    public Func<ConfigurationOptions, IConnectionMultiplexer>? ConnectionFactory { get; set; }

    public string? ConnectionMultiplexerFactoryType { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the connection should fail immediately 
    /// if the initial connection attempt cannot be established.
    /// </summary>
    /// <remarks>
    /// When set to <c>true</c>, the connection attempt will throw an exception if the 
    /// initial connection cannot be established. This is useful in scenarios where 
    /// immediate feedback about connection issues is required.
    /// 
    /// When set to <c>false</c>, the connection multiplexer will silently retry in the 
    /// background, allowing the application to continue running while the connection 
    /// is re-established. This is useful for applications that can tolerate temporary 
    /// connection interruptions.
    /// </remarks>
    public bool AbortOnConnectFail { get; set; }
}
