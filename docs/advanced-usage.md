# Advanced usage

Using Multiple cache providers in the same time and multiple cache key strategies (with simple prefix or with application version prefix)

```csharp

public static class CacheFactoryExtensions
{
    private static const char Separator = ':';
    private static readonly string ApplicationVersion = "1.0";

    private static readonly ICacheKeyStrategy AppKey1Strategy = AppVersionPrefix("key1");
    private static readonly ICacheKeyStrategy Key2Strategy = Prefix("key2");

    public static IHashCache<MyDto> MyDtos(this ICacheFactory factory) =>
        new HashCache<MyDto>(factory.CreateMultilayerHashCache(), AppKey1Strategy);

    public static ICache<List<string>> SomeLists(this ICacheFactory factory) =>
        new Cache<List<string>>(factory.CreateRedisCache(), Key2Strategy);

    private static ICacheKeyStrategy AppVersionPrefix(string prefix) =>
        new PrefixCacheKeyStrategy(string.Join(Separator, prefix, ApplicationVersion), Separator);

    private static ICacheKeyStrategy Prefix(string prefix) =>
        new PrefixCacheKeyStrategy(string.Join(Separator, prefix), Separator);
}

public class MyService{
    private IHashCache<MyDto> _cache;

    public MyService(ICacheFactory cacheFactory){
        _cache = cacheFactory.MyDtos();
    }
}
```

Extending the library:

* cache provider. Implement [ICacheProvider](/Caching.Abstractions/ICacheProvider.cs) and register it in `ICacheFactory.AddProvider(ICacheProvider provider)`
* topic provider for event broadcasting. Implement [ITopicProvider](/Caching.Abstractions/Broadcast/ITopicProvider.cs) and register it in `ITopicFactory.AddProvider(ITopicProvider provider)`

## Stream notify doorbell

`RedisStreamsTopic` polls Redis at `PollInterval` (default 250 ms) for new entries. Setting `NotifyEnabled = true` adds a sibling Pub/Sub channel that the publisher fires after each `XADD`. The channel name is resolved via `NotifyChannelStrategy` if supplied; otherwise it defaults to the stream's Redis key joined with `NotifyChannelName` (default `"notify"`) using the same `CacheOptions.Separator` as the stream key, so the channel inherits whatever prefix or sharding scheme the stream itself uses (e.g. stream key `app:st:topicA` → channel `app:st:topicA:notify`). Set `NotifyShardedPubSub = true` to use sharded Pub/Sub (`SPUBLISH`/`SSUBSCRIBE`, requires Redis 7.0+) so the doorbell does not fan out across cluster nodes; otherwise regular `PUBLISH`/`SUBSCRIBE` is used.

When `NotifyShardedPubSub = true`, the sharded channel strategy ensures the channel and the stream key share a Redis Cluster hash tag so they hash to the same slot — `XADD` and `SPUBLISH` from the publisher both target the same shard. The strategy supports two stream-key shapes:

1. **Stream key with a valid hash tag** (e.g. `app:st:{topicA}`): the suffix append preserves the existing tag — `app:st:{topicA}:notify`.
2. **Stream key with no `{` or `}` characters at all** (e.g. `app:st:topicA`): the strategy wraps the whole stream key as the channel's hash tag — `{app:st:topicA}:notify`. CRC16 of the wrapped tag content equals CRC16 of the bare stream key, so they collide on the same slot.

Stream keys that contain `{`/`}` but do not form a valid non-empty hash tag (e.g. `app:st:{}topicA`, `app:st:{topicA`) are rejected at topic construction with `InvalidOperationException`, because no safe wrapping exists for them. Either supply a custom `IRedisStreamKeyStrategy` that produces well-formed keys, or override `NotifyChannelStrategy` to take full control of channel naming.

The consumer's fetch loop wakes immediately on notification and re-issues `XREADGROUP`. The poll continues to run as a safety net — Pub/Sub is best-effort and a missed notification only delays delivery by at most one `PollInterval`. With notify enabled you can usually raise `PollInterval` (e.g. 1–5 s) to reduce idle Redis load without sacrificing latency.

| Option | Default | Notes |
| --- | --- | --- |
| `NotifyEnabled` | `false` | Opt-in. |
| `NotifyChannelStrategy` | `null` | Optional `IRedisChannelStrategy` override. When `null`, channel = stream key joined with `NotifyChannelName` via `CacheOptions.Separator`. |
| `NotifyChannelName` | `"notify"` | Name appended to the stream's Redis key by the default strategy, joined with `CacheOptions.Separator`. Empty/whitespace falls back to `"notify"`. Ignored if `NotifyChannelStrategy` is set. |
| `NotifyShardedPubSub` | `false` | When `true`, the default strategy uses sharded Pub/Sub (`SPUBLISH`/`SSUBSCRIBE`, requires Redis 7.0+). Ignored if `NotifyChannelStrategy` is set. |
| `NotifySubscriberTimeout` | multiplexer timeout | Resubscribe interval if Subscribe fails. |
| `NotifySubscriberDueTime` | half of `NotifySubscriberTimeout` | First-attempt delay. |
