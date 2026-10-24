# Broadcast

Cross-node cache sync (L1 invalidation across hosts). Two transports:

- **Redis Streams** — durable, at-least-once, consumer groups, replay. Default.
- **Redis Pub/Sub** — fire-and-forget, no replay. Simpler but lossy.

Pick **Streams** unless you have a specific reason to drop durability.

The sections below cover the full lifecycle: enabling and disabling broadcast, choosing a transport, configuring options at the app-wide and per-topic levels, the notify doorbell for sub-poll-interval latency, and the stream maintainer that keeps Redis tidy in long-running deployments.

Each node in a multi-host deployment maintains its own in-process L1 cache. When node A writes or evicts a value, node B's L1 still holds the prior version — the two caches have silently diverged. The broadcast topic carries an invalidation event from the writing node to every other node, prompting them to drop their stale L1 entry before the next read. Without broadcast, every node's L1 drifts independently and `InMemoryRedis` effectively becomes two separate caches running in parallel with no coordination. This is also why broadcast can be safely turned off for single-node deployments — set `BroadcastEnabled: false` in configuration and the overhead disappears entirely.

## Enabling and disabling broadcast

Broadcast is wired up by calling `AddBroadcast()` on the caching builder. The no-argument overload reads a `BroadcastEnabled` boolean from the root of the caching configuration section; if that key is absent it defaults to `true`.

```jsonc
// appsettings.json
"Caching": {
  "BroadcastEnabled": false   // disable broadcast for this service entirely
}
```

**Disable from config, not from code.** `AddInMemoryRedis()` calls `AddBroadcast()` internally, which reads `BroadcastEnabled` from configuration (defaulting to `true`). A code-only `AddBroadcast(enabled: false)` followed by `AddInMemoryRedis()` is *not* sticky — the subsequent internal call re-enables broadcast. Use the appsettings flag shown above, or call `AddRedisStreams()` / `AddRedisPubSub()` directly instead of relying on `AddBroadcast`. (`AddMemory()` does not call `AddBroadcast()`, so the code-only path works for memory-only setups — but those need an explicit `BroadcastEnable: true` on `InMemoryCacheOptions` and an explicit `AddBroadcast()` to cross-node-sync at all.)

When broadcast is disabled via `BroadcastEnabled: false`, neither `RedisStreamsTopicProvider` nor `RedisPubSubTopicProvider` is registered. All `ITopic` resolutions fall through to the null implementation — writes go to Redis and L1 but no invalidation messages are sent or received. This is safe for single-node deployments and for development environments where a second Redis connection for streams is undesirable.

The same key also exists as a runtime property, `CacheOptions.BroadcastEnabled`, settable from the `AddCaching` options lambda. Setting it to `false` makes `TopicFactory` resolve every topic to `NullTopicProvider`, so broadcast goes quiet even when the providers were already registered. Prefer the configuration key when you can: it skips the registration altogether rather than neutering it afterwards.

### Disabling broadcast for `InMemoryRedis` only

`InMemoryRedisCacheOptions.BroadcastEnable` turns broadcast off for that one provider while leaving it on for everything else:

```jsonc
"Caching": {
  "InMemoryRedis": {
    "BroadcastEnable": false   // L1+L2 tiering, no cross-node invalidation
  }
}
```

Use it when a single provider should skip broadcast: a single-node deployment where cross-node invalidation buys nothing, or a Redis-compatible backend whose Streams support does not cover `XREADGROUP` (Garnet, for example) and you would rather drop invalidation than switch transport.

Two things to know:

- It defaults to `true`, the opposite of `InMemoryCacheOptions.BroadcastEnable`, which is opt-in. `InMemoryRedis` has always broadcast by default and continues to.
- It can only narrow the app-wide setting. The effective behavior is `CacheOptions.BroadcastEnabled` AND this flag, so when broadcast is off app-wide, setting this to `true` changes nothing.

## Choosing a transport

`AddBroadcast()` calls both `AddRedisStreams()` and `AddRedisPubSub()` — which provider actually handles a topic depends on whether its `Enabled` flag is `true`. By default Redis Streams is enabled and Pub/Sub is disabled (`RedisPubSubTopicOptions.Enabled` defaults to `false`).

