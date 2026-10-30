# Glossary

Alphabetical lookup for terms used across the docs.

**App-wide-only field** — A field on `RedisStreamsTopicOptions` or `RedisPubSubTopicOptions` whose per-topic override is silently ignored because the value is read at provider construction. See [how-to/broadcast.md](../how-to/broadcast.md#app-wide-only-fields).

**AppShortName** — Required-in-practice cache key prefix scoping all keys an app writes. Prevents collisions when multiple services share a Redis instance.

**Background distributed write** — `BackgroundDistributedWrite`: a single-key set returns once the value is in the local tier, and the distributed write follows detached. Writes to one key keep their order. See [concepts.md](../concepts.md#background-distributed-writes).

**Bundled GET+TTL** — A single Redis transaction that fetches a value and its remaining TTL in one round-trip, replacing the older N+1 pattern. Implemented on `ICache.GetCacheEntryAsync<T>` and `IHashCache.GetCacheEntryAsync<T>`. Routes by `ReadFrom` (the master by default, a replica with `Replica`) through the flag on the transaction's `ExecuteAsync`.

**CacheKey** — A typed wrapper around `string` used everywhere a key is accepted. Implicit-converts from `string`.

**CachePolicy** — Per-cache-instance settings resolved at `ICache<T>` construction by name (defaulting to `typeof(T).FullName`). Drives local and distributed TTLs, the generator timeout (`FactoryTimeout`), jitter, rehydration, and lock settings. Not the resilience pipeline, which is the Polly settings for Redis calls. See [how-to/resilience.md](../how-to/resilience.md#named-policies).

**Change token** — The per-entry mechanism that drops one local entry when an invalidation arrives for its key, or when events were missed.

**Cooldown** — Time the distributed lock TTL keeps a key locked after a failed rehydrate attempt, preventing thundering-herd retries.

**Disconnected mode** — The state while the connection monitor reports the distributed tier unhealthy. The cache serves from the local tier under the tighter cap `LocalMaxExpirationDisconnected`; `UseLocalOnlyWhenDisconnected` decides whether writes stay local too.

**Distributed lock** — Cross-node single-flight via `IDistributedLock`. Default impl `RedisDistributedLock` uses `LockTakeAsync`/`LockReleaseAsync` with a `SourceUri`-prefixed token. See [how-to/resilience.md](../how-to/resilience.md#stampede-protection).

**Doorbell (notify)** — Pub/Sub channel a stream publisher fires after every `XADD` (on by default) so the other nodes wake immediately instead of waiting for the next poll. Best-effort; the poll continues as a safety net. See [how-to/broadcast.md](../how-to/broadcast.md#notify-doorbell).

**Event** — A message published on a topic, such as a set, a removal or a refresh of a key. Its effect on a receiving node is an [invalidation](#invalidation).

**Generated expiration** — A lifetime the generator picks for the value it returns, instead of the policy. `GetOrAddWithExpirationAsync` takes a `GeneratedValue<T>`; a null expiration leaves it to the policy.

**Generator** — The caller's function that makes a value on a miss. The `FactoryTimeout` option and the `cache.factory.timed_out` event say "factory"; the docs say generator.

**Hash field** — One named value inside a hash cache entry. All fields share the entry's one TTL. Not a setting. Names of the form `_word_`, such as `_metadata_` and `_version_`, are reserved.

**Hash tag** — A `{...}` substring inside a Redis key that pins the key to a single shard in Redis Cluster. Used by the sharded Pub/Sub strategy so the doorbell channel and the stream key collide on the same slot.

**Invalidation** — What an [event](#event) does on the receiving node: the local entry for the key is dropped, and the next read goes to the distributed tier. A peer's set of a key held locally and read within its local expiration is instead refreshed in the background (`RefreshLocalOnPeerWrite`); one not read in that time is dropped.

**Jitter** — Uniform random delay added to the distributed TTL at write time (`CachePolicy.JitterMaxDuration`) to spread cluster-wide expirations after bulk writes.

**Local tier / distributed tier** — The in-process memory tier and the shared tier behind it. L1 and L2 are shorthand for them. `InMemoryRedis` has both; `InMemory` is the same cache with the local tier only; the set cache uses the same names. Say Redis provider, not "Redis tier", when the `Redis` provider is meant.

**Loss / subscription gap / missed events** — Missed events is the umbrella: a node may not have seen some events, so it clears its local tier. A loss is known: events were removed before the node read them (`MissedEventsReason.Lost`, for example entries trimmed from the stream). A subscription gap is a possible miss: the subscription was not in place for a while (`MissedEventsReason.SubscriptionGap`).

**Multilayer cache** — A cache with a local tier in front of a distributed one. `InMemoryRedis` is the one with both; reads go local first, writes pass through both.

**Peer field update** — A write that tells the other nodes which hash fields it wrote, so a node whose local copy is exactly one write behind patches the copy instead of dropping it. Opt in with `PeerFieldUpdates`. See [version / epoch](#version--epoch).

**Per-topic overlay** — The `Topics[]` array under `Broadcast:RedisStreams` / `Broadcast:RedisPubSub`. Each entry's fields override the app-wide values for that topic only. See [how-to/broadcast.md](../how-to/broadcast.md#per-topic-overrides).

**Planned maintenance** — A provider-announced window in which the Redis server is about to restart or fail over (Azure Redis `NodeMaintenanceStarting`). The library probes and reconnects, and the health check stays green. Switched by `PlannedMaintenanceEnabled`; `IRedisPlannedMaintenance` reports it.

**Primary / replica** — The write node of Redis and its read copies. The docs say primary and replica; shipped names keep `Master` (`RedisReadFrom.Master`, `MasterReadWindow`). Reads go to the primary unless `ReadFrom` is `Replica`.

**Redis keyspace** — A short literal segment inserted between `AppShortName` and the rest of the key. `DefaultRedisKeyStrategyFactory` inserts `s` and `h` for the caches; the broadcast paths insert `st` and `ps` through their own `PrefixStrategy`. Packages layered on the library reserve their own through `ReserveRedisKeyspace`. Identifies the Redis data type the key holds (STRING / HASH / Stream / Pub-Sub channel) so caches of different types cannot collide on the same Redis key. Constants live on `UiPath.Caching.Redis.RedisKeyspaces`. See [how-to/telemetry-and-strategies.md](../how-to/telemetry-and-strategies.md#final-key-shape-on-redis).

**Rehydration** — Proactive background refresh of an entry before it expires, by `CachePolicy.RehydrateEnabled` + `RehydrateOptions`. It never returns stale data. The resilience guide's section is titled "Hydrating cache". See [how-to/resilience.md](../how-to/resilience.md#hydrating-cache).

**Separator** — `CacheOptions.Separator`, default `:`. Joins prefix segments in keys, channel names, and stream keys.

**Set cache** — One key holding a set of members, reached through `ISetCache` / `ISetCache<T>`. The `UiPath.Caching.Queue` package and `IQueueCacheFactory` call it a queue cache. See [concepts.md](../concepts.md#set-caches).

**Single-flight** — In-process: one generator runs per key on a cache miss and the other callers on the node join it. Switched by `Lock.LocalSingleFlightEnabled`. Across nodes the same job is done by the [distributed lock](#distributed-lock). "Shared run" and "local lock" are not used.

**SourceUri** — `CacheOptions.SourceUri`, default `urn:<MachineName>`. Identifies the machine/pod in cross-node sync events.

**State / argument** — State is the `TState` a batch `GetOrAddAsync` pairs with each key, handed back to the generator for the keys that missed. An argument is one value for the whole call (`TArgument`). Neither is cache state or a setting.

**Stream maintainer** — `RedisStreamHealthMaintainer` background service that trims streams and quarantines no-consumer groups. See [how-to/broadcast.md](../how-to/broadcast.md#stream-maintainer).

**Topic** — The abstraction events are published to and read from, by name. `TopicKey` carries the name; `ITopicFactory` resolves it to a `RedisStreamsTopic` or `RedisPubSubTopic`.

**Transport** — What carries a topic's events between nodes: Redis Streams (the default) or Redis Pub/Sub, named in `KnownTopicNames`.

**Two surfaces** — `ICache<T>` / `IHashCache<T>` (typed, factory) vs `ICache` / `IHashCache` (dynamic-key, power-user). See [reference/interfaces.md](interfaces.md).

**Version / epoch** — A hash's version counts its writes, kept in the reserved `_version_` field. The high bits are the epoch, drawn when a write makes the hash; a hash made again after a removal, expiry or eviction has a new epoch.
