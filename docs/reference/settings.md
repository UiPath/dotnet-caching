# Settings reference

Every binding-visible property on every shipped options class, with shipped defaults, scope, and a one-line note. This page mirrors [`samples/UiPath.Caching.Sample/appsettings.all.json`](../../samples/UiPath.Caching.Sample/appsettings.all.json); keep the two in step.

**Reading the Scope column:**

- **App-wide** — set under `Caching:<section>`; applies to the whole app.
- **Per-provider** — set under a provider section (`InMemoryRedis`, `Redis`, `InMemory`).
- **Per-topic** — set inside a `Topics[]` entry under `Broadcast:RedisStreams` or `Broadcast:RedisPubSub`.
- **Per-policy** — set inside an entry under `Policies` keyed by `typeof(T).FullName`.

**Code-only seams** — properties whose type is a delegate (`Func<...>`), `System.Type`, or a non-collection interface (`ICacheKeyStrategy`, `IRedisStreamKeyStrategy`, etc.) cannot bind from JSON via `ConfigurationBinder` and are not listed in their option's table here. They appear once at the end of each table as a *Code-only seams* footnote. Set them programmatically through the `Action<CacheOptions>` / provider-options delegates passed to `AddCaching` and `Add<Provider>` builder methods.

---

## Caching (CacheOptions)