| Transport | Durability | Replay | Consumer groups | Recommended |
| --- | --- | --- | --- | --- |
| Redis Streams | At-least-once | Yes (XREADGROUP) | Yes | Default |
| Redis Pub/Sub | None (fire-and-forget) | No | No | Single-node or read-mostly |

Switch to Pub/Sub by setting `Broadcast:RedisPubSub:Enabled: true`, `Broadcast:RedisStreams:Enabled: false`, **and `DefaultTopic: "RedisPubSub"`** at the root of the `Caching` section. The last step matters: `TopicFactory.Get(name)` resolves topics by their configured provider name, not by "the first enabled provider". Leaving `DefaultTopic` at its `RedisStreams` default while disabling Streams resolves to the null topic provider, so L1 invalidations are silently dropped.

You can also call `AddRedisStreams()` / `AddRedisPubSub()` directly instead of `AddBroadcast()` to register only the transport you need:

```csharp
builder.Host.ConfigureCaching(x => x
    .AddRedisConnection()
    .AddRedisStreams()   // Pub/Sub omitted entirely
    .AddInMemoryRedis());
```

**When to prefer Pub/Sub:** the main use case is services that process events from an external Pub/Sub channel already and want to reuse the same Redis connection without the overhead of stream consumer groups. Pub/Sub can also be a reasonable default for services that restart frequently (e.g. short-lived batch jobs) where creating and cleaning up consumer groups would add noise. Outside these cases, Streams is almost always the better choice — at-least-once delivery and consumer group replay mean that a pod restart does not miss invalidations that arrived while it was down.

## App-wide options

See [reference/settings.md](../reference/settings.md) for the full set. The tables below summarise every field with its shipped default and per-topic override eligibility.

### `RedisStreamsTopicOptions`

| Field | Default | Per-topic | Notes |
| --- | --- | --- | --- |
| `Enabled` | `true` | No | App-wide provider toggle. |
| `MaxLength` | `32768` | Yes | `XADD MAXLEN ~` cap (server-side trim); `null` never trims. |
| `Limit` | `1024` | Yes | Not used by the stream reader or the publisher. |
| `PollBatchSize` | `1024` | Yes | Entries one `XREADGROUP` asks for. |
| `FieldName` | `"event"` | Yes | Redis stream field name for the payload. |
| `PollInterval` | `250 ms` | Yes | `XREADGROUP` cadence while the doorbell is not live. |
| `NotifiedPollInterval` | `5 s`, or `PollInterval` when that is set | Yes | `XREADGROUP` cadence while the doorbell subscription is live; must be at least `PollInterval`. |
| `RedisStreamKeyStrategy` | `null` | Yes | Code-only `IRedisStreamKeyStrategy`. |
| `ConsumerCapacity` | `2048` | Yes | Bounded channel capacity. |
| `FullMode` | `Wait` | Yes | `BoundedChannelFullMode` for backpressure. |
| `SlowObserverThreshold` | `250 ms` | Yes | Log threshold for slow observers. |
| `ConnectionMonitorEnabled` | `null` | No | Enables connection monitor at provider construction. |
| `TrackStatistics` | `false` | No | Emit telemetry metrics for streams and consumer groups. |
| `MaintainerEnabled` | `true` | No | Register `RedisStreamHealthMaintainer`. |
| `MaintainerCheckInterval` | `30 min` | No | Maintainer timer period. |
| `MaintainerTrimInterval` | `1 h` | No | Age threshold for entry trimming. |
| `MaintainerQuarantineInterval` | `1 h` | No | Quarantine window before consumer group deletion. |
| `MaintainerSearchPattern` | `null` | No | SCAN pattern to discover streams. |
| `ProfilerEnabled` | `false` | Yes | Enable Redis command profiling. |
| `EmitStreamReceivedEvent` | `false` | Yes | Emit a telemetry event per received stream entry. |
| `NotifyEnabled` | `true` | Yes | Notify doorbell via Pub/Sub; `false` polls only. |
| `NotifyChannelStrategy` | `null` | Yes | Code-only `IRedisChannelStrategy` for the channel. |
| `NotifyChannelName` | `"notify"` | Yes | Channel suffix used by the default strategy. |
| `NotifyShardedPubSub` | `false` | Yes | Use sharded Pub/Sub (Redis 7.0+). |
| `NotifySubscriberTimeout` | multiplexer timeout | Yes | Resubscribe interval. |
| `NotifySubscriberDueTime` | half of timeout | Yes | First-subscribe delay. |

