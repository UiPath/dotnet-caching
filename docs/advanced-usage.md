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

## Per-topic broadcast options

By default the `Broadcast:RedisStreams` and `Broadcast:RedisPubSub` sections in `appsettings.json` apply to every topic the app produces. You can override individual fields on a per-topic basis from configuration or from code; everything you do not override inherits from the app-wide section.

Topics are matched by name (case-insensitive). A `TopicKey` for a generic type may contain `:` (the default key strategy uses `CacheOptions.KeySeparator` for type parameters), so per-topic entries live in an **array** under `Topics` with a `Name` field rather than as dictionary keys.

### Appsettings layout

```json
"Caching": {
  "Broadcast": {
    "RedisStreams": {
      "MaxLength": 32768,
      "PollInterval": "00:00:00.250",
      "Topics": [
        { "Name": "ilist:simplefolder", "MaxLength": 131072 },
        { "Name": "orders", "NotifyEnabled": true }
      ]
    },
    "RedisPubSub": {
      "Topics": [
        { "Name": "orders", "SlowObserverThreshold": "00:00:00.100" }
      ]
    }
  }
}
```

Environment-variable form follows IConfiguration's array indexing:

```
Caching__Broadcast__RedisStreams__Topics__0__Name=ilist:simplefolder
Caching__Broadcast__RedisStreams__Topics__0__MaxLength=131072
```

Matching rules:

- Entries with a missing or blank `Name` are skipped and logged at Debug level (look for messages like "Per-topic entry at '...' has a missing or blank 'Name'; skipping.").
- If two entries share the same `Name`, the last one (highest index) wins — matching the standard `IConfiguration` "last wins" precedence so layered sources (e.g. `appsettings.Production.json` appended onto `appsettings.json`) override earlier entries.
- `Name` matching is case-insensitive against the resolved `TopicKey.Name`.
- Only fields present on an entry are overridden; everything else inherits from the app-wide section (delta overlay).

### Code-side overrides

Use the builder API for things that cannot bind from configuration (strategy instances) or for code-discovered overrides:

```csharp
builder.Host.ConfigureCaching(x => x
    .AddRedisConnection()
    .AddBroadcast()
    .ConfigureRedisStreamsTopic("orders", opt =>
    {
        opt.MaxLength = 131_072;
        opt.RedisStreamKeyStrategy = new MyCustomStreamKeyStrategy();
    })
    .ConfigureRedisPubSubTopic("orders", opt =>
    {
        opt.RedisChannelStrategy = new MyCustomChannelStrategy();
    }));
```

`ConfigureRedisStreamsTopic` / `ConfigureRedisPubSubTopic` may be called more than once for the same topic name. Every action runs in registration order against the same resolved options instance, so for fields both actions touch, the last call wins — matching `services.Configure<T>` semantics. This lets a base library set defaults and a consuming app layer additional overrides on top without losing either.

Call `AddRedisStreams` / `AddRedisPubSub` (or `AddBroadcast`, which calls both) before the corresponding `ConfigureRedis*Topic`. The registry is bound to the configuration section that `AddRedis*` was given, so the order matters when a custom section name is used. Calling `ConfigureRedis*Topic` first throws `InvalidOperationException`.

### Precedence

For each topic the resolved options are computed in this order:

1. App-wide defaults from `Broadcast:RedisStreams` / `Broadcast:RedisPubSub`.
2. The matching entry under `Topics` (delta overlay).
3. The optional `ConfigureRedis*Topic` action (code wins on overlapping fields).

The result is **snapshotted** when the topic is first created. Per-topic configuration changes after that point are not reapplied to existing topics.

### Caveat: some fields are app-wide only

The following fields are read at provider construction (or by singleton hosted services) and ignore per-topic overrides. You can set them in a `Topics` array entry or via `ConfigureRedis*Topic` but the values won't affect runtime behavior:

**`RedisStreamsTopicOptions` and `RedisPubSubTopicOptions`:**
- `Enabled` — provider enablement is app-wide.
- `ConnectionMonitorEnabled` — passed to `RedisTopicProviderBase` once at provider construction.

**`RedisStreamsTopicOptions` only:**
- `TrackStatistics` — read by `RedisStreamHealthMaintainer` (singleton hosted service).
- `MaintainerEnabled`, `MaintainerCheckInterval`, `MaintainerTrimInterval`, `MaintainerQuarantineInterval`, `MaintainerSearchPattern` — same; the maintainer is registered once with the app-wide options.

Every other field on the options classes (e.g. `MaxLength`, `PollInterval`, `ConsumerCapacity`, `FullMode`, `SlowObserverThreshold`, `NotifyEnabled`, `NotifyChannelStrategy`, `NotifyChannelName`, `NotifyShardedPubSub`, `NotifySubscriberTimeout`, `NotifySubscriberDueTime`, `RedisStreamKeyStrategy`, `RedisChannelStrategy`, `FieldName`, `Limit`, `PollBatchSize`, `ProfilerEnabled`, `EmitStreamReceivedEvent`, `SubscriberTimeout`, `SubscriberDueTime`) is honored per topic.

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
