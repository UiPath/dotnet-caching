# Glossary

Alphabetical lookup for terms used across the docs.

**App-wide-only field** — A field on `RedisStreamsTopicOptions` or `RedisPubSubTopicOptions` whose per-topic override is silently ignored because the value is read at provider construction. See [how-to/broadcast.md](../how-to/broadcast.md#app-wide-only-fields).

**AppShortName** — Required-in-practice cache key prefix scoping all keys an app writes. Prevents collisions when multiple services share a Redis instance.

**Bundled GET+TTL** — A single Redis transaction that fetches a value and its remaining TTL in one round-trip, replacing the older N+1 pattern. Implemented on `ICache.GetCacheEntryAsync<T>` and `IHashCache.GetCacheEntryAsync<T>`. Routes to a replica via `CommandFlags.PreferReplica` on the transaction's `ExecuteAsync`.

**CacheKey** — A typed wrapper around `string` used everywhere a key is accepted. Implicit-converts from `string`.

**CachePolicy** — Per-cache-instance settings resolved at `ICache<T>` construction by name (defaulting to `typeof(T).FullName`). Drives L1/L2 TTLs, factory timeout, jitter, rehydration, and lock settings. See [how-to/resilience.md](../how-to/resilience.md#named-policies).

**Cooldown** — Time the distributed lock TTL keeps a key locked after a failed rehydrate attempt, preventing thundering-herd retries.

**Distributed lock** — Cross-node single-flight via `IDistributedLock`. Default impl `RedisDistributedLock` uses `LockTakeAsync`/`LockReleaseAsync` with a `SourceUri`-prefixed token. See [how-to/resilience.md](../how-to/resilience.md#stampede-protection).

**Doorbell (notify)** — Opt-in Pub/Sub channel a stream publisher fires after every `XADD` so consumers wake immediately instead of waiting for the next poll. Best-effort; the poll continues as a safety net. See [how-to/broadcast.md](../how-to/broadcast.md#notify-doorbell).

**Hash tag** — A `{...}` substring inside a Redis key that pins the key to a single shard in Redis Cluster. Used by the sharded Pub/Sub strategy so the doorbell channel and the stream key collide on the same slot.

**Hydrating cache** — Proactive background refresh of a cache entry before it expires. Configured via `CachePolicy.RehydrateEnabled` + `RehydrateOptions`. Distinct from FusionCache's fail-safe — no stale data is ever returned. See [how-to/resilience.md](../how-to/resilience.md#hydrating-cache).

**Jitter** — Uniform random delay added to L2 TTL at write time (`CachePolicy.JitterMaxDuration`) to spread cluster-wide expirations after bulk writes.

**L1 / L2** — In-memory tier (L1) and Redis tier (L2) of the multilayer cache. `InMemoryRedis` provider only.

**Local lock** — In-process single-flight per cache key via `ILocalLock`. Default impl `AsyncKeyedLocalLock`.

**Multilayer cache** — The `InMemoryRedis` provider; reads L1 → L2; writes pass through both.

**Per-topic overlay** — The `Topics[]` array under `Broadcast:RedisStreams` / `Broadcast:RedisPubSub`. Each entry's fields override the app-wide values for that topic only. See [how-to/broadcast.md](../how-to/broadcast.md#per-topic-overrides).

**Redis type prefix** — A short literal segment (`s`, `h`, `st`, `ps`) inserted between `AppShortName` and the cache key by `DefaultRedisKeyStrategyFactory`. Identifies the Redis data type the key holds (STRING / HASH / Stream / Pub-Sub channel) so caches of different types cannot collide on the same Redis key. Constants live on `UiPath.Platform.Caching.Redis.RedisTypePrefixes`. See [how-to/telemetry-and-strategies.md](../how-to/telemetry-and-strategies.md#final-key-shape-on-redis).

**Separator** — `CacheOptions.Separator`, default `:`. Joins prefix segments in keys, channel names, and stream keys.

**Single-flight** — One generator runs per key on a cache miss; other callers wait for or coalesce on its result. Implemented via `ILocalLock` (per node) and `IDistributedLock` (cross node).

**SourceUri** — `CacheOptions.SourceUri`, default `urn:<MachineName>`. Identifies the machine/pod in cross-node sync events.

**Stream maintainer** — `RedisStreamHealthMaintainer` background service that trims streams and quarantines no-consumer groups. See [how-to/broadcast.md](../how-to/broadcast.md#stream-maintainer).

**Topic** — A named broadcast channel. `TopicKey` carries the name; `ITopicFactory` resolves it to a `RedisStreamsTopic` or `RedisPubSubTopic`.

**Two surfaces** — `ICache<T>` / `IHashCache<T>` (typed, factory) vs `ICache` / `IHashCache` (dynamic-key, power-user). See [reference/interfaces.md](interfaces.md).