### `RedisPubSubTopicOptions`

| Field | Default | Per-topic | Notes |
| --- | --- | --- | --- |
| `Enabled` | `false` | No | App-wide provider toggle. Must be `true` to use Pub/Sub. |
| `RedisChannelStrategy` | `null` | Yes | Code-only `IRedisChannelStrategy`. |
| `ConsumerCapacity` | `2048` | Yes | Bounded channel capacity. |
| `FullMode` | `Wait` | Yes | `BoundedChannelFullMode` for backpressure. |
| `SlowObserverThreshold` | `250 ms` | Yes | Log threshold for slow observers. |
| `ConnectionMonitorEnabled` | `null` | No | Enables connection monitor at provider construction. |
| `SubscriberTimeout` | multiplexer timeout | Yes | Resubscribe interval. |
| `SubscriberDueTime` | half of timeout | Yes | First-subscribe delay. |

### `MaxLength` vs `PollBatchSize`

Both touch how many entries move through the consumer pipeline, but at different layers (`Limit` is not used):

- `MaxLength` (default 32768) is a server-side cap — `XADD MAXLEN ~` trims the stream to at most this many entries. Tune it to bound Redis memory usage on the stream key. Entries trimmed before a node read them are reported as a loss (see [Troubleshooting](#troubleshooting)), so size it for the topic's burst traffic; a node's own entries are read at the poll, or every `MaxLength / 2` of its own publishes, whichever comes first.
- `Limit` (default 1024) is not used by the stream reader or the publisher.
- `PollBatchSize` (default 1024) is the `COUNT` of each `XREADGROUP`. A full batch is read again at once, so a smaller batch costs a round trip per batch while catching up, not throughput. Its reply travels on the connection the cache shares, and the commands behind it wait for it, so keep a full batch's reply well within the connection's timeout: at a few hundred bytes an entry, 1024 entries are a few hundred KiB. Lower it with a short `syncTimeout`/`asyncTimeout` on a small server.

For most services the defaults are fine. Tune `MaxLength` first if you see Redis memory pressure on stream keys, and `PollBatchSize` if `XREADGROUP` calls time out while a node catches up.

## Per-topic overrides

App-wide options under `Broadcast:RedisStreams` and `Broadcast:RedisPubSub` apply to every topic the app produces. To override individual fields for a specific topic, add entries under `Topics[]`:

```jsonc
"Caching": {
  "Broadcast": {
    "RedisStreams": {
      "MaxLength": 32768,
      "Topics": [
        { "Name": "ilist:simplefolder", "MaxLength": 131072, "PollInterval": "0:00:00.050" },
        { "Name": "orders",             "NotifyEnabled": true,  "ConsumerCapacity": 8192     }
      ]
    },
    "RedisPubSub": {
      "Topics": [
        { "Name": "orders", "SlowObserverThreshold": "0:00:00.100" }
      ]
    }
  }
}
```

Environment-variable form follows `IConfiguration`'s array indexing (useful in Kubernetes `env:` blocks or Azure App Service application settings):

```
Caching__Broadcast__RedisStreams__Topics__0__Name=ilist:simplefolder
Caching__Broadcast__RedisStreams__Topics__0__MaxLength=131072
Caching__Broadcast__RedisStreams__Topics__1__Name=orders
Caching__Broadcast__RedisStreams__Topics__1__NotifyEnabled=true
```

**Matching rules:** `Name` is matched case-insensitively against the resolved `TopicKey.Name`. Duplicates: last entry wins — this matches `IConfiguration` semantics, so a layered source like `appsettings.Production.json` overriding `appsettings.json` works without surprises. Entries with a missing or blank `Name` are skipped; a Debug-level message is logged with the offending section path.

**Why the array form (not a dictionary):** topic names commonly contain `:` (e.g. `ilist:simplefolder`). Using `Topics` as a dictionary key would conflict with `IConfiguration`'s `:` path separator, breaking any key strategy that uses colons.

## Code-side overrides

For overrides that cannot bind from JSON — strategy instances, or values computed at startup — use the builder API:

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

**Ordering:** call `AddRedisStreams` (or `AddBroadcast`, which calls both) before any `ConfigureRedisStreamsTopic`. The registry binds to the configuration section that `AddRedis*` was given, so the order matters when a custom section name is used. Calling `ConfigureRedis*Topic` before the corresponding `AddRedis*` throws `InvalidOperationException`.

Multiple `ConfigureRedis*Topic` calls for the same topic name run in registration order against the same resolved options instance. For fields touched by more than one action, the last call wins — matching `services.Configure<T>` semantics. This lets a base library set defaults and a consuming app layer additional overrides on top without losing either.

**Precedence:** for each topic the resolved options are computed in this order:

1. App-wide defaults from `Broadcast:RedisStreams` / `Broadcast:RedisPubSub`.
2. The last matching entry under `Topics[]` (bound via `IConfigurationSection.Bind`).
3. Every `ConfigureRedis*Topic` action in registration order (code wins on overlapping fields).

The result is snapshotted when the topic is first created. Per-topic configuration changes made after that point are not reapplied to existing topics.

## App-wide-only fields

Some options are read at provider construction or by singleton hosted services. Setting them in a `Topics[]` entry or in a `ConfigureRedis*Topic` action is accepted by the API but has no effect on the already-constructed provider or service.

**`RedisStreamsTopicOptions` and `RedisPubSubTopicOptions`:**

- `Enabled` — controls whether the provider is registered at all; read once during `AddRedisStreams` / `AddRedisPubSub`.
- `ConnectionMonitorEnabled` — wires the connection monitor at provider construction.

**`RedisStreamsTopicOptions` only:**

- `TrackStatistics` — enables telemetry tracking across all streams; read at maintainer construction.
- `MaintainerEnabled` — gates `RedisStreamHealthMaintainer` registration; read during `AddRedisStreams`.
- `MaintainerCheckInterval` — maintainer timer period; read at `StartAsync`.
- `MaintainerTrimInterval` — age threshold for stream entry trimming; read per maintainer cycle.
- `MaintainerQuarantineInterval` — quarantine window for empty consumer groups; read per maintainer cycle.
- `MaintainerSearchPattern` — SCAN pattern used to discover streams; resolved once at `Initialize`.

Everything else (`MaxLength`, `PollBatchSize`, `PollInterval`, `NotifiedPollInterval`, `ConsumerCapacity`, `FullMode`, `SlowObserverThreshold`, every `Notify*` field, etc.) is honored per-topic.

## Notify doorbell

`RedisStreamsTopic` polls at `PollInterval` (default 250 ms) until its doorbell subscription is live, and at `NotifiedPollInterval` while it is: 5 s by default, or `PollInterval` when `PollInterval` is set (a `PollInterval` key in configuration counts as set, even at 250 ms). `NotifyEnabled` (default `true`) adds a Pub/Sub channel that the publisher fires immediately after each `XADD`. The other nodes wake up and call `XREADGROUP` right away instead of waiting for the next poll tick, so an invalidation reaches them in a few network round trips rather than up to one poll interval later. The periodic poll continues in parallel as a safety net — a notification that is lost while the subscription still looks live (a dropped `PUBLISH`) adds at most one poll interval of latency. Set `NotifyEnabled: false` to poll only, for example where the Pub/Sub subscription count matters (see below). A read that fails, as every read does while Redis is down, is retried three times at `PollInterval` and then with a doubling back-off capped at 30 s; the connection coming back ends the back-off at once, whether or not the connection monitor is on.

What a broadcast costs on the server: the `XADD`, one fire-and-forget `PUBLISH`, and on every *other* node one script (`XINFO STREAM` and `XREADGROUP`, one round trip) and one `XACK`. The doorbell carries the publisher's consumer name, so the publishing node does not wake for its own ring — its own entries are never dispatched; the poll, the next ring from another node, or every `MaxLength / 2` of its own publishes reads and acknowledges them. Every read is checked for a trim past the group's position: a woken node compares the stream's oldest entry and counts (`XINFO STREAM`, O(1)) with the position it knows, while the check that also asks the server for the group's position (`XINFO GROUPS`, which grows with the number of nodes) keeps the poll's cadence — once a poll interval, after a reconnect, and after a full batch. A doorbell that arrives while a read is in flight is collapsed with any others into one more read.

If the server refuses the subscription — the connection user lacks `SUBSCRIBE` or the channel (`NOPERM`), or the command is disabled — the topic logs one warning and polls only; a connection error is retried at `NotifySubscriberTimeout`, warned about once per streak.

The long interval applies only while the subscription is confirmed live. Before the first subscribe, after a forced reconnect until the topic subscribes again, while a subscription connection is down (a failed command connection does not count), when the server refuses the subscription, and with `NotifyEnabled: false`, the loop polls at `PollInterval`; a connection failure also ends a wait in progress at the long interval. Failure retries and the `XINFO GROUPS` position check stay on `PollInterval`. An idle topic costs about 15 server-executed commands a second per process at 250 ms and 0.8 at 5 s (measured on a single node: 3 topics, 44 against 2.4); 5 s is where the saving levels off; set it lower (down to `PollInterval`) to shorten the window a dropped doorbell leaves, or higher to cut the idle cost further.

Only `AddRedisStreams(sectionName)` and the `Topics[]` entries keep an absent `PollInterval` key unset. An app that binds the options itself — `AddRedisStreams(o => section.Bind(o))`, `services.Configure<RedisStreamsTopicOptions>(section)` or `ConfigureRedisStreamsTopic(name, o => section.Bind(o))` — counts as having set `PollInterval`, because the configuration binder writes it back, so the live cadence stays at `PollInterval`; set `NotifiedPollInterval` explicitly there to get the long one.

A reader takes the long interval because its own subscription is live, not because the writers ring. Entries from a writer that does not ring the doorbell — one with `NotifyEnabled: false`, another app sharing the stream with other settings, or a library version from before the doorbell — reach it up to `NotifiedPollInterval` late instead of `PollInterval`. Where some writers do not ring, set `NotifiedPollInterval` to `PollInterval`, or keep `PollInterval` set.

Do not raise `PollInterval` for this: it also paces the fallback, so a topic without a live doorbell would poll slowly too.

The notify feature uses one Pub/Sub subscription per topic per node. At modest topic counts (tens of topics, hundreds of nodes) the subscription count is negligible. At very high topic × node products — thousands of topics, thousands of nodes — consider whether the subscription fan-out pressure on the Redis Pub/Sub bus outweighs the polling savings. In those cases, `NotifyEnabled: false` with a `PollInterval` of 250–500 ms may produce fewer total round-trips.

On a Redis Cluster a plain `PUBLISH` is forwarded over the cluster bus to every node, whichever node the subscribers sit on. That is one small message per broadcast per node, and it is what the default does on a cluster too, because sharded Pub/Sub needs Redis 7.0 and a stream key whose hash tag the channel can share, neither of which the library can assume. Where both hold, `NotifyShardedPubSub: true` keeps the doorbell on the stream's shard (see below).

### Channel naming

By default the notify channel name is the stream's Redis key joined with `NotifyChannelName` (default `"notify"`) using `CacheOptions.Separator`. The separator and the name suffix are lowercased at construction time; the stream key itself is not modified by the channel strategy (its casing is controlled by the key strategy). For example, if the stream key is `app:st:topicA` and the separator is `:`, the channel is `app:st:topicA:notify`. Override the channel entirely by assigning a custom `IRedisChannelStrategy` to `NotifyChannelStrategy` (code-only; `IRedisChannelStrategy` instances are not JSON-bindable).

Changing `NotifyChannelName` to an empty or whitespace string silently falls back to `"notify"` — the strategy guards against blank names so the default always produces a valid channel.

### Sharded Pub/Sub (Redis 7+)

Set `NotifyShardedPubSub: true` to use `SPUBLISH`/`SSUBSCRIBE` so the doorbell message does not fan out across every cluster node. The default sharded strategy (`StreamSuffixShardedChannelStrategy`) ensures the channel and stream key hash to the same Redis Cluster slot:

- **Stream key with a valid hash tag** (e.g. `app:st:{topicA}`) — the channel inherits it verbatim: `app:st:{topicA}:notify`. A valid hash tag is a non-empty string between the first `{` and the next `}`.
- **Stream key with no `{` or `}` characters** (e.g. `app:st:topicA`) — the strategy wraps the entire key in braces: `{app:st:topicA}:notify`. CRC16 of the wrapped tag content equals CRC16 of the bare key, so channel and stream land on the same slot.

Stream keys that contain `{`/`}` but do not form a valid non-empty hash tag (e.g. `app:st:{}topicA` or `app:st:{topicA` with no closing brace) are rejected at topic construction with `InvalidOperationException`. No safe slot-preserving wrapping exists for these patterns. Either supply a custom `IRedisStreamKeyStrategy` that produces well-formed hash tags, or assign a custom `IRedisChannelStrategy` to `NotifyChannelStrategy` to take full control of channel naming.

### Notify options reference

| Option | Default | Notes |
| --- | --- | --- |
| `NotifyEnabled` | `true` | `false` polls only. |
| `NotifyChannelStrategy` | `null` | Code-only `IRedisChannelStrategy` override. When `null`, channel = stream key joined with `NotifyChannelName` via `CacheOptions.Separator`. |
| `NotifyChannelName` | `"notify"` | Suffix appended to the stream's Redis key by the default strategy. Empty/whitespace falls back to `"notify"`. Ignored when `NotifyChannelStrategy` is set. |
| `NotifyShardedPubSub` | `false` | When `true`, uses sharded Pub/Sub (`SPUBLISH`/`SSUBSCRIBE`; requires Redis 7.0+). Ignored when `NotifyChannelStrategy` is set. |
| `NotifySubscriberTimeout` | multiplexer timeout | Resubscribe interval if `Subscribe` fails (5 s until the connection is up). |
| `NotifySubscriberDueTime` | half of `NotifySubscriberTimeout` | First-attempt delay before the initial subscribe. |

## Stream maintainer

`RedisStreamHealthMaintainer` is a singleton hosted service that wakes on a `PeriodicTimer` every `MaintainerCheckInterval` (default 30 min). On each cycle it acquires a distributed lock — so only one instance in the cluster does the work — then:

1. **Trims** entries older than `MaintainerTrimInterval` (default 1 h) using `XTRIM MINID` on Redis 6.2+ or falls back to timestamp-based `XDEL` on older versions. Set `MaintainerTrimInterval` to a value greater than `InMemoryRedis.LocalMaxExpiration`; trimming entries before all consumer groups have read them can cause missed invalidations on slow consumers.
2. **Quarantines** consumer groups by recording them with a timestamp in a hash key, and permanently deletes them once they have been quarantined for longer than `MaintainerQuarantineInterval` (default 1 h). Two conditions quarantine a group: having no consumers at all, and having consumers whose last-delivered-id is older than `MaintainerTrimInterval`. Both serve the same interval, measured from the recorded timestamp rather than from the pass that observes it, so overlapping passes cannot shorten it. Groups that recover between cycles are removed from quarantine automatically.
3. **Deletes** streams with no consumer groups if the stream has not received a new entry within `MaintainerQuarantineInterval`.

### How streams are discovered

`SCAN` is server-scoped: its cursor walks the keyspace of the one server it is sent to, and it carries no key for the
client to route by. The maintainer therefore sends a scan to **every connected primary**, each on its own cursor, and
merges the results — on a Redis Cluster that is the only way to see a stream whose slot lives on another shard. The
scans run concurrently, so a pass costs the slowest shard rather than the sum of all of them, and a primary that
cannot be reached costs only its own streams for that cycle instead of aborting the pass. Everything after discovery
(`XINFO`, `XTRIM`, the quarantine hash, the deletes) is keyed, so the client already routes it to the owning shard.

Primaries come from `IRedisConnector.GetPrimaries()`. It is a defaulted interface member returning an empty sequence,
so for a custom `IRedisConnector` that does not override it the maintainer takes the connected primaries of
`Database.Multiplexer` instead. Each is scanned in the connector's database (`Database.Database`). Only when neither
source names a primary does it scan the routed server — correct on a single server, but on a cluster it reaches one
shard. The maintainer logs a warning whenever it takes that path, and an error naming the endpoint whenever a primary
cannot be scanned.

### Setting a custom search pattern

For services that use a non-default stream key strategy, set `MaintainerSearchPattern` to scope the SCAN to only the keys your app owns. Without it the maintainer derives a pattern from `RedisStreamKeyStrategy` (defaulting to `PrefixStrategy` with `RedisKeyspaces.Streams`), which covers the standard key layout but may pick up keys from other apps sharing the same Redis instance if the key prefix is not unique enough.

Example — a service using `PrefixStrategy` can derive the scan pattern at startup and stamp it onto the options before binding:

```csharp
void Configure(ICachingBuilder builder) => builder
    .AddRedisStreams(opt =>
    {
        var cacheOptions = new CacheOptions { AppShortName = appShortName };
        var keyFactory = new PrefixStrategy(RedisKeyspaces.Streams, cacheOptions);
        opt.MaintainerSearchPattern = keyFactory.GetRedisKey("*").ToString();
    });
```

An explicit pattern is used as written, so behind a connector whose database applies `WithKeyPrefix` it must start with
that prefix too: SCAN matches the keys as stored. The derived pattern adds the connector's prefix by itself.

### Operational considerations

The maintainer is enabled by default (`MaintainerEnabled: true`). Disable it (`MaintainerEnabled: false`) only when another process or operator is responsible for stream lifecycle management in your cluster — for example, a centralized platform team running a dedicated maintenance job. Without trimming, streams grow unbounded and eventually exhaust Redis memory allocations. Without quarantine cleanup, abandoned consumer groups from decommissioned pod replicas accumulate and slow down `XREADGROUP` fan-out for every active reader on that stream.

When running with multiple replicas, the distributed lock (`MaintainerCheckInterval` as the lock TTL) ensures only one replica performs maintenance per cycle. A replica that fails to acquire the lock exits the cycle immediately and tries again on the next tick.

## Complete example

A service that uses Redis Streams with the notify doorbell enabled on a high-traffic topic and custom per-topic settings:

```jsonc
// appsettings.json
"Caching": {
  "AppShortName": "orders-svc",
  "BroadcastEnabled": true,
  "Broadcast": {
    "RedisStreams": {
      "Enabled": true,
      "MaxLength": 32768,
      "PollInterval": "0:00:00.250",
      "NotifiedPollInterval": "0:00:05.000",
      "MaintainerEnabled": true,
      "MaintainerCheckInterval": "0:30:00",
      "MaintainerTrimInterval": "1:00:00",
      "MaintainerQuarantineInterval": "1:00:00",
      "Topics": [
        {
          "Name": "orders",
          "MaxLength": 131072,
          "NotifyEnabled": true,
          "ConsumerCapacity": 8192
        }
      ]
    }
  }
}
```

```csharp
// Program.cs
builder.Host.ConfigureCaching(x => x
    .AddRedisConnection()
    .AddBroadcast()
    .AddInMemoryRedis());
```

With this configuration:

- All topics poll every 5 seconds while their doorbell is live (`NotifiedPollInterval`, because notify wakes consumers sooner) and every 250 ms otherwise.
- The `orders` topic has a larger stream cap (131072 entries) and a larger consumer channel (8192).
- The `orders` topic uses the notify doorbell — after every `XADD` a Pub/Sub message wakes consumers immediately, so the 5-second poll is only a fallback.
- The stream maintainer runs every 30 minutes, trims entries older than 1 hour, and quarantines empty consumer groups for up to 1 hour before deleting them.

## Troubleshooting

**L1 entries are stale after a write from another node.**

Check that `BroadcastEnabled` is `true` and that `RedisStreams:Enabled` is `true` (the default). Confirm that all nodes share the same `AppShortName` — stream keys are prefixed with it, so nodes with different `AppShortName` values write to different streams and never receive each other's invalidations.

Also confirm that the consumer group for each node is active: run `XINFO GROUPS <stream-key>` and verify `consumers` > 0 and `lag` is not growing.

**Consumer channel is full (`FullMode: Wait` blocks writes; `FullMode: DropOldest` silently drops).**

Raise `ConsumerCapacity` for the topic or increase the number of consumer threads. A full channel usually indicates a slow observer — check `SlowObserverThreshold` warnings in the logs. The logged duration is the time the observer held the channel slot; values consistently above `PollInterval` mean the observer cannot keep up.

**Notify doorbell is not waking consumers.**

Verify `NotifyEnabled` is not set to `false` for the topic. Check that the Redis connection multiplexer can subscribe — subscribe failures are retried on `NotifySubscriberTimeout`; watch for subscribe-error log lines. For Redis Cluster, confirm that the stream key and notify channel fall on the same slot by inspecting `CLUSTER KEYSLOT <stream-key>` and `CLUSTER KEYSLOT <channel-name>`.

**Sharded Pub/Sub fails at topic construction with `InvalidOperationException`.**

The stream key contains `{` or `}` but not a valid non-empty hash tag. Either fix the key strategy to produce a well-formed `{tag}`, or set `NotifyChannelStrategy` to a custom implementation that resolves the channel independently of the key.

**Stream maintainer is not trimming old entries.**

Confirm `MaintainerEnabled: true` and that `StartAsync` was called (the service host calls it on startup). Check that `MaintainerTrimInterval` is set appropriately — the default is 1 hour; entries must be older than this to be trimmed. On Redis < 6.2, `XTRIM MINID` is not available and trimming falls back to a less efficient path; consider upgrading Redis.

**Some streams are trimmed and others are not, on the same cluster and the same options.**

Versions before the per-primary scan (2.0.0 and earlier) discovered streams with a single server-scoped `SCAN`, which
reached one primary only. On a Redis Cluster every stream whose slot lived on another shard was never trimmed, never
group-reaped and never deleted; its size was held only by the `MAXLEN` each `XADD` carries, so it retained the last
`MaxLength` entries (32,768 by default) rather than `MaintainerTrimInterval`, on a key with no TTL. Which streams were
affected moved whenever the cluster was resized and slots were remapped. Upgrade to a version with the per-primary
scan; there is no configuration that works around it.

**Consumer groups from decommissioned pods accumulate.**

These are cleaned up by the maintainer's quarantine logic. A group with no consumers is quarantined on the first cycle it is observed and deleted after `MaintainerQuarantineInterval` (default 1 h). If groups are accumulating faster than they are being cleaned up, lower `MaintainerCheckInterval` to run maintenance more frequently.

**`ERR unknown command` from the fetch loop against a Redis-compatible backend.**

The server does not implement `XREADGROUP`. Some RESP-compatible stores support Streams writes and ranges without the consumer-group read side; Microsoft Garnet is the common case. The fetch loop reports this once at `Critical` and then retries every 30 s instead of once per `PollInterval`, so the log does not flood, and it recovers on its own if the connection later lands on a server that does support the command. Until then this node receives no invalidations over Streams.

Three ways out, in order of preference: switch to Pub/Sub (see [Choosing a transport](#choosing-a-transport)), which those backends do support and which keeps cross-node invalidation; set `InMemoryRedis:BroadcastEnable: false` to keep L1+L2 without invalidation; or disable broadcast app-wide with `BroadcastEnabled: false`.

Note that `RedisStreams:Enabled: false` is worth setting alongside either of the last two, otherwise `RedisStreamHealthMaintainer` still starts and runs `SCAN`/`XINFO` against the same backend.

**`MaintainerSearchPattern` warning: SCAN is matching keys outside this app.**

Set an explicit `MaintainerSearchPattern` scoped to your app's stream key prefix. Use `PrefixStrategy.GetRedisKey("*")` to derive the correct glob at startup, as shown in the [Setting a custom search pattern](#setting-a-custom-search-pattern) section above.

## Related

- [reference/settings.md](../reference/settings.md) — full binding reference for every option above.
- [reference/interfaces.md](../reference/interfaces.md) — `IRedisStreamKeyStrategy`, `IRedisChannelStrategy`, `ITopicProvider`.
- [concepts.md](../concepts.md) — why L1 exists and where broadcast fits in the multilayer architecture.
- [how-to/resilience.md](resilience.md) — connection monitor and retry pipeline that broadcast providers inherit.
