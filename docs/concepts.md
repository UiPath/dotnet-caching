# Caching concepts

This page builds a mental model of the library. It explains the *what* and the
*why* of each concept so the how-to pages and reference tables are easier to
follow. If you have already worked through the quickstart, you have seen the
moving parts in action; this page explains why they are shaped the way they are.
Each section closes with a pointer to the place that carries the operational
detail.

The seven concepts covered here — providers, layers, topics, locks, policies,
telemetry, and the two API surfaces — are largely independent of one another,
but they compose. A single `GetOrAddAsync` call can touch all of them: the
provider resolves which tiers to read and write, the layers determine what is
already in memory vs. Redis, topics keep remote nodes in sync after the write,
locks prevent redundant generator runs, the policy governs TTLs and rehydration,
and telemetry records the outcome. Understanding each piece individually makes
the composed behavior predictable.

---

## Providers

A *provider* is the factory for an `ICache` / `IHashCache` pair backed by a
particular storage technology. The library ships three providers; which one a
cache instance uses is governed by `CacheOptions.DefaultCache`. The canonical
string values are collected on the static class `KnownCacheProviderNames`:
`"InMemory"`, `"Redis"`, and `"InMemoryRedis"`. Providers are registered in the
DI container by the cache builder (`AddMemory`, `AddRedis`, `AddInMemoryRedis`)
and resolved by name at runtime, so you can register multiple providers and
let individual caches opt into a non-default one by passing a provider name to
the factory.

`InMemoryRedis` is the default and the right choice for most services. It
combines an in-process memory tier (L1) with a shared Redis tier (L2), and it
wires up cross-node L1 invalidation automatically via topics. When a service
runs multiple replicas, every node's in-memory snapshot stays consistent without
any application-level plumbing. The higher-level concepts of L1/L2 tiers and
topics are described in their own sections below; what matters here is that
`InMemoryRedis` is the composition that ties them together.

`Redis` is the provider to choose when you want every read and write to go
straight to Redis and never be fronted by an in-process copy. The typical reason
is footprint: if cached values are large or numerous enough that duplicating them
in every process's heap is prohibitive, `Redis` keeps memory consumption
bounded. It still benefits from the library's policy system, resilience
pipelines, and telemetry; only the in-process memory tier is absent. Because
there is no L1, cross-node invalidation via topics is also unnecessary for a
`Redis`-only deployment, though the broadcast infrastructure remains available
if you want it for other purposes.