| Property | Type | Default | Scope | Notes |
|---|---|---|---|---|
| `Enabled` | `bool` | `true` | App-wide | Master on/off switch for the caching subsystem. |
| `TelemetryEnabled` | `bool` | `true` | App-wide | Gates the `ICachingTelemetryProvider` seam; set to `false` to silence all cache metrics. |
| `BroadcastEnabled` | `bool` | `true` | App-wide | Gates the `ITopicFactory` wiring; set to `false` to disable all invalidation broadcasts. |
| `ShardKeyEnabled` | `bool` | `false` | App-wide | Enable for Redis Cluster deployments that span multiple shards. |
| `AuditEnabled` | `bool` | `true` | App-wide | Log writes whose serialized size exceeds `LargeValueThreshold` bytes. |
| `DefaultCache` | `string` | `"InMemoryRedis"` | App-wide | Provider name resolved when no explicit provider is requested; values: `InMemory`, `Redis`, `InMemoryRedis`. |
| `DefaultTopic` | `string` | `"RedisStreams"` | App-wide | Topic provider used when no explicit topic is requested; values: `RedisStreams`, `RedisPubSub`. |
| `SourceUri` | `Uri?` | `urn:<hostname>` | App-wide | Machine/pod identity embedded in cross-node sync events; use _placeholder_ `"urn:machine1"` in config, override per environment. |
| `Separator` | `char` | `':'` | App-wide | Character used to join cache key segments. |
| `AppShortName` | `string` | _required_ | App-wide | Short application name prefixed to every cache key; apps throw at startup if blank or missing. |
| `KeyCasing` | `CacheKeyCasing` | `Insensitive` | App-wide | Key case folding applied when a key is built without an explicit mode, including every implicit `string` -> `CacheKey` conversion. `Insensitive` trims and lowercases (historical behavior); `Sensitive` preserves the caller's casing. **Changing this relocates every cache key** — existing entries become unreachable and are rewritten under the new spelling. The distributed cache always uses `Sensitive` and is unaffected. Scope is the **process**, not the container: `CacheKey` is a struct built by callers without access to DI, so the setting is seeded into the static `CacheKey.DefaultCasing`. Hosting two differently-configured containers in one process is therefore unsupported — the last `AddCaching` wins for both. |
| `LargeValueThreshold` | `int` | `20000` | App-wide | Byte threshold for audit logging; writes whose payload exceeds this are logged when `AuditEnabled` is `true`. |
| `ConnectionMonitorEnabled` | `bool` | `false` | App-wide | Enable Redis health-check polling app-wide; provider-level `ConnectionMonitorEnabled` inherits this when `null`. |
| `LocalLockPoolSize` | `int` | `100` | App-wide | Semaphore pool size for the default local lock — allocation hint, not a hard concurrency cap. |
| `LocalLockPoolInitialFill` | `int` | `10` | App-wide | Semaphores pre-allocated at startup; must be in `[0, LocalLockPoolSize]`. |
| `DistributedLockPollInterval` | `TimeSpan` | `00:00:00.050` | App-wide | Initial wait between distributed-lock acquire retries; doubles per attempt up to `DistributedLockMaxPollInterval`. |
| `DistributedLockMaxPollInterval` | `TimeSpan` | `00:00:00.500` | App-wide | Upper bound for the exponential-backoff retry interval used by the distributed lock. |
| `Policies` | `IDictionary<string, CachePolicy>` | `{}` | App-wide | Named per-cache policies; see [Policies\[\<name\>\] (CachePolicy)](#policiesname-cachepolicy). |
| `DefaultCachePolicy` | `CachePolicy?` | `null` | App-wide | Fills gaps in each cache instance's effective default. Provider-specific options (`IMultilayerCacheOptions.LocalMaxExpiration`, `DefaultExpiration`, lock fields) win per field; `DefaultCachePolicy`'s fields fill any the provider left null. Also merged into every named policy at factory construction. See [Policies\[\<name\>\] (CachePolicy)](#policiesname-cachepolicy). |

*Code-only seams:* the default `ICacheFactory` and `ICachePolicyFactory` registrations are swapped via the fluent `builder.UseCacheFactory<T>()` / `builder.UseCachePolicyFactory<T>()` extensions (instance and `Func<IServiceProvider, T>` overloads also exist). Both are intentionally not bindable from JSON — `ConfigurationBinder` has no string → `Type` converter. See [how-to/extending.md](../how-to/extending.md#swapping-the-default-factories) for custom factory wiring.

---

## Caching:Connections:Redis (RedisConnectionOptions)

| Property | Type | Default | Scope | Notes |
|---|---|---|---|---|
| `ConnectionString` | `string` | _required_ | App-wide | StackExchange.Redis connection string; apps fail at startup without a valid value. Use `"localhost:6379"` as a local placeholder. |
| `ConnectionStringExtraParams` | `string?` | `null` | App-wide | Appended verbatim to `ConnectionString`; useful for Azure Redis extra parameters. |
| `WarmUpOnStart` | `bool` | `false` | App-wide | When `true`, kicks off the Redis connection at host startup via a hosted service (running any auth configurators), best-effort (never fails startup). Default `false` connects lazily on first cache use — **except** that with `PlannedMaintenanceEnabled=true` (the default) the planned-maintenance hosted service opens its own connection (and acquires an Entra token, if configured) at startup regardless of `WarmUpOnStart`. Set `PlannedMaintenanceEnabled=false` to keep startup fully lazy. |
| `BackOffMilliseconds` | `int` | `1000` | App-wide | ms to wait before reconnecting after a connection failure. |
| `HeartbeatConsistencyChecks` | `bool?` | `null` | App-wide | `null` = StackExchange.Redis default; `true` enables heartbeat consistency checks. |
| `HeartbeatInterval` | `TimeSpan?` | `null` | App-wide | `null` = StackExchange.Redis default; TimeSpan override for the heartbeat period. |
| `ProfilerFeatureFlagKey` | `string` | `"RedisProfiler.Enabled"` | App-wide | Feature-flag key consulted before enabling the StackExchange.Redis command profiler. |
| `PlannedMaintenanceEnabled` | `bool` | `true` | App-wide | Tolerate planned-maintenance disconnects gracefully instead of faulting. |
| `PlannedMaintenanceConnectionRetryCount` | `int` | `5` | App-wide | Attempts to establish the planned-maintenance subscription before backing off to quiet retries; failures are logged as warnings, never faulting startup. |
| `PlannedMaintenanceConnectionRetryDelay` | `TimeSpan` | `00:00:05` | App-wide | Delay between planned-maintenance subscription attempts (negative/zero is clamped to 1s). |
| `LogConnectionFailedEvents` | `bool` | `true` | App-wide | Log `ConnectionFailed` events from the multiplexer. |
| `LogConnectionRestoredEvents` | `bool` | `true` | App-wide | Log `ConnectionRestored` events from the multiplexer. |
| `EnableHangDetection` | `bool` | `true` | App-wide | Detect hung write/read channels and emit log warnings. |
| `LastWriteIntervalThresholdMilliseconds` | `int` | `15000` | App-wide | ms since the last write before the channel is declared hung. |
| `LastReadIntervalThresholdMilliseconds` | `int` | `15000` | App-wide | ms since the last read before the channel is declared hung. |
| `DefaultVersion` | `string?` | `"6.0"` | App-wide | Redis server version hint passed to StackExchange.Redis for command compatibility. |
| `HangDetectionDueTime` | `TimeSpan?` | `null` | App-wide | Delay before the first hang-detection check; `null` = 30 s library default. |
| `HangDetectionPeriod` | `TimeSpan?` | `null` | App-wide | Period between hang-detection checks; `null` = library default. |
| `FailFastBacklogPolicy` | `bool?` | `null` | App-wide | `null` = library default; `true` = fail immediately when the command backlog is full. |
| `ProfilerEnabled` | `bool` | `false` | App-wide | Enable StackExchange.Redis command profiler. |
| `ProfilerHasDefaultSession` | `bool` | `true` | App-wide | Start a default profiling session automatically at startup. |
| `ProfilerFlushInterval` | `TimeSpan` | `00:00:01` | App-wide | How often profiling data is flushed to the sink. |
| `ProfilerSessionMaxLifespan` | `TimeSpan?` | `00:01:00` | App-wide | Maximum lifetime of a profiling session before it is auto-closed. |
| `ProfilerSessionMaxChecks` | `int?` | `100` | App-wide | Maximum commands captured per profiling session before it is closed. |
| `ProfilerTrackMetricEnabled` | `bool` | `true` | App-wide | Emit profiler metrics via the telemetry provider. |
| `ConnectionMultiplexerFactoryType` | `string?` | `null` | App-wide | Assembly-qualified type name of a custom `IConnectionMultiplexer` factory; `null` = built-in. See [recipes/opentelemetry-multiplexer-factory.md](../recipes/opentelemetry-multiplexer-factory.md). |
| `AbortOnConnectFail` | `bool` | `false` | App-wide | `false` = retry in background; `true` = throw on first connect failure. |

*Code-only seams:* `ConnectionFactory`, `ProfilingSessionFactory`.

---

## Caching:Broadcast:RedisStreams (RedisStreamsTopicOptions)

App-wide-only fields (per-topic `Topics[]` entries ignore these): `Enabled`, `ConnectionMonitorEnabled`, `TrackStatistics`, `MaintainerEnabled`, `MaintainerCheckInterval`, `MaintainerTrimInterval`, `MaintainerQuarantineInterval`, `MaintainerSearchPattern`.

| Property | Type | Default | Scope | Notes |
|---|---|---|---|---|
| `Enabled` | `bool` | `true` | App-wide only | Enable/disable the Redis Streams provider for the whole app; per-topic override is ignored. |
| `MaxLength` | `long?` | `32768` | App-wide / Per-topic | Max entries in a stream before approximate trimming (`MAXLEN ~`); `null` = unlimited. |
| `Limit` | `long?` | `1024` | App-wide / Per-topic | Max entries returned per `XREAD` call. |
| `PollBatchSize` | `int` | `4096` | App-wide / Per-topic | Max entries fetched per poll cycle. |
| `FieldName` | `string` | `"event"` | App-wide / Per-topic | Stream entry field name used for the serialized event payload. |
| `PollInterval` | `TimeSpan` | `00:00:00.250` | App-wide / Per-topic | Time between poll cycles when no notify signal is received. |
| `ConsumerCapacity` | `int` | `2048` | App-wide / Per-topic | Bounded-channel capacity for in-process event delivery; use `-1` for unbounded. |
| `FullMode` | `BoundedChannelFullMode` | `Wait` | App-wide / Per-topic | Policy when the channel is full: `Wait`, `DropNewest`, `DropOldest`, `DropWrite`. |
| `SlowObserverThreshold` | `TimeSpan` | `00:00:00.250` | App-wide / Per-topic | Log a warning when an observer takes longer than this to process an event. |
| `ConnectionMonitorEnabled` | `bool?` | `null` | App-wide only | `null` = inherit from `CacheOptions.ConnectionMonitorEnabled`. |
| `TrackStatistics` | `bool` | `false` | App-wide only | Emit stream-level statistics via the telemetry provider. |
| `MaintainerEnabled` | `bool` | `true` | App-wide only | Run the background health maintainer that trims and quarantines stale streams. |
| `MaintainerCheckInterval` | `TimeSpan` | `00:30:00` | App-wide only | How often the maintainer checks stream health. |
| `MaintainerTrimInterval` | `TimeSpan` | `01:00:00` | App-wide only | How often the maintainer trims old entries; should exceed `InMemoryRedis.LocalMaxExpiration` to avoid trimming live L1 entries. |
| `MaintainerQuarantineInterval` | `TimeSpan` | `01:00:00` | App-wide only | Idle time before the maintainer removes a consumer group with no active consumers. |
| `MaintainerSearchPattern` | `string?` | `null` | App-wide only | Redis key glob pattern for maintainer scan; `null` = use default prefix pattern. |
| `ProfilerEnabled` | `bool` | `false` | App-wide / Per-topic | Enable per-stream Redis command profiling. |
| `EmitStreamReceivedEvent` | `bool` | `false` | App-wide / Per-topic | Emit a telemetry event for each raw stream message received. |
| `NotifyEnabled` | `bool` | `false` | App-wide / Per-topic | Opt-in pub/sub doorbell — `PUBLISH` after `XADD` wakes the consumer immediately instead of waiting `PollInterval`. |
| `NotifyChannelName` | `string` | `"notify"` | App-wide / Per-topic | Channel suffix appended to the stream key; ignored when `NotifyChannelStrategy` is set in code. |
| `NotifyShardedPubSub` | `bool` | `false` | App-wide / Per-topic | `true` = use `SPUBLISH`/`SSUBSCRIBE` (Redis 7.0+); ignored when `NotifyChannelStrategy` is set in code. |
| `NotifySubscriberTimeout` | `TimeSpan?` | `null` | App-wide / Per-topic | Resubscribe interval when `Subscribe` fails; `null` or non-positive = multiplexer timeout. |
| `NotifySubscriberDueTime` | `TimeSpan?` | `null` | App-wide / Per-topic | Delay before the first subscribe attempt; `null` = half of resolved `NotifySubscriberTimeout`. |

Per-topic overrides: add entries to `Topics[]` under `Broadcast:RedisStreams`. Each entry is matched case-insensitively on `Name`; only present fields override app-wide values (delta overlay). See [how-to/broadcast.md#per-topic-overrides](../how-to/broadcast.md#per-topic-overrides).

*Code-only seams:* `RedisStreamKeyStrategy`, `NotifyChannelStrategy`.

---

## Caching:Broadcast:RedisPubSub (RedisPubSubTopicOptions)

App-wide-only fields (per-topic `Topics[]` entries ignore these): `Enabled`, `ConnectionMonitorEnabled`.

| Property | Type | Default | Scope | Notes |
|---|---|---|---|---|
| `Enabled` | `bool` | `false` | App-wide only | Enable/disable the Redis Pub/Sub provider for the whole app; per-topic override is ignored. Default is `false` — opt-in. |
| `ConsumerCapacity` | `int` | `2048` | App-wide / Per-topic | Bounded-channel capacity for in-process event delivery. |
| `FullMode` | `BoundedChannelFullMode` | `Wait` | App-wide / Per-topic | Policy when the channel is full: `Wait`, `DropNewest`, `DropOldest`, `DropWrite`. |
| `SlowObserverThreshold` | `TimeSpan` | `00:00:00.250` | App-wide / Per-topic | Log a warning when an observer takes longer than this to process an event. |
| `ConnectionMonitorEnabled` | `bool?` | `null` | App-wide only | `null` = inherit from `CacheOptions.ConnectionMonitorEnabled`. |
| `SubscriberTimeout` | `TimeSpan?` | `null` | App-wide / Per-topic | Resubscribe interval when `Subscribe` fails; `null` = multiplexer timeout. |
| `SubscriberDueTime` | `TimeSpan?` | `null` | App-wide / Per-topic | Delay before the first subscribe attempt; `null` = half of resolved `SubscriberTimeout`. |

Per-topic overrides: add entries to `Topics[]` under `Broadcast:RedisPubSub`. Each entry is matched case-insensitively on `Name`; only present fields override app-wide values (delta overlay). See [how-to/broadcast.md#per-topic-overrides](../how-to/broadcast.md#per-topic-overrides).

*Code-only seams:* `RedisChannelStrategy`.

---

## Caching:InMemoryRedis (InMemoryRedisCacheOptions)

| Property | Type | Default | Scope | Notes |
|---|---|---|---|---|
| `Enabled` | `bool` | `true` | Per-provider | Enable/disable this two-tier (L1 in-memory + L2 Redis) cache provider. |
| `DefaultExpiration` | `TimeSpan?` | `01:00:00` | Per-provider | Default TTL when no per-call or per-policy expiration is set. `null` means *inherit*, which resolves to `CachePolicy.DefaultDistributedExpiration` (1 h) — it does **not** mean "never expire". For unbounded entries set `TimeSpan.MaxValue`. |
| `Timeout` | `TimeSpan` | `00:00:01` | Per-provider | Max wait for a cache operation before giving up and falling through. |
| `TrackStatistics` | `bool` | `true` | Per-provider | Emit hit/miss/eviction counters via the telemetry provider. |
| `StatisticsFlushInterval` | `TimeSpan` | `00:01:00` | Per-provider | How often statistics are flushed to the telemetry sink. |
| `BroadcastEnable` | `bool` | `true` | Per-provider | Publish/consume cross-node L1 invalidations for this provider. `false` keeps L1+L2 with no broadcast traffic. Can only narrow `CacheOptions.BroadcastEnabled`, never widen it. Note the default is the opposite of `InMemoryCacheOptions.BroadcastEnable`. |
| `Topic` | `string?` | `null` | Per-provider | Topic name for L1 invalidation broadcasts; `null` = use `CacheOptions.DefaultTopic`. |
| `LocalMaxExpiration` | `TimeSpan?` | `null` | Per-provider | Cap on the L1 (in-memory) TTL while L2 is connected; `null` = no cap beyond `DefaultExpiration`. |
| `ConnectionMonitorEnabled` | `bool?` | `null` | Per-provider | `null` = inherit from `CacheOptions.ConnectionMonitorEnabled`. |
| `CacheNullValues` | `bool` | `false` | Per-provider | Persist `null`/empty factory returns as sentinels to suppress thundering-herd on missing keys. |
| `ConnectionMonitorPeriod` | `TimeSpan?` | `00:00:05` | Per-provider | How often the connection monitor probes Redis health. |
| `SizeLimit` | `long?` | `null` | Per-provider | Max bytes for the in-memory tier; `null` = unlimited. |
| `CompactionPercentage` | `double?` | `null` | Per-provider | Fraction of `SizeLimit` to free when the limit is hit; `null` = runtime default (0.05). |
| `UseLocalOnlyWhenDisconnected` | `bool?` | `null` | Per-provider | `null` = `false`; `true` = serve L1-only responses when L2 is disconnected. |
| `LocalMaxExpirationDisconnected` | `TimeSpan?` | `00:00:30` | Per-provider | L1 TTL cap while L2 is disconnected; limits the stale-read window. |
| `LocalLockEnabled` | `bool?` | `true` | Per-provider | Acquire a local (in-process) lock before calling the value factory. |
| `LocalLockTimeout` | `TimeSpan?` | `00:00:00.500` | Per-provider | Max wait to acquire the local lock before bypassing it. |
| `DistributedLockEnabled` | `bool?` | `null` | Per-provider | Acquire a distributed (Redis) lock before calling the value factory; `null` = not configured. |
| `DistributedLockTimeout` | `TimeSpan?` | `00:00:00.500` | Per-provider | Max wait to acquire the distributed lock. |
| `DistributedLockExpiry` | `TimeSpan?` | `00:00:05` | Per-provider | Redis key TTL for the distributed lock (safety expiry to prevent deadlocks). |

*Code-only seams:* `EntryFactory`, `CacheKeyStrategy`, `TopicKeyStrategy`, `SizeProvider`, `LockKeyStrategy`.

---

## Caching:Redis (RedisCacheOptions)

| Property | Type | Default | Scope | Notes |
|---|---|---|---|---|
| `Enabled` | `bool` | `true` | Per-provider | Enable/disable the standalone Redis cache provider. |
| `DefaultExpiration` | `TimeSpan?` | `01:00:00` | Per-provider | Default TTL when no per-call or per-policy expiration is set. `null` means *inherit*, which resolves to `CachePolicy.DefaultDistributedExpiration` (1 h) — it does **not** mean "never expire". For unbounded entries set `TimeSpan.MaxValue`. |
| `KeyPrefix` | `string` | `""` | Per-provider | Prefix prepended to every Redis key before `AppShortName` and the cache key segments. |
| `Timeout` | `TimeSpan` | `00:00:01` | Per-provider | Max wait for a cache operation before giving up and falling through. |
| `ConnectionMonitorEnabled` | `bool?` | `null` | Per-provider | `null` = inherit from `CacheOptions.ConnectionMonitorEnabled`. |
| `CacheNullValues` | `bool` | `false` | Per-provider | Persist `null`/empty factory returns as sentinels to suppress thundering-herd on missing keys. |
| `KeyReadTelemetryEnabled` | `bool` | `false` | Per-provider | Opt-in per-key read attribution: each read emits a `Redis` dependency carrying the key in `data` (one per hash key for hash reads), with a `BatchId` shared across the operation. Off by default because raw keys are high-cardinality; the per-operation hit/miss metric is always emitted regardless. |
| `AwaitRefresh` | `bool` | `false` | Per-provider | Wait for the server to apply a refresh instead of sending `KEYEXPIRE`/`PERSIST` fire-and-forget. Off by default, keeping the round trip off the sliding-expiration path, which runs on every read of a sliding entry. While off, a refresh is unverifiable: `RefreshAsync` returns `false` whether it succeeded, failed or the key was absent; the reply is never seen, so a rejected command is neither logged nor retried by the resilience pipeline, and telemetry records every refresh as unsuccessful; and the new deadline is not yet in effect when the call returns. Turn it on where a lost refresh matters more than a round trip. `AddDistributedCache` sets it for its own provider. |

*Code-only seams:* `EntryFactory`, `CacheKeyStrategy`, `RedisKeyStrategyFactory`.

---

## Caching:InMemory (InMemoryCacheOptions)

| Property | Type | Default | Scope | Notes |
|---|---|---|---|---|
| `Enabled` | `bool` | `true` | Per-provider | Enable/disable the in-memory-only cache provider. |
| `DefaultExpiration` | `TimeSpan?` | `01:00:00` | Per-provider | Default TTL when no per-call or per-policy expiration is set. `null` means *inherit*, which resolves to `CachePolicy.DefaultDistributedExpiration` (1 h) — it does **not** mean "never expire". For unbounded entries set `TimeSpan.MaxValue`. |
| `Timeout` | `TimeSpan` | `00:00:01` | Per-provider | Max wait for a cache operation before giving up. |
| `TrackStatistics` | `bool` | `true` | Per-provider | Emit hit/miss/eviction counters via the telemetry provider. |
| `StatisticsFlushInterval` | `TimeSpan` | `00:01:00` | Per-provider | How often statistics are flushed to the telemetry sink. |
| `BroadcastEnable` | `bool` | `false` | Per-provider | Enable broadcast invalidation for this in-memory cache instance. |
| `Topic` | `string?` | `null` | Per-provider | Topic name for invalidation broadcasts; `null` = use `CacheOptions.DefaultTopic`. |
| `LocalMaxExpiration` | `TimeSpan?` | `01:00:00` | Per-provider | Cap on in-memory TTL; `null` = no cap (falls back to the resolved `DefaultExpiration`). |
| `ConnectionMonitorEnabled` | `bool?` | `null` | Per-provider | Inert for this provider (no Redis connection); present to satisfy `IMultilayerCacheOptions`. |
| `CacheNullValues` | `bool` | `false` | Per-provider | Persist `null`/empty factory returns as sentinels. |
| `ConnectionMonitorPeriod` | `TimeSpan?` | `00:00:05` | Per-provider | Inert for this provider; present to satisfy `IMultilayerCacheOptions`. |
| `SizeLimit` | `long?` | `null` | Per-provider | Max bytes for the in-memory store; `null` = unlimited. |
| `CompactionPercentage` | `double?` | `null` | Per-provider | Fraction of `SizeLimit` to free when the limit is hit; `null` = runtime default (0.05). |
| `UseLocalOnlyWhenDisconnected` | `bool?` | `null` | Per-provider | Inert for this provider; present to satisfy `IMultilayerCacheOptions`. |
| `LocalMaxExpirationDisconnected` | `TimeSpan?` | `00:00:30` | Per-provider | Inert for this provider; present to satisfy `IMultilayerCacheOptions`. |
| `LocalLockEnabled` | `bool?` | `true` | Per-provider | Acquire a local (in-process) lock before calling the value factory. |
| `LocalLockTimeout` | `TimeSpan?` | `00:00:00.500` | Per-provider | Max wait to acquire the local lock before bypassing it. |
| `DistributedLockEnabled` | `bool?` | `null` | Per-provider | Inert for this provider; present to satisfy `IMultilayerCacheOptions`. Startup validation still applies. |
| `DistributedLockTimeout` | `TimeSpan?` | `null` | Per-provider | Inert for this provider; present to satisfy `IMultilayerCacheOptions`. |
| `DistributedLockExpiry` | `TimeSpan?` | `null` | Per-provider | Inert for this provider; present to satisfy `IMultilayerCacheOptions`. |

*Code-only seams:* `EntryFactory`, `CacheKeyStrategy`, `TopicKeyStrategy`, `SizeProvider`, `LockKeyStrategy`.

---

## Queue caches (`UiPath.Caching.Queue`)

The queue package's `AddQueueMemory` / `AddQueueRedis` / `AddQueueInMemoryRedis` bind from the **same sections as the core providers** by default — `Caching:InMemory`, `Caching:Redis`, `Caching:InMemoryRedis` — into their own options types. A key both types declare (`Enabled`, `DefaultExpiration`, `LocalMaxExpiration`, …) therefore configures both the core cache and the set cache of that backing; keys one type lacks are ignored by the binder. Pass a section name to any of the three to bind from elsewhere. The Redis tier of the multilayer set cache reuses `RedisCacheOptions` and `RedisSetCacheOptions`.

### Caching:InMemory (InMemoryQueueCacheOptions)

| Property | Type | Default | Scope | Notes |
|---|---|---|---|---|
| `Enabled` | `bool` | `true` | Per-provider | Enable/disable the in-memory set cache. |
| `DefaultExpiration` | `TimeSpan?` | `01:00:00` | Per-provider | Whole-set lifetime when neither the call nor the policy names one; every add re-applies it. `null` resolves to `CachePolicy.DefaultDistributedExpiration` (1 h); `TimeSpan.MaxValue` keeps a set forever. |
| `LocalMaxExpiration` | `TimeSpan?` | `null` | Per-provider | Cap on a stored set's lifetime. `null` by default: with no backing tier `DefaultExpiration` already bounds every set. |
| `ConnectionMonitorEnabled` | `bool` | `false` | Per-provider | Inert for this provider (no backing tier); present for parity with `InMemoryRedisQueueCacheOptions`. |
| `ConnectionMonitorPeriod` | `TimeSpan?` | `00:00:05` | Per-provider | Inert for this provider. |
| `UseLocalOnlyWhenDisconnected` | `bool` | `false` | Per-provider | Inert for this provider. |
| `LocalMaxExpirationDisconnected` | `TimeSpan?` | `00:00:30` | Per-provider | Inert for this provider. |
| `TrackStatistics` | `bool` | `true` | Per-provider | Emit hit/miss/eviction counters via the telemetry provider. |
| `StatisticsFlushInterval` | `TimeSpan` | `00:01:00` | Per-provider | How often statistics are flushed to the telemetry sink. |
| `SizeLimit` | `long?` | `null` | Per-provider | Max size for the in-memory store; `null` = unlimited. |
| `CompactionPercentage` | `double?` | `null` | Per-provider | Fraction of `SizeLimit` to free when the limit is hit; `null` = runtime default (0.05). |

*Code-only seams:* `SizeProvider`.

### Caching:Redis (RedisSetCacheOptions)

| Property | Type | Default | Scope | Notes |
|---|---|---|---|---|
| `Enabled` | `bool` | `true` | Per-provider | Enable/disable the Redis set cache. |
| `ResilienceKeyName` | `string?` | `null` | Per-provider | Name of the resilience pipeline applied to destructive reads (`SPOP`), resolved via `IResiliencePipelineProvider`; `null` or empty runs them with no pipeline. |

Lifetimes and the connection come from `RedisCacheOptions` (`DefaultExpiration`, `ConnectionMonitorEnabled`, …) bound from the same section.

### Caching:InMemoryRedis (InMemoryRedisQueueCacheOptions)

| Property | Type | Default | Scope | Notes |
|---|---|---|---|---|
| `Enabled` | `bool` | `true` | Per-provider | Enable/disable the multilayer set cache. |
| `DefaultExpiration` | `TimeSpan?` | `null` | Per-provider | Whole-set lifetime when neither the call nor the policy names one. A value outranks the Redis tier's `RedisCacheOptions.DefaultExpiration` for sets written through this provider, as `InMemoryRedisCacheOptions.DefaultExpiration` does for the other caches; `null` inherits the tier's default. |
| `LocalMaxExpiration` | `TimeSpan?` | `00:01:00` | Per-provider | How long a locally cached set snapshot is served before it is re-fetched from Redis. Also bounds the staleness window for mutations made on other nodes — this tier does not subscribe to broadcast invalidation. `null` = no time bound (not recommended multi-node). |
| `ConnectionMonitorEnabled` | `bool` | `false` | Per-provider | Monitor the Redis tier's connection. Required for `UseLocalOnlyWhenDisconnected`; enabled without it, snapshots are dropped rather than served while Redis is unreachable. |
| `ConnectionMonitorPeriod` | `TimeSpan?` | `00:00:05` | Per-provider | How often the connection monitor re-evaluates a failed connection. |
| `UseLocalOnlyWhenDisconnected` | `bool` | `false` | Per-provider | Serve reads and apply mutations on the local snapshot while Redis is unreachable. Requires `ConnectionMonitorEnabled`. |
| `LocalMaxExpirationDisconnected` | `TimeSpan?` | `00:00:30` | Per-provider | Lifetime cap on local set state written while Redis is unreachable, so it dies quickly once connectivity returns. |
| `TrackStatistics` | `bool` | `true` | Per-provider | Emit hit/miss/eviction counters via the telemetry provider. |
| `StatisticsFlushInterval` | `TimeSpan` | `00:01:00` | Per-provider | How often statistics are flushed to the telemetry sink. |
| `SizeLimit` | `long?` | `null` | Per-provider | Max size for the local snapshot store; `null` = unlimited. |
| `CompactionPercentage` | `double?` | `null` | Per-provider | Fraction of `SizeLimit` to free when the limit is hit; `null` = runtime default (0.05). |

*Code-only seams:* `SizeProvider`.

---

## Caching:Distributed (UiPathDistributedCacheOptions)

Registered in code via `builder.AddDistributedCache(providerName)`; the backing tier is a required
argument (`Redis` recommended, `InMemoryRedis`, or `InMemory`). The options object is passed to the
extension rather than bound from configuration.

| Property | Type | Default | Scope | Notes |
|---|---|---|---|---|
| `CacheKeyStrategy` | `ICacheKeyStrategy?` | `null` | Per registration | Composes the stored key from the caller key. Null applies `PrefixCacheKeyStrategy` with `DefaultKeyPrefix` (`"d"`); pass `new PrefixCacheKeyStrategy("d:sess")` to sub-namespace while keeping that prefix, or `DefaultCacheKeyStrategy` to opt out of prefixing entirely. Code-only seam. |
| `RedisKeyDifferentiator` | `string?` | `null` | Per registration | Fills the slot after `AppShortName` that the application's caches fill with a `RedisTypePrefixes` value. Null uses `DefaultRedisKeyDifferentiator` (`"dh"`). Inert on the `InMemory` tier; a value matching a `RedisTypePrefixes` value is rejected at registration. Prefixes belonging to packages layered on top of `UiPath.Caching` are **not** checked — it cannot see them without depending on them — so avoid those too: `UiPath.Caching.Queue`'s set cache uses `"se"`. |
| `RedisKeyStrategyFactory` | `IRedisKeyStrategyFactory?` | `null` | Per registration | Builds the Redis key, receiving `RedisKeyDifferentiator`. Null inherits the application's `RedisCacheOptions.RedisKeyStrategyFactory`, keeping its `AppShortName`, separator and sharding conventions. Code-only seam. |
| `PolicyName` | `string?` | `null` | Per registration | Named `CachePolicy` applied to the adapter's operations; an unregistered name fails fast at startup. |
| `DefaultEntryExpiration` | `TimeSpan?` | `null` | Per registration | Expiration used when the caller supplies none. `IDistributedCache` treats absent expiration as "until removed"; unless `AllowUnboundedEntries` is set that is mapped to this value, falling back to the backing tier's `DefaultExpiration` and then to `CachePolicy.DefaultDistributedExpiration`. |
| `AllowUnboundedEntries` | `bool` | `false` | Per registration | Honor "no expiration" literally. Off by default, and now the only way to reach an unbounded entry through this adapter without naming a lifetime — an unset default resolves to `CachePolicy.DefaultDistributedExpiration` rather than to "until removed". |

Entries are stored as a Redis hash (`data`, `absexp`, `sldexp`) in a keyspace disjoint from the
application's own caches, so `Refresh` reads only the expiration metadata. Keys are always
case-sensitive regardless of `CacheOptions.KeyCasing`.

**`IBufferDistributedCache`.** On `net9.0` and later the registered `IDistributedCache` also implements
[`IBufferDistributedCache`][ibdc], the buffer-based half of the same contract, so a caller reads into an
`IBufferWriter<byte>` and writes from a `ReadOnlySequence<byte>` instead of trading a fresh array per
operation. Consumers find it the way the framework's own caches expose it — a type check on the resolved
`IDistributedCache`, which is what `HybridCache` does — so nothing extra is registered and no code
changes. Both halves share one read and write path: same keyspace, same expiration and sliding, same
stored fields, so entries written through either are readable through the other and by any other client
on the conventional layout. `TryGet` reports hit and payload separately, which is the one thing the array
half cannot express: an entry stored with an empty payload is a hit that writes no bytes, where
`Get` returns an empty array for that and for a miss alike. On a write, what happens to the caller's
buffer depends on the tier: `Redis` is handed a single-segment sequence as the caller's own memory — no
array in between, since the connection copies it as the command is written and the write is awaited to
completion (see [`IMemorySerializerProxy`](../how-to/extending.md#lending-memory-imemoryserializerproxy)) —
and flattens a segmented one into a rented buffer returned after the await; the memory tiers keep what
they are handed, so there the sequence is copied into an owned array. Either way a pooled buffer is safe
to reuse the moment the call returns. The `net8.0` floor pins
`Microsoft.Extensions.Caching.Abstractions` to 8.0.0, which predates the interface; there the type check
comes up empty and consumers stay on the array path.

[ibdc]: https://learn.microsoft.com/dotnet/api/microsoft.extensions.caching.distributed.ibufferdistributedcache

**Keyspace separation.** A full Redis key carries three independent segments, each answering a
different question:

```
app  :  dh  :  d:  <caller key>
└─┬─┘   └┬─┘   └┬┘
  │      │      └─ CacheKey level: CacheKeyStrategy ("d:" by default)
  │      └─ RedisKeyDifferentiator — separates this cache from the application's own
  └─ CacheOptions.AppShortName — separates applications sharing one Redis
```

`AppShortName` is required and app-wide, so it is never this registration's job. `CacheKeyStrategy`
prefixes the `CacheKey` itself, so a distributed entry cannot be reached through the application's own
`ICache`/`IHashCache` with the bare caller key, and the memory tiers' local lock keyspace (keyed by
provider name plus cache key) stays separate too — neither of which a Redis-key-level segment can do.
`RedisKeyDifferentiator` and `RedisKeyStrategyFactory` separate the physical Redis keyspace, taking the
slot the application's caches fill with a `RedisTypePrefixes` value. Because a differentiator only separates anything if the factory
honors it, registration composes a probe key both ways and fails when the distributed key matches what
the application's own caches would produce — so a factory that ignores its differentiator argument is
rejected instead of quietly sharing the keyspace.

The cache-key strategy is applied by the adapter rather than by the backing provider's own
`ICacheOptions.CacheKeyStrategy`, which `RedisHashCache` does not consult — routing it through the
provider would make the stored key depend on which tier is configured.

> **Keys appear in logs.** `IDistributedCache` keys are chosen by the consumer and can be secrets — under
> ASP.NET Core session the key is the session id. This adapter names the key in its two own messages (a failed
> write at `Warning`, a no-op remove at `Debug`), and the composed key is passed to the backing cache, which
> logs it in its own diagnostics too: `MultilayerHashCache` includes the `CacheKey` in five `LogLevel.Warning` messages (raised on
> inner-cache exceptions) and in several Debug/Trace ones, and `RedisHashCache` includes the physical key in
> `LogLargeValueDetected` (Warning) and `LogRefreshingKey` (Trace). Keys stored verbatim for parity with
> the conventional layout also means they appear in `KEYS`/`SCAN` output and RDB
> snapshots. Treat logs and Redis dumps for this provider as containing session identifiers, or filter the
> `UiPath.Caching` log categories accordingly.

**`Refresh` waits for the server on this provider.** `AddDistributedCache` sets
`RedisCacheOptions.AwaitRefresh` on the private provider it builds, so `IDistributedCache.Refresh` — and the
sliding extension a `Get` performs — costs a round trip but is in effect when the call returns, a rejection
reaches the log and the resilience pipeline, and telemetry reflects the real outcome. Deliberate for a
session store: a silently dropped refresh shortens the session and fire-and-forget cannot report one. The
application's own caches keep the fire-and-forget default; see `AwaitRefresh` above.

**Backing-tier caveats.** `InMemoryRedis` carries a stale-read window until backplane invalidation
lands and requires `AddInMemoryRedis` for its broadcast wiring (enforced at startup). `InMemory`
stores entries only in memory, where `InMemoryCacheOptions.LocalMaxExpiration` (1 hour by default)
caps every entry regardless of the caller's requested expiration — use it for tests and single-node
scenarios, not for long-lived entries.

---

## Caching:ResiliencePolicies (ResiliencePoliciesOptions)

| Property | Type | Default | Scope | Notes |
|---|---|---|---|---|
| `Enabled` | `bool` | `true` | App-wide | Enable Polly circuit-breaker and retry policies for Redis operations. |
| `DurationOfBreak` | `TimeSpan` | `00:01:00` | App-wide | How long the circuit stays open after tripping. |
| `ExceptionsAllowedBeforeBreaking` | `int` | `500` | App-wide | Number of failures within the sampling window before the circuit opens. |
| `RequestTimeout` | `TimeSpan?` | `00:00:01` | App-wide | Per-operation timeout enforced by the Polly pipeline. |
| `RetryCount` | `int?` | `1` | App-wide | Number of immediate retries before propagating a failure. |
| `TelemetryEnabled` | `bool` | `true` | App-wide | Emit circuit-breaker state-change events via the telemetry provider. |
| `RethrowCircuitBreakerExceptions` | `bool` | `false` | App-wide | `true` = rethrow `BrokenCircuitException` to the caller instead of swallowing it. |

---

## Policies\[\<name\>\] (CachePolicy)

Entries are keyed by string under `Caching:Policies`. `ICache<T>` and `IHashCache<T>` bind by name at construction (default key = `typeof(T).FullName`). Unregistered names fall back to `DefaultCachePolicy`.

| Property | Type | Default | Scope | Notes |
|---|---|---|---|---|
| `LocalExpiration` | `TimeSpan?` | `null` | Per-policy | L1 (in-memory) TTL cap for this policy; `null` = inherit from provider `LocalMaxExpiration`. Effective L1 TTL is `min(entry.Expiration, LocalExpiration)`. |
| `LocalExpirationDisconnected` | `TimeSpan?` | `null` | Per-policy | L1 TTL cap when L2 is disconnected; `null` = inherit from provider `LocalMaxExpirationDisconnected`. |
| `DistributedExpiration` | `TimeSpan?` | `null` | Per-policy | L2 (Redis) entry lifetime; `null` = use provider `DefaultExpiration`, and under that `CachePolicy.DefaultDistributedExpiration` (1 h). Per-call expiration arguments still take precedence. Set `TimeSpan.MaxValue` for unbounded. |
| `FactoryTimeout` | `TimeSpan?` | `null` | Per-policy | Max time allowed for the value factory before it is abandoned; `null` = no timeout. |
| `JitterMaxDuration` | `TimeSpan?` | `null` | Per-policy | Max random duration added to the L2 TTL at write time (uniform in `[0, JitterMaxDuration)`); `null` or `00:00:00` disables jitter. Caller-supplied expiration is honored exactly (no jitter). |
| `RehydrateEnabled` | `bool?` | `null` | Per-policy | Master switch for proactive background refresh; `null` = inherit (default off). |
| `Rehydrate` | `RehydrateOptions?` | `null` | Per-policy | Rehydrate tuning; replaced wholesale (no per-field merge) when overriding. See [Policies\[\<name\>\].Rehydrate (RehydrateOptions)](#policiesnamerehydrate-rehydrateoptions). |
| `Lock` | `LockProfile?` | `null` | Per-policy | Per-cache lock overrides; field-level merged against the default policy. See [Policies\[\<name\>\].Lock (LockProfile)](#policiesnamelock-lockprofile). |

---

## Policies\[\<name\>\].Rehydrate (RehydrateOptions)

Nested under a `CachePolicy` entry. Replaced wholesale when a named policy overrides it — redeclare all fields you want to keep.

| Property | Type | Default | Scope | Notes |
|---|---|---|---|---|
| `Threshold` | `double` | `0.75` | Per-policy | Soft-TTL trigger fraction in `(0, 1]`. Refresh fires once the **elapsed** lifetime reaches `Threshold × Duration` (i.e. at `0.75`, refresh fires after 75% of the TTL has elapsed — 25% remaining). |
| `BaseCooldown` | `TimeSpan` | `00:00:05` | Per-policy | Minimum cooldown between consecutive refresh attempts after the trigger fires. |
| `MaxCooldown` | `TimeSpan` | `00:05:00` | Per-policy | Upper bound on the exponential-backoff cooldown after repeated refresh failures. |
| `TimeoutFraction` | `double` | `0.5` | Per-policy | Background factory timeout as a fraction of the entry's `Duration`; floored at 1 s. |
| `Name` | `string?` | `null` | Per-policy | Profile label surfaced on telemetry as the `profile` dimension. |

---

## Policies\[\<name\>\].Lock (LockProfile)

Nested under a `CachePolicy` entry. Field-level merged against the default policy's `LockProfile` — set only the fields you want to override; `null` inherits.

| Property | Type | Default | Scope | Notes |
|---|---|---|---|---|
| `LocalLockEnabled` | `bool?` | `null` | Per-policy | `null` = inherit from provider options or default policy. |
| `DistributedLockEnabled` | `bool?` | `null` | Per-policy | `null` = inherit from provider options or default policy. |
| `LocalLockTimeout` | `TimeSpan?` | `null` | Per-policy | `null` = inherit from provider options or default policy. |
| `DistributedLockTimeout` | `TimeSpan?` | `null` | Per-policy | `null` = inherit from provider options or default policy. |
| `DistributedLockExpiry` | `TimeSpan?` | `null` | Per-policy | Redis key TTL for the distributed lock; `null` = inherit from provider options or default policy. |
