# Settings reference

Every binding-visible property on every shipped options class, with shipped defaults, scope, and a one-line note. This page mirrors [`Sample.AspNetCore/appsettings.all.json`](../../Sample.AspNetCore/appsettings.all.json) 1:1 — when the JSON drifts, the bind-validation test in `Caching.Tests/AppSettingsAllJsonBindingTests.cs` fails.

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
| `BackOffMilliseconds` | `int` | `1000` | App-wide | ms to wait before reconnecting after a connection failure. |
| `HeartbeatConsistencyChecks` | `bool?` | `null` | App-wide | `null` = StackExchange.Redis default; `true` enables heartbeat consistency checks. |
| `HeartbeatInterval` | `TimeSpan?` | `null` | App-wide | `null` = StackExchange.Redis default; TimeSpan override for the heartbeat period. |
| `ProfilerFeatureFlagKey` | `string` | `"RedisProfiler.Enabled"` | App-wide | Feature-flag key consulted before enabling the StackExchange.Redis command profiler. |
| `PlannedMaintenanceEnabled` | `bool` | `true` | App-wide | Tolerate planned-maintenance disconnects gracefully instead of faulting. |
| `LogConnectionFailedEvents` | `bool` | `true` | App-wide | Log `ConnectionFailed` events from the multiplexer. |
| `LogConnectionRestoredEvents` | `bool` | `true` | App-wide | Log `ConnectionRestored` events from the multiplexer. |
| `EnableHangDetection` | `bool` | `true` | App-wide | Detect hung write/read channels and emit log warnings. |
| `LastWriteIntervalThresholdMilliseconds` | `int` | `15000` | App-wide | ms since the last write before the channel is declared hung. |
| `LastReadIntervalThresholdMilliseconds` | `int` | `15000` | App-wide | ms since the last read before the channel is declared hung. |
| `DefaultVersion` | `string?` | `"6.0"` | App-wide | Redis server version hint passed to StackExchange.Redis for command compatibility. |
| `HangDetectionDueTime` | `TimeSpan?` | `null` | App-wide | Delay before the first hang-detection check; `null` = 30 s library default. |
| `HangDetectionPeriod` | `TimeSpan?` | `null` | App-wide | Period between hang-detection checks; `null` = library default. |
| `FailFastBacklogPolicy` | `bool?` | `null` | App-wide | `null` = library default; `true` = fail immediately when the command backlog is full. |
| `ThreadPoolSocketManager` | `bool?` | `null` | App-wide | `null` = library default; `true` = use the thread-pool socket manager. |
| `ProfilerEnabled` | `bool` | `false` | App-wide | Enable StackExchange.Redis command profiler. |
| `ProfilerHasDefaultSession` | `bool` | `true` | App-wide | Start a default profiling session automatically at startup. |
| `ProfilerFlushInterval` | `TimeSpan` | `00:00:01` | App-wide | How often profiling data is flushed to the sink. |
| `ProfilerSessionMaxLifespan` | `TimeSpan?` | `00:01:00` | App-wide | Maximum lifetime of a profiling session before it is auto-closed. |
| `ProfilerSessionMaxChecks` | `int?` | `100` | App-wide | Maximum commands captured per profiling session before it is closed. |
| `ProfilerTrackMetricEnabled` | `bool` | `true` | App-wide | Emit profiler metrics via the telemetry provider. |
| `ConnectionMultiplexerFactoryType` | `string?` | `null` | App-wide | Assembly-qualified type name of a custom `IConnectionMultiplexer` factory; `null` = built-in. See [recipes/opentelemetry-multiplexer-factory.md](../recipes/opentelemetry-multiplexer-factory.md). |
| `AbortOnConnectFail` | `bool` | `false` | App-wide | `false` = retry in background; `true` = throw on first connect failure. |

*Code-only seams:* `ConnectionFactory`, `ProfilingSessionFactory`, `Clock`.

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
| `DefaultExpiration` | `TimeSpan?` | `01:00:00` | Per-provider | Default TTL when no per-call or per-policy expiration is set. |
| `Timeout` | `TimeSpan` | `00:00:01` | Per-provider | Max wait for a cache operation before giving up and falling through. |
| `TrackStatistics` | `bool` | `true` | Per-provider | Emit hit/miss/eviction counters via the telemetry provider. |
| `StatisticsFlushInterval` | `TimeSpan` | `00:01:00` | Per-provider | How often statistics are flushed to the telemetry sink. |
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