Every Redis key the lib writes carries a short *Redis type prefix* between
`AppShortName` and the logical key — `s` for `ICache` STRING values, `h` for
`IHashCache` HASH values, `st` for Stream keys, `ps` for Pub/Sub channels (the
constants live on `RedisTypePrefixes`). The prefix prevents type-collision
errors when the same logical key is used by caches of different types: an
`ICache<User>` write at `user:42` becomes `my-service:s:user:42` on Redis while
an `IHashCache<UserField>` write at the same logical key becomes
`my-service:h:user:42`, so the two never compete for the same Redis key and no
`WRONGTYPE` error can occur. See
[how-to/telemetry-and-strategies.md](how-to/telemetry-and-strategies.md#final-key-shape-on-redis)
for the full key-shape breakdown.

`InMemory` is for single-process scenarios — CLI tools, integration tests, or a
service running in local development where a Redis instance is not available. No
Redis connection is required. Under the hood it uses `MultilayerCache` backed by
`NullCache` as the inner tier, so architecturally it is the same engine as
`InMemoryRedis` with Redis replaced by a no-op. The provider can participate in
the broadcast system if you both set `BroadcastEnable: true` on `InMemoryCacheOptions`
and call `AddBroadcast()` explicitly in the builder chain — unlike `AddInMemoryRedis()`,
`AddMemory()` does not wire `AddBroadcast()` for you. With both opted in, cross-node
invalidation logic can be tested without Redis by wiring an alternative topic transport.

A real pattern seen in consumer code is to select the provider at startup based
on configuration rather than hard-coding one. The service reads whether Redis is
enabled and supplies either `KnownCacheProviderNames.InMemoryRedis` or
`KnownCacheProviderNames.InMemory` as `CacheOptions.DefaultCache`. The cache
consumer code is identical either way — `GetOrAddAsync` works the same on any
provider — so the same binaries run in local development without Redis and in
production with it. The recipe page linked below shows the exact wiring,
including how to avoid leaking a Redis dependency into tests that do not need it.

When the three shipped providers don't fit — for example, you need a Memcached backend or an in-memory fake with deterministic eviction for integration tests — implement `ICacheProvider` and register it on the `ICacheFactory`. The full contract and a worked example live in [how-to/extending.md](how-to/extending.md#custom-cache-provider). The same path applies to broadcast: implement `ITopicProvider` for a non-Redis transport (Kafka, NATS, an in-memory test transport).

See also: [recipes/provider-fallback.md](recipes/provider-fallback.md), [how-to/resilience.md](how-to/resilience.md), [how-to/extending.md](how-to/extending.md).

---

## Layers (L1 / L2)

The `InMemoryRedis` provider (and, structurally, `InMemory` as well) is built on
`MultilayerCache`. That class wraps an inner `ICache` as the L2 tier and places
an in-process memory cache in front of it as L1.

A read from `GetAsync` or `GetOrAddAsync` always checks L1 first. If the key is
present in L1, the call returns immediately — no network round-trip. On an L1
miss the call fetches from L2 (Redis), writes the result into L1, and returns it
to the caller. If L2 also misses, the user-supplied generator runs, the result
is written to both tiers, and the call returns. The split is invisible to the
caller: `cache.GetAsync(key)` works the same whether the cache is `InMemoryRedis`,
`Redis`, or `InMemory`.

Three options on `InMemoryRedisCacheOptions` shape how the two tiers interact.

`LocalMaxExpiration` caps the L1 TTL. When a value is written, the L1 entry's
effective lifetime is `min(entry.Expiration, LocalMaxExpiration)`. Keeping L1
entries short-lived (for example, capping at two minutes while L2 entries live
for an hour) limits how stale an in-process snapshot can become after a write
arrives from another node. In configuration:

```json
{
  "Caching": {
    "InMemoryRedis": {
      "LocalMaxExpiration": "00:02:00"
    }
  }
}
```

`LocalMaxExpirationDisconnected` is a tighter cap applied when the connection
monitor reports that L2 is unhealthy. The default is 30 seconds. When the Redis
connection is degraded you still want L1 to serve reads, but you want entries to
expire relatively quickly so that as soon as connectivity is restored the process
re-populates from a fresh L2 rather than serving stale data for the full
`LocalMaxExpiration` window.

`UseLocalOnlyWhenDisconnected` changes the disconnected behavior further: when
true, an unhealthy L2 means reads are served from L1 only and no L2 round-trip
is attempted. This is appropriate when you would rather serve potentially stale
data than accumulate timeout latency on every cache miss during a Redis outage.

Why split into two tiers at all? L1 eliminates Redis latency and bandwidth on
the hot path — for a busy service, removing the network hop from cache reads can
have a measurable throughput impact. L2 makes the cache survive a process
restart and lets multiple processes share a warm dataset: a freshly restarted
replica does not start cold if other replicas have already populated Redis. The
tradeoff is that each node's L1 can drift from L2 when another node writes a new
value. Topics solve that problem, and their relationship to layers is explained
in the next section.

It is worth noting that all three of these disconnected-scenario options
(`LocalMaxExpiration`, `LocalMaxExpirationDisconnected`,
`UseLocalOnlyWhenDisconnected`) require the connection monitor to be active —
either by setting `ConnectionMonitorEnabled` to `true` on `CacheOptions`, or by
setting it on `InMemoryRedisCacheOptions` directly. Without the monitor the cache
cannot distinguish "connected" from "disconnected" and the disconnected behavior
never triggers.

See also: [how-to/resilience.md](how-to/resilience.md), [reference/settings.md#cachinginmemoryredis-inmemoryrediscacheoptions](reference/settings.md#cachinginmemoryredis-inmemoryrediscacheoptions).

---

## Topics & cross-node sync

A *topic* is a pub/sub channel over which events flow between nodes. The most
visible use of topics in this library is cache invalidation: when node A writes
or removes a value, it publishes an invalidation event on the topic; every other
node listening on that topic removes the corresponding key from its L1. This is
how the `InMemoryRedis` provider keeps per-process snapshots consistent across a
deployment without requiring every process to call Redis on every read.

Without topics, a two-tier design would create a correctness hazard: a write on
one node would update L2 but leave every other node's L1 holding the old value
until it naturally expired. If `LocalMaxExpiration` were set to ten minutes,
every node except the writing one would serve stale data for up to ten minutes.
Topics collapse that window to one polling interval (250 ms for `RedisStreams` by
default) plus network RTT.

The library ships two topic transports, named in `KnownTopicNames`.

`RedisStreams` (`"RedisStreams"`) is the default. It uses Redis Streams — XADD
for publishing and XREADGROUP for consuming — with a consumer group per process.
Delivery is at-least-once and durable: events are retained in the stream and can
be replayed if a consumer falls behind or restarts. The consumer polls the stream
at `PollInterval` (default 250 ms), so worst-case invalidation latency under
normal conditions is approximately one polling interval. To reduce
publish-to-deliver latency further, `RedisStreamsTopic` supports an optional
Pub/Sub notify channel that acts as a doorbell: a cheap PUBLISH message wakes
the poll loop immediately so that delivery approaches a single Redis RTT in the
common case while stream durability is preserved for lagging or restarted
consumers. You get the low latency of Pub/Sub and the reliability of Streams.

`RedisPubSub` (`"RedisPubSub"`) uses Redis Pub/Sub directly. It is simpler and
has lower steady-state overhead, but delivery is fire-and-forget: messages
published while a subscriber is disconnected or lagging are lost. It is a
reasonable choice for caches where occasional stale L1 entries are acceptable
and you want to minimize Redis CPU.

`ITopicFactory` resolves a `TopicKey` to a topic implementation. Topics are
typed by event payload, making them usable for any event structure, not just
cache invalidation. The multilayer cache uses one topic per provider for L1
invalidation events; the factory lets different providers or custom components
share or isolate their topic channels. `DefaultTopic` on `CacheOptions` sets the
transport name (`"RedisStreams"` by default) applied when no topic is specified
for a particular cache.

Cross-node sync is not free. Every cache write fans out an invalidation event to
every node listening on the topic. In a large cluster with a high write rate,
topic traffic can become non-trivial. The per-topic options — polling interval,
consumer capacity, stream trimming — give you control over that fan-out at the
topic level without touching the cache itself. Topics can also be used
independently of the multilayer cache if you need a lightweight event bus for
other coordination purposes within a service.

See also: [how-to/broadcast.md](how-to/broadcast.md), [reference/settings.md#cachingbroadcastredisstreams-redisstreamstopicoptions](reference/settings.md#cachingbroadcastredisstreams-redisstreamstopicoptions).

---

## Locks

`MultilayerCache.GetOrAddAsync` can deduplicate concurrent generator calls using
two cooperating lock abstractions.

`ILocalLock` is an in-process single-flight keyed on the cache key. The default
implementation is `AsyncKeyedLocalLock`, which wraps `AsyncKeyedLocker` with a
pre-allocated semaphore pool (size and initial fill controlled by
`CacheOptions.LocalLockPoolSize` and `LocalLockPoolInitialFill`). When N
concurrent callers on the same node all miss the cache for the same key, the
local lock ensures only one of them runs the generator; the rest wait and then
receive the result that the winner fetched. The local lock is cheap — it never
leaves the process — and it is on by default for `InMemoryRedis` and `InMemory`
providers (`LocalLockEnabled` defaults to `true` on `InMemoryRedisCacheOptions`).

`IDistributedLock` extends single-flight across nodes using Redis `SET NX`. The
default implementation is `RedisDistributedLock`. When a node acquires the
distributed lock, every other node that loses the acquire will wait with
exponential backoff — initial interval 50 ms (`CacheOptions.DistributedLockPollInterval`),
capped at 500 ms (`DistributedLockMaxPollInterval`), with ±20% jitter at each
step to de-synchronize competing waiters — and then re-check the cache rather
than running their own generator. This prevents the same costly work from
happening on every replica simultaneously after a cluster-wide cache miss. The
distributed lock is off by default (`DistributedLockEnabled` is null / false on
the options); `AddInMemoryRedis()` registers the implementation automatically,
so the typical opt-in is just setting `DistributedLockEnabled: true` on the
provider options or in a `CachePolicy.Lock` block. `AddMemory()` does **not**
register `RedisDistributedLock` (the in-memory provider passes
`NullDistributedLock` to `MultilayerCache`), so memory-only deployments and any
custom wiring that skips `AddInMemoryRedis()` should add
`.AddRedisDistributedLock()` explicitly on the builder.

When both locks are enabled, `GetOrAddAsync` takes the local lock first (cheap,
in-process) and then — under a double-checked read to avoid redundant generator
runs after waiting — attempts the distributed lock. A failure to acquire the
distributed lock (because it is contended or Redis is unavailable) degrades
gracefully: the call falls back to per-node single-flight only. Callers are
never stalled indefinitely by lock failures. The degradation is intentional:
the goal of the distributed lock is to reduce redundant work, not to guarantee
exactly-once generator execution.

`NullDistributedLock` is the default no-op shim when the distributed lock is not
registered. Single-node deployments and local-development configurations are
perfectly fine with it: the local lock still coalesces concurrent callers within
the same process. For multi-node deployments, wiring `RedisDistributedLock` is
advisable when the generator is expensive and simultaneous cluster-wide cold
misses would be problematic.

Per-cache-instance tuning is available via the `Lock` member on `CachePolicy`
(of type `LockProfile?`). `LockProfile` mirrors the lock fields on the cache-wide
options (`LocalLockEnabled`, `DistributedLockEnabled`, `LocalLockTimeout`,
`DistributedLockTimeout`, `DistributedLockExpiry`) and is merged field-by-field
against the default policy, so a named policy can override one field while
inheriting the rest. This is one of the two places in `CachePolicy` where
field-level merging applies; `RehydrateOptions`, by contrast, is replaced
wholesale when a named policy sets it (see the Policies section below).

See also: [how-to/resilience.md#stampede-protection](how-to/resilience.md#stampede-protection), [recipes/avoid-raw-iredisconnector.md](recipes/avoid-raw-iredisconnector.md).

---

## Policies

`CachePolicy` is the per-cache-instance configuration record. Every
`ICache<T>` / `IHashCache<T>` binds a policy by name at construction; the
default name is `typeof(T).FullName`. The policy drives TTLs, factory timeout,
jitter, rehydration, and lock settings for that specific cache. Policies are
declared in configuration under `Caching.Policies` and bound by the DI
container at startup, so you can add or change a policy without recompiling.

The resolution chain for any `null` field is: named policy (`CacheOptions.Policies[name]`,
pre-merged with `CacheOptions.DefaultCachePolicy` at factory construction)
→ the cache instance's effective default (provider options merged with
`CacheOptions.DefaultCachePolicy`; for lock fields, MultilayerCache also applies hardcoded
fallbacks like `5s` distributed-lock expiry and `500ms` timeouts). A field set to `null`
always means "inherit from the next level down." To explicitly disable a feature, set the
relevant boolean to `false`, not `null`.
The distinction matters: if `DefaultCachePolicy` enables rehydration and a named
policy sets `RehydrateEnabled = null`, rehydration remains on because `null`
inherits. Setting `RehydrateEnabled = false` turns it off for that cache only.

TTLs are layered across policy and per-call arguments. `LocalExpiration` is a
per-policy L1 cap analogous to `LocalMaxExpiration` on the provider options but
applied narrowly to one cache instance. `LocalExpirationDisconnected` does the
same for the disconnected scenario. `DistributedExpiration` is the L2 lifetime
used when the caller of `GetOrAddAsync` or `SetAsync` does not supply an explicit
`expiration:` argument. A caller-supplied expiration always wins over the policy
value; use per-call overrides for one-off needs without declaring a dedicated
policy.

`FactoryTimeout` is the maximum time the generator is allowed to run when called
from `GetOrAddAsync`. If the generator exceeds this duration, the call is
cancelled and an exception propagates to the caller. When `FactoryTimeout` is
null the generator runs without a deadline. Setting an explicit timeout is
advisable for generators that call external services, so that a slow upstream
cannot hold a cache lock indefinitely.

`JitterMaxDuration` adds a random offset drawn from `[0, JitterMaxDuration)` to
the resolved L2 expiration at write time. This spreads cluster-wide expirations
after a bulk warm-up — for example, after a deploy where every node fetches and
caches the same data simultaneously. Without jitter, every entry written during
warm-up has the same absolute expiry and all of them fall off Redis at once,
producing a coordinated miss storm. Jitter applies only when the caller does not
supply an explicit expiration. Setting `JitterMaxDuration` to a value smaller
than `DistributedExpiration` gives a modest spread while keeping TTLs
predictable; a value larger than the base lifetime produces highly skewed TTLs
and should be avoided.

The hydrating-cache feature is the headline policy capability. When
`RehydrateEnabled` is `true` and `Rehydrate` carries a `RehydrateOptions`
object, the cache performs proactive background refresh of entries before they
expire. The `Threshold` fraction (default `0.75`) defines the soft-TTL trigger:
once an entry has lived through 75% of its lifetime, the next read schedules a
background generator run. Foreground callers continue to receive the current
(still valid) value while the refresh happens off the hot path — there is no
blocking wait. If the background refresh fails, an exponential backoff governed
by `BaseCooldown` (default 5 s) and `MaxCooldown` (default 5 min) prevents
repeated failed refreshes from stampeding the upstream. Cooldown enforcement
uses the distributed lock's TTL so that backoff is consistent across nodes
without any additional shared state.

`RehydrateOptions` is replaced wholesale when a named policy declares it —
there is no per-field merge. If your named policy needs to change only the
`Threshold` while keeping the cooldown defaults, it must redeclare all
`RehydrateOptions` fields explicitly.

When a service wants to tune just the lock behavior for a specific cache without
changing TTLs or rehydration, it adds a named policy with only the `Lock` field
populated. `LockProfile` fields are merged individually against the default
policy, so specifying `DistributedLockEnabled = true` in a named policy overrides
only that field while the timeouts come from the default policy or the
provider options.

A useful mental model for the entire policy system is that it works like CSS
specificity: the most specific setting (per-call argument) wins, then the named
policy, then the cache instance's effective default. The effective default is itself
a per-field merge — provider-specific options (`IMultilayerCacheOptions.LocalMaxExpiration`,
`DefaultExpiration`, lock fields) win field-by-field over `CacheOptions.DefaultCachePolicy`,
which fills any field the provider didn't set; for lock fields MultilayerCache additionally
supplies hardcoded fallbacks so every cache instance has a fully resolved lock by the time it
starts serving requests. Most services need no policy configuration at all when the
provider defaults are acceptable. Named policies become valuable when different
caches serving different data categories have meaningfully different freshness
requirements or cost profiles.

See also: [how-to/resilience.md#named-policies](how-to/resilience.md#named-policies), [reference/settings.md#policiesname-cachepolicy](reference/settings.md#policiesname-cachepolicy).

---

## Telemetry

`ICachingTelemetryProvider` is the instrumentation seam. The library never takes
a hard dependency on a particular telemetry SDK; instead it calls the four
methods on this interface and lets the host decide how to route the signal. This
design means the caching library is useful in any service regardless of which
observability infrastructure the service uses — OpenTelemetry,
a proprietary internal system, or a test double that records calls for assertions.

Cache hit/miss outcomes are tracked through `ITelemetryOperation.Track(hit: bool)`
and surface as metrics named `Caching.Stats.Hits.<provider>.<method>.<type>` /
`Caching.Stats.Misses.<provider>.<method>.<type>` (the elapsed time of the op is
the metric value). `TrackEvent` fires on other signals — rehydration outcomes,
distributed-lock acquire/release/cooldown, Redis connection-monitor state changes,
and stream receipt events. `TrackMetric` emits other counters and timings —
statistics flushed from the in-memory cache at the configured
`StatisticsFlushInterval`, profiler-derived counters, etc. `TrackDependency`
records Redis dependency events with start time and duration, following the
standard OpenTelemetry dependency/span model. `TrackException` captures failures that
occur inside resilience pipelines, such as a generator timeout or a Redis
command failure during a background
refresh.

All four methods accept tag-bag parameters as `ReadOnlySpan<KeyValuePair<string, string>>`
(and `ReadOnlySpan<KeyValuePair<string, double>>` for metric dimensions). Using
`ReadOnlySpan` keeps the hot path allocation-free: when telemetry is disabled or
when a no-op provider is registered, passing an empty span costs nothing and no
dictionary is heap-allocated on each call. The default implementations on the
interface are no-ops, so a partial custom provider only needs to override the
methods it cares about.

The default integration path is OpenTelemetry. Calling `.AddOpenTelemetry()` on
the cache builder registers `UiPath.Caching.OpenTelemetry.CachingTelemetryProvider`,
an `ICachingTelemetryProvider` backed by a `System.Diagnostics.ActivitySource`
and a `Meter` both named `UiPath.Caching`. You collect those signals by adding
the source and meter to your OTel providers (`AddSource("UiPath.Caching")` /
`AddMeter("UiPath.Caching")`). Raw Redis **command** spans are a separate,
optional layer: supply a custom `IConnectionMultiplexerFactory` that calls
`StackExchangeRedisInstrumentation.AddConnection(...)` on each new connection.
Cache-semantic signals (`TrackEvent`, `TrackMetric`, `TrackException`, and the
hit/miss counters) flow through the adapter regardless of whether you also wire
up the Redis-command instrumentation.

Registering a custom `ICachingTelemetryProvider` implementation directly is also
first-class: if your service has its own telemetry surface, implement
the interface and register it in the DI container instead of calling
`.AddOpenTelemetry()`. The interface is small — four methods with default no-op
bodies — so custom implementations are straightforward to write and easy to test.

See also: [how-to/telemetry-and-strategies.md](how-to/telemetry-and-strategies.md).

---

## Two surfaces

> ### Typical vs. power-user surface
>
> Most consumers use **`ICache<T>` / `IHashCache<T>`** via `ICacheFactory`
> extension methods. The typed surface gives you compile-time safety, a single
> `ICacheKeyStrategy` per cache, and `CachePolicy` resolution by
> `typeof(T).FullName`.
>
> **`ICache` / `IHashCache`** are the dynamic-key power-user surface. Reach for
> them when keys or value types vary per call. They are not strictly more
> powerful than the typed surface — they are different shapes for a different
> problem.

The typed surface is what most consumer code in the UiPath codebase looks like.
A service declares extension methods on `ICacheFactory` returning
`ICache<UserDto>` or `IHashCache<OrgPermissions>`, each with a pre-built key
strategy, and callers invoke `factory.Users()` or `factory.OrgPermissions()`
without knowing anything about keys, policies, or provider names. The
`CachePolicy` is resolved automatically by `typeof(T).FullName`, so tuning a
specific cache's TTL or enabling rehydration is a matter of adding a named entry
to `CacheOptions.Policies` in configuration — no code change required. The
extension-method pattern also makes the caches easy to discover: all of a
service's typed caches are visible as methods on `ICacheFactory`, and each one
carries only the key strategy and policy relevant to its value type.

`IHashCache<T>` is the variant for hash-structured values — instead of a single
key mapping to a single value, a hash key maps to a dictionary of field/value
pairs. It is appropriate for data that is naturally structured as a group of
related fields (for example, per-user settings stored as a map) where you want
to fetch or evict individual fields without invalidating the whole entry. The
typed hash cache works the same policy-resolution way as `ICache<T>`.

The non-generic `ICache` and `IHashCache` surfaces appear in framework-level
glue where the key or value type is not known until call time. A MediatR
pipeline behavior that caches arbitrary query responses is the canonical example:
the behavior receives a dynamic request type, builds a key from the request's
properties, and calls `GetOrAddAsync` on a bare `ICache`. In that scenario,
using `ICache<T>` would require a separate cache instance per query type, which
is impractical. The non-generic surface is the right tool for that problem — not
a shortcut around it.

One thing both surfaces share is that the cache instance itself is lightweight
and cheap to keep alive. There is no connection pooling or heavy initialization
behind `CreateCache()`; the underlying Redis connection is managed by the
provider. It is fine to inject `ICacheFactory` into a singleton, and it is fine
to call `factory.Users()` on every request — the result is the same singleton
cache instance each time.

See also: [reference/interfaces.md](reference/interfaces.md), [recipes/factory-extension-methods.md](recipes/factory-extension-methods.md), [recipes/mediatr-pipeline-behavior.md](recipes/mediatr-pipeline-behavior.md).
