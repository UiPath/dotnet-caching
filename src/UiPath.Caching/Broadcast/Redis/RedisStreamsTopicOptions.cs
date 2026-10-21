using System.Threading.Channels;
using UiPath.Caching.Config;

namespace UiPath.Caching.Broadcast.Redis;

public class RedisStreamsTopicOptions : IBindsFromConfiguration
{
    internal static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(250);
    internal static readonly TimeSpan DefaultNotifiedPollInterval = TimeSpan.FromSeconds(5);

    private TimeSpan? _pollInterval;

    /// <summary>
    /// Enables or disables the Redis Streams topic provider for the whole app.
    /// Per-topic overrides of this property in appsettings or via the builder API are ignored;
    /// provider enablement is app-wide only.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Entries kept on the stream (<c>XADD MAXLEN ~</c>); <c>null</c> never trims.</summary>
    public long? MaxLength { get; set; } = 32_768;

    public long? Limit { get; set; } = 1_024;

    public int PollBatchSize { get; set; } = 4096;

    public string FieldName { get; set; } = "event";

    /// <summary>Poll cadence, 250 ms unless set; setting it, even to 250 ms, also makes it the cadence while the doorbell is live unless <see cref="NotifiedPollInterval"/> is set.</summary>
    public TimeSpan PollInterval
    {
        get => _pollInterval ?? DefaultPollInterval;
        set => _pollInterval = value;
    }

    /// <summary>Poll cadence while the doorbell subscription is live; <c>null</c> is 5 s, or <see cref="PollInterval"/> when that was set. Must be at least <see cref="PollInterval"/>.</summary>
    public TimeSpan? NotifiedPollInterval { get; set; }

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

    /// <summary>Publishes a Pub/Sub doorbell after every <c>XADD</c> so consumers read at once instead of at the next <see cref="PollInterval"/>.</summary>
    public bool NotifyEnabled { get; set; } = true;

    public IRedisChannelStrategy? NotifyChannelStrategy { get; set; }

    public string NotifyChannelName { get; set; } = StreamSuffixChannelStrategy.DefaultName;

    public bool NotifyShardedPubSub { get; set; }

    public TimeSpan? NotifySubscriberTimeout { get; set; }

    public TimeSpan? NotifySubscriberDueTime { get; set; }

    internal bool PollIntervalSet => _pollInterval.HasValue;

    internal TimeSpan EffectiveNotifiedPollInterval => NotifiedPollInterval ?? (PollIntervalSet ? PollInterval : DefaultNotifiedPollInterval);

    // The reflection binder writes every value-type property back, so an absent PollInterval key would read as set.
    void IBindsFromConfiguration.Bind(IConfiguration section)
    {
        var before = _pollInterval;
        section.Bind(this);
        if (string.IsNullOrEmpty(section[nameof(PollInterval)]))
        {
            _pollInterval = before;
        }
    }

    // The backing field, not the property: a clone of options that never set PollInterval must not read as set.
    internal RedisStreamsTopicOptions Clone() => new()
    {
        _pollInterval                = _pollInterval,
        Enabled                      = Enabled,
        MaxLength                    = MaxLength,
        Limit                        = Limit,
        PollBatchSize                = PollBatchSize,
        FieldName                    = FieldName,
        NotifiedPollInterval         = NotifiedPollInterval,
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
