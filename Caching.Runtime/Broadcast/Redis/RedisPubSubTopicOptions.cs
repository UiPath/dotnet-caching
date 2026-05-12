using System.Threading.Channels;

namespace UiPath.Platform.Caching.Broadcast.Redis;

public class RedisPubSubTopicOptions
{
    /// <summary>
    /// Enables or disables the Redis Pub/Sub topic provider for the whole app.
    /// Per-topic overrides of this property in appsettings or via the builder API are ignored;
    /// provider enablement is app-wide only.
    /// </summary>
    public bool Enabled { get; set; }

    public IRedisChannelStrategy? RedisChannelStrategy { get; set; }

    public int ConsumerCapacity { get; set; } = 2048;

    public BoundedChannelFullMode FullMode { get; set; } = BoundedChannelFullMode.Wait;

    public TimeSpan SlowObserverThreshold { get; set; } = TimeSpan.FromMilliseconds(250);

    public bool? ConnectionMonitorEnabled { get; set; }

    public TimeSpan? SubscriberTimeout { get; set; }

    public TimeSpan? SubscriberDueTime { get; set; }

    internal RedisPubSubTopicOptions Clone() => new()
    {
        Enabled                  = Enabled,
        RedisChannelStrategy     = RedisChannelStrategy,
        ConsumerCapacity         = ConsumerCapacity,
        FullMode                 = FullMode,
        SlowObserverThreshold    = SlowObserverThreshold,
        ConnectionMonitorEnabled = ConnectionMonitorEnabled,
        SubscriberTimeout        = SubscriberTimeout,
        SubscriberDueTime        = SubscriberDueTime,
    };
}