*Code-only seams:* `Clock`, `EntryFactory`, `CacheKeyStrategy`, `TopicKeyStrategy`, `SizeProvider`, `LockKeyStrategy`.

---

## Caching:Redis (RedisCacheOptions)

| Property | Type | Default | Scope | Notes |
|---|---|---|---|---|
| `Enabled` | `bool` | `true` | Per-provider | Enable/disable the standalone Redis cache provider. |
| `DefaultExpiration` | `TimeSpan?` | `01:00:00` | Per-provider | Default TTL when no per-call or per-policy expiration is set. |
| `KeyPrefix` | `string` | `""` | Per-provider | Prefix prepended to every Redis key before `AppShortName` and the cache key segments. |
| `Timeout` | `TimeSpan` | `00:00:01` | Per-provider | Max wait for a cache operation before giving up and falling through. |
| `ConnectionMonitorEnabled` | `bool?` | `null` | Per-provider | `null` = inherit from `CacheOptions.ConnectionMonitorEnabled`. |
| `CacheNullValues` | `bool` | `false` | Per-provider | Persist `null`/empty factory returns as sentinels to suppress thundering-herd on missing keys. |
| `KeyReadTelemetryEnabled` | `bool` | `false` | Per-provider | Opt-in per-key read attribution: each read emits a `Redis` dependency carrying the key in `data` (one per hash key for hash reads), with a `BatchId` shared across the operation. Off by default because raw keys are high-cardinality; the per-operation hit/miss metric is always emitted regardless. |

*Code-only seams:* `Clock`, `EntryFactory`, `CacheKeyStrategy`, `RedisKeyStrategyFactory`.

---

## Caching:InMemory (InMemoryCacheOptions)

| Property | Type | Default | Scope | Notes |
|---|---|---|---|---|
| `Enabled` | `bool` | `true` | Per-provider | Enable/disable the in-memory-only cache provider. |
| `DefaultExpiration` | `TimeSpan?` | `01:00:00` | Per-provider | Default TTL when no per-call or per-policy expiration is set. |
| `Timeout` | `TimeSpan` | `00:00:01` | Per-provider | Max wait for a cache operation before giving up. |
| `TrackStatistics` | `bool` | `true` | Per-provider | Emit hit/miss/eviction counters via the telemetry provider. |
| `StatisticsFlushInterval` | `TimeSpan` | `00:01:00` | Per-provider | How often statistics are flushed to the telemetry sink. |
| `BroadcastEnable` | `bool` | `false` | Per-provider | Enable broadcast invalidation for this in-memory cache instance. |
| `Topic` | `string?` | `null` | Per-provider | Topic name for invalidation broadcasts; `null` = use `CacheOptions.DefaultTopic`. |
| `LocalMaxExpiration` | `TimeSpan?` | `01:00:00` | Per-provider | Cap on in-memory TTL; `null` = no cap (falls back to `DefaultExpiration`). |
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

*Code-only seams:* `Clock`, `EntryFactory`, `CacheKeyStrategy`, `TopicKeyStrategy`, `SizeProvider`, `LockKeyStrategy`.

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
| `DistributedExpiration` | `TimeSpan?` | `null` | Per-policy | L2 (Redis) entry lifetime; `null` = use provider `DefaultExpiration`. Per-call expiration arguments still take precedence. |
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

---

## Deprecated names (still bind)

These property names were renamed but still bind for backcompat. They forward to the new property. Migrate when convenient.

| Old name | New name | Defined on |
|---|---|---|
| `PrimaryMaxExpiration` | `LocalMaxExpiration` | `InMemoryRedisCacheOptions`, `InMemoryCacheOptions` |
| `PrimaryMaxExpirationDisconnected` | `LocalMaxExpirationDisconnected` | `InMemoryRedisCacheOptions`, `InMemoryCacheOptions` |
| `UsePrimaryOnlyWhenDisconnected` | `UseLocalOnlyWhenDisconnected` | `InMemoryRedisCacheOptions`, `InMemoryCacheOptions` |
