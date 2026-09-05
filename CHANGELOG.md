# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/)

## [Unreleased]

### Added

- **`IDistributedCache` adapter.** `builder.AddDistributedCache(providerName)` registers a
  `Microsoft.Extensions.Caching.Distributed.IDistributedCache` backed by this library's pipeline —
  shared Redis connection, resilience, telemetry — so consumers that require it (ASP.NET Core session
  state, OpenIddict, DataProtection, rate limiters, and `HybridCache` as an L2) stop running a second,
  parallel Redis stack. The backing tier is a required argument
  (`Redis` recommended, `InMemoryRedis`, or `InMemory`); full absolute + sliding expiration and
  `Refresh` semantics are supported. Keys are **always case-sensitive**, independent of
  `CacheOptions.KeyCasing`, because `IDistributedCache` keys are opaque and case-significant.
  Entries are stored as a Redis hash (`data`, `absexp`, `sldexp` — the conventional layout for this
  contract) in a keyspace disjoint from the application's own caches, so `Refresh` reads
  only the expiration metadata rather than transferring the payload. Keyspace separation is
  configurable at both levels: `CacheKeyStrategy` prefixes the `CacheKey` itself (`d:` by default) so a
  distributed entry cannot be reached through the application's own `ICache`/`IHashCache` with the bare
  caller key, and `RedisKeyDifferentiator` plus `RedisKeyStrategyFactory` separate the physical Redis
  keyspace (`dh` by default, inheriting the application's factory). A differentiator matching one of the
  application's own type prefixes is rejected at registration, and because a differentiator only
  separates anything if the factory honors it, registration also proves the composed key differs from
  what the application's caches would produce. Configurable via
  `UiPathDistributedCacheOptions`: `PolicyName`, `DefaultEntryExpiration`, and
  `AllowUnboundedEntries`. Writes with no caller expiration take a bounded default rather than living
  forever in shared storage; an unset default resolves to `CachePolicy.DefaultDistributedExpiration`,
  and only `AllowUnboundedEntries` reaches "until removed".
- **`CacheOptions.KeyCasing`** selects how keys built without an explicit mode are normalized —
  `Insensitive` (trim + lowercase, the historical behavior and the default) or `Sensitive` (preserve
  the caller's casing). `CacheKey` gained a `(string?, CacheKeyCasing)` constructor, a `Casing`
  property, and `WithName` so key transformations preserve the mode. Changing the app-wide setting
  relocates every cache key, so existing entries are rewritten under the new spelling.
- **`CacheKeyComparer`** exposes cached `Sensitive` / `Insensitive` equality comparers over
  `CacheKey`, in the `StringComparer` shape.
- **`ISerializerProxy<byte[]>`** is now the library's only serialization seam, replacing
  `ISerializerProxy<RedisValue>` everywhere — see **Removed** below. The serialization contract no
  longer names a Redis type, so a custom serializer needs no dependency on StackExchange.Redis, and
  one registration covers `ICache`, `IHashCache` and `ISetCache`. Swapping in a binary serializer
  such as MessagePack is one class and one registration — see
  [how-to/extending.md](docs/how-to/extending.md). Two implementations ship:
  - `SystemJsonByteSerializerProxy` (the default) is UTF-8 JSON for every value, which is
    byte-for-byte the format `SystemJsonSerializerProxy` wrote — **no cache entry needs migrating**,
    byte payloads and nulls included.
  - `RawByteSerializerProxy` stores byte payloads verbatim — no base64, no JSON, no encoding layer —
    and JSON for every other type. `AddDistributedCache` constructs this for the provider it builds
    rather than resolving one from DI, because an `IDistributedCache` payload is caller bytes the
    caller has already serialized and re-encoding them would be both wasteful and wrong; that also
    means replacing the app-wide serializer does not change what the adapter stores. Registering it
    app-wide is available as an opt-in, and *is* a wire-format change: it returns stored bytes as-is
    rather than base64-decoding them, so relocate the keyspace (a version segment in `AppShortName`
    or the cache-key strategy) when switching an existing deployment over.
- **`RedisCacheOptions.AwaitRefresh`** (default `false`) waits for the server to apply a refresh instead of
  sending `KEYEXPIRE`/`PERSIST` fire-and-forget. Off by default, which keeps the round trip off the
  sliding-expiration path — it runs on every read of a sliding entry — and preserves existing behavior.
  `AddDistributedCache` enables it on the private provider it builds, because a silently dropped refresh
  shortens a session and fire-and-forget cannot report one. Honored by both `RedisCache` and
  `RedisHashCache`.
- **Conditional add: `ICache.TryAddAsync` / `ICache<T>.TryAddAsync` (+ blocking `ICache<T>.TryAdd`).**
  Writes a key only if it does not already exist, returning `true` only to the caller that created it.
  On Redis-backed caches this is StackExchange.Redis `When.NotExists` — `SET key value EX … NX` — a
  single atomic round-trip with the TTL applied by the same command, so exactly one caller across all
  nodes wins a given key and a won key is never briefly immortal. Intended for at-most-once semantics
  keyed by something: idempotency keys, dedup markers whose TTL is the dedup window, electing which
  replica runs a job. `false` deliberately conflates "the key already existed" with "the write could
  not be completed" (disconnected, threw, or a `null`/`default` value the cache cannot represent) —
  fail-closed, so a caller treating `true` as "I own this key" is never wrongly told it won; the same
  conflation `IDistributedLock.TryAcquireAsync` already documents. Unlike `SetAsync`, it never
  deletes: where `SetAsync` removes the key when handed a `null` with `CacheNullValues` off,
  `TryAddAsync` reports `false` and leaves the key untouched. Per tier: `RedisCache` issues the `NX`
  write; `MultilayerCache` runs one sequence for every provider — take the local lock, probe the
  local tier, ask the L2, populate L1 (plus an invalidation broadcast) on a win, best-effort. A
  broadcast or L1 failure never downgrades a real win to `false`, since that would strand the entry
  with no owner; with the L2 disconnected the call returns `false` rather than granting a local-only
  claim every node would also be granted (`SetAsync` degrades to a local write there). The probe is
  what bounds exclusion at one winner per process, and the only thing doing so on the `InMemory`
  provider, whose `NullCache` L2 tells every caller it added the key; it also reports a local hit as
  a loss without asking the L2, which is fail-closed but costs a win the L2 would have granted when
  the local copy outlived the shared one. The local lock is what makes that probe-then-write atomic,
  so a conditional add takes it regardless of `Lock.LocalLockEnabled` (which trades single-flight for
  throughput on `GetOrAddAsync` and must not be able to hand one key to two callers), and a caller
  that cannot acquire it within `Lock.LocalLockTimeout` is told it lost rather than proceeding
  unserialized. The L2 itself is only ever asked, never classified by type or provider name, so a
  provider a consumer registers participates on the same terms — with the corollary that an L2
  arbitrating in-process only is trusted as though it arbitrated for every sharer. On the `InMemory`
  provider no invalidation broadcast is published: `ChangeTokenFactory` accepts only `CacheRemoved`
  and `CacheRefreshed` there, so peers ignore `CacheSet` — deliberately, since each node's memory is
  the store rather than a copy of a shared one.
  An expiration that is not in the future reports `false` on every tier: the entry would be evicted on
  arrival, so a `true` would be handed to every later caller as well. `NullCache` returns `false` —
  the one member where it does not degrade to "caching is off, carry on", because it cannot complete
  the write (which is exactly what a fail-closed `false` means) and because `true` there would hand
  every caller a claim of exclusive ownership; it is reached by accident, being what
  `ICacheFactory.CreateCache` resolves to when the requested provider is absent or has
  `Enabled=false`. Added to both interfaces as **default interface methods**, so
  **required** members rather than default interface methods: a probe followed by a write is not
  atomic, and no fallback body could stand in for one without either voiding the guarantee or hiding
  which stores can arbitrate, so an implementation states what its own store can do. **This is
  source-breaking for hand-written `ICache` / `ICache<T>` implementations,** which must add the three
  overloads. `NullCache` returns `true`, as its `SetAsync` does — the null store retains nothing, so
  no key pre-exists and no caller loses; note that it provides no exclusion at all and is what
  `ICacheFactory.CreateCache` resolves to for an absent or disabled provider, so assert the provider
  you expect at startup when the `true` branch runs a side effect that must not repeat. `CacheExtensions` carries the token-positional overloads
  (`TryAddAsync(key, value, token)` and the `TimeSpan?`/`DateTimeOffset?` pairs), matching `SetAsync`.
  No multi-key overload: Redis has no atomic multi-key `NX`, and all-or-nothing versus per-key
  semantics would be a guess. This is a cache primitive, not a lock — no ownership token, no early
  release, and a later `SetAsync`/`RemoveAsync` ignores the claim; for a fencing token and explicit
  release use `IDistributedLock`. No conditional-add member was added to the hash surface, where `NX`
  is per-field (`HSETNX`) and a different shape.
- A cancellation raised while the `SET … NX` write is in flight now propagates instead of being
  reported as `false`, which would have claimed the key belongs to someone else — a fact the cancelled
  call never established. The write itself stays on the shared `Write` resilience pipeline: retries
  fire on exceptions only, and re-issuing `NX` is harmless, since an attempt whose reply was lost is
  refused by the key it just wrote and reports the same `false` the exception would have, while an
  attempt that never reached Redis is recovered as the `true` it should have been.

### Changed

- **BREAKING:** an unset expiration no longer means "keep this forever". `CachePolicy` gained
  `DefaultDistributedExpiration` (1 hour), applied as the floor under
  `CachePolicy.DistributedExpiration` and the providers' `DefaultExpiration` on every write that
  carries no caller expiration. Previously the whole chain was nullable with nothing underneath it,
  so a provider whose `DefaultExpiration` was set to `null` — in code or bound from configuration —
  wrote entries with no TTL into shared storage. The 1 hour that four options classes each declared
  as a property initializer now comes from that one constant, so the value is stated once.
  Unbounded entries are still available and now have to be asked for: configure a lifetime of
  `TimeSpan.MaxValue`, which is what the providers already read as "no TTL" (`SET` with no TTL,
  `PERSIST` on refresh). `TimeProvider.ToDateTimeOffset` is the single place a duration becomes a deadline, and it
  saturates at `DateTimeOffset.MaxValue` rather than overflowing, so `TimeSpan.MaxValue` reaches the
  providers as the sentinel they read as "no TTL". The memory-only set tier and the rehydrate write
  each did that conversion themselves with a bare `Add` and threw on it; both go through the clock
  now.
  Jitter leaves that sentinel alone: jitter spreads an expiry that is coming, and clamping an
  unbounded lifetime would put the key back on the `EXPIRE` path.

  The floor is on the **write** paths only — `MultilayerCacheBase.ResolveWriteDuration`,
  `RedisCacheBase.PolicyDuration` / `GetExpiration` — deliberately not in the
  resolved default policy, so "nothing configured" stays observable as `null` and a *read* never
  consults a default: a key that genuinely has no TTL in Redis reports `DateTimeOffset.MaxValue`
  rather than a fabricated `now + default`.
  `MultilayerCacheBase.ResolveWriteDuration` returns `TimeSpan` rather than `TimeSpan?` as a result.
- **BREAKING:** `CacheClock` is gone (see **Removed**); the clock is `System.TimeProvider`. No default
  expiration is carried anywhere near the clock any more. Previously a configured `DefaultExpiration`
  was also filled in on **reads** — `GetCacheEntryAsync` on a key with no TTL in Redis reported
  `now + DefaultExpiration`, while `ExpireTimeAsync` on the same key returned `null` — and the
  fabricated deadline doubled as a hidden L1 cap: the L1 copy of a no-TTL key turned over every
  `DefaultExpiration`. Reads now report `DateTimeOffset.MaxValue` for such a key, and the L1 copy
  lives until a broadcast invalidation or `LocalMaxExpiration` evicts it, which is what that knob is
  for. Write paths are unaffected: every one resolves its lifetime before asking the clock.

  `GetOrAddAsync` resolves the lifetime **once** and uses it for both the write deadline and the
  rehydrate threshold. These were two separate chains, and the floor would otherwise have applied to
  only one of them: entries written under the default would expire after an hour while proactive
  rehydration, still bottoming out at `null`, never fired. Only the write is jittered: the rehydrate
  threshold and timeout are measured against the configured, unjittered lifetime, so a hit crosses
  the soft-TTL threshold at the same point on every read rather than against a fresh random draw.
  Rehydration is skipped for an unbounded lifetime, which has no deadline to pre-empt.
- **BREAKING:** every conversion from a duration to a deadline goes through the `TimeProvider`, so a
  per-call `TimeSpan.MaxValue` means "no TTL" on every surface. It already did on `RedisCache.SetAsync`
  (the driver maps it) and on the set caches, but `MultilayerCache.SetAsync`, `RefreshAsync` and
  `GetOrAddAsync`, `RedisCache.RefreshAsync`, and `RedisHashCache.GetOrAddAsync` / `RefreshAsync` /
  `SetAsync` each added the duration to the clock themselves and threw `ArgumentOutOfRangeException`
  from `DateTimeOffset.Add` instead; a `LocalMaxExpiration` of `TimeSpan.MaxValue` threw on every L1
  write. The sentinel now survives the other direction too: a per-call deadline of
  `DateTimeOffset.MaxValue` becomes a duration of `TimeSpan.MaxValue` rather than a finite eight
  thousand years, so the rehydrate guard recognizes it. With the downstream add saturating, the two
  pre-clamps that protected it are gone — `MultilayerCacheBase.ApplyJitter` no longer trims the
  jittered lifetime to the clock's remaining range (and takes one clock reading per write fewer), and
  the `IDistributedCache` adapter hands `TimeSpan.MaxValue` to the backing cache untouched.
  `ApplyJitter` is now `(TimeSpan, TimeSpan?) -> TimeSpan`; `RedisCacheBase.GetExpiration(TimeSpan)`
  is added.
- **BREAKING: one clock.** `System.TimeProvider` is the single source of "now". `AddCaching` registers
  `TimeProvider.System` unless the container already has a `TimeProvider`, and every provider, cache
  base class, helper, the profiler and the stream maintainer take it from DI. No library clock type
  remains: the BCL's own seam is the one, and any `TimeProvider` subclass drives it in tests. The per-options `Clock`
  properties are gone (see **Removed**): they let each cache carry its own clock while the
  `IMemoryCache` judging its deadlines ran on the DI-wide one, so a configured clock made L1 entries
  expire early or late against the ambient clock, and the memory-only set tier read
  `DateTimeOffset.UtcNow` directly and ignored a clock altogether. The conversion from a lifetime to
  an expiration is `TimeProviderExtensions.ToDateTimeOffset(...)` in the abstractions package, so a
  fake converts exactly like the real clock. To control time, register a `TimeProvider` before
  `AddCaching`; an `ISystemClock` registration is no longer consulted.
- **`IMultilayerSetCacheOptions`** is what `MultilayerSetCache` reads its settings from — the memory
  tier's knobs through `IMemoryCacheOptions`, plus `DefaultExpiration`, `LocalMaxExpiration`,
  `ConnectionMonitorEnabled`, `ConnectionMonitorPeriod`, `UseLocalOnlyWhenDisconnected` and
  `LocalMaxExpirationDisconnected` — instead of each setting as a constructor argument. Both queue
  options types implement it: `InMemoryRedisQueueCacheOptions` gains `DefaultExpiration`, which — like
  `InMemoryRedisCacheOptions.DefaultExpiration` — outranks the Redis tier's own default for sets written
  through the provider (`null`, the default, inherits as before), and `InMemoryQueueCacheOptions` gains
  the connection and local-cap settings for parity, which have nothing to observe on a provider with no
  backing tier and default to off.
- **BREAKING:** `AddDistributedCache` no longer fails at registration when no bounded default
  resolves, because that state no longer exists — an unset default now resolves to the floor. The
  `"would store entries without an expiration"` throw is removed; the non-positive check stays,
  since a value someone configured as zero or negative is still a real misconfiguration.
  `UiPathDistributedCacheOptions.AllowUnboundedEntries` keeps its meaning and is now the only way
  to reach an unbounded entry through that adapter without naming a lifetime.
- **BREAKING:** the per-call `expiration` is no longer nullable. Every write on `ICache`,
  `ICache<T>`, `IHashCache`, `IHashCache<T>`, `ISetCache`, `ISetCache<T>` and their extension
  surfaces takes `TimeSpan` / `DateTimeOffset` instead of `TimeSpan?` / `DateTimeOffset?`. The
  nullable was a redundant third state: each of these members already has a sibling overload with no
  `expiration` parameter, and passing `null` meant exactly the same thing as not passing it — fall
  back to `CachePolicy.DistributedExpiration`, then the provider's `DefaultExpiration`. It also made
  `cache.SetAsync(key, value, null)` ambiguous (`CS0121`) between the `TimeSpan?` and
  `DateTimeOffset?` overloads, which is why the forwarders had to spell out `(CachePolicy?)null`.
  Callers passing a real value are unaffected; a caller forwarding its own `TimeSpan?` now branches
  on it and calls the overload without an expiration for the null case. Nullability stays where it
  means *inherit* — `CachePolicy.LocalExpiration` / `DistributedExpiration`, the providers'
  `DefaultExpiration`, `HashCacheEntryOptions.ExpireTime` / `TimeToLive` — and on reads, where
  `TimeToLiveAsync` / `ExpireTimeAsync` still return `null` for a key with no TTL.
- **BREAKING:** a per-call `expiration` that cannot be honored is now rejected instead of silently
  absorbed. A `TimeSpan` that is not strictly positive, or a `DateTimeOffset` at or before the
  cache's current time, raises `ArgumentOutOfRangeException` (`ParamName` `"expiration"`) and nothing
  is written. Previously such a value was quietly treated as "no expiration" and, on `TryAddAsync`,
  answered `false` — the same answer as "somebody else holds the key". With the nullable gone there
  is no third state left to carry that meaning, so the argument is refused at the boundary. The new
  public `CacheExpiration` helper (`ThrowIfNotPositive`, `ThrowIfNotFuture`, `ToDuration`) holds the
  guard for out-of-tree implementations. `TimeSpan.MaxValue` and `DateTimeOffset.MaxValue` remain
  valid — they are how the providers spell "no TTL". `NullCache`, `NullHashCache` and `NullSetCache`
  read no argument at all and so enforce nothing, keeping "caching is off, carry on".
- **BREAKING:** `ICache.Compat.cs`, `IHashCache.Compat.cs` and `ISetCache.Compat.cs` are gone. The
  pre-`CachePolicy` convenience overloads they carried as default interface methods —
  `GetAsync<T>(key, token)`, `SetAsync<T>(key, value, expiration, token)`,
  `TryAddAsync<T>(key, value, token)`, `AddAsync<T>(key, item, token)`, `PopAsync<T>(key, token)` and
  the rest — now live as extension methods on the new `CacheExtensions`, `HashCacheExtensions` and
  `SetCacheExtensions` static classes in the same `UiPath.Caching` namespace (`SetCacheExtensions`
  ships in `UiPath.Caching.Queue`, alongside `ISetCache`). Call sites are unchanged and need no edit;
  the interfaces shrink to just the policy-bearing members, so an implementation now has one member to
  write per operation instead of one plus an inherited forwarder it could accidentally override.
- **BREAKING:** `ICacheOfT.Sync.cs`, `IHashCacheOfT.Sync.cs` and `ISetCacheOfT.Sync.cs` are gone the
  same way. The 59 blocking forwarders they carried as default interface methods — `Get`, `GetOrAdd`,
  `Set`, `TryAdd`, `Refresh`, `Remove`, `Contains`, `TimeToLive`, `ExpireTime`, the hash surface's
  `GetItem`, `GetCacheEntry`, `GetMetadata` and `SetMetadata`, and the set surface's `Add`, `Pop`,
  `Members`, `ContainsItem`, `Count`, `RemoveItem` and `RemoveItems` — now live on the new
  `CacheSyncExtensions`, `HashCacheSyncExtensions` and `SetCacheSyncExtensions` static classes
  (`SetCacheSyncExtensions` ships in `UiPath.Caching.Queue`). Each still blocks on the async member
  via `.AsTask().GetAwaiter().GetResult()`; nothing about the blocking behavior changed. `T` becomes a
  method type parameter inferred from the receiver, so call sites are unchanged, and they stay
  reachable through the concrete `Cache<T>` / `HashCache<T>` / `SetCache<T>` classes as well as the
  interfaces. `partial` comes off `ICache<T>`, `IHashCache<T>` and `ISetCache<T>`, which nothing else
  extends now, leaving all three as pure async contracts: an implementation writes only the members it
  actually implements, rather than inheriting blocking forwarders it could accidentally override.
- **BREAKING:** `CachePolicy? policy` is now a **required** parameter on every `ICache`,
  `IHashCache` and `ISetCache` member that takes one, along with the `expiration` / `setOption`
  parameters that precede it — the `= null` defaults are removed. `CacheExtensions` /
  `HashCacheExtensions` / `SetCacheExtensions` supply the short forms, so
  `cache.GetAsync<T>(key, token)` and `cache.SetAsync(key, value, expiration)` still
  compile; what no longer compiles is an interface call that relied on the defaults to skip the policy
  slot positionally. This is also what makes the extensions load-bearing rather than decorative: while
  the interface still declared `policy = null`, an applicable interface overload existed for every
  short call and instance members always beat extension members, so `cache.GetAsync<T>(key, token)`
  kept binding to the interface and the extension was never reached. With `policy` required, no
  interface overload is applicable to the short forms and there is exactly one way to spell "no
  policy". Implementations (`MultilayerCache`, `RedisCache`, their hash
  counterparts, `NullCache`, `NullHashCache`, `MultilayerSetCache`, `RedisSetCache`, `NullSetCache`)
  drop the defaults too, so behavior is identical whether the call goes through the interface or the
  concrete type. The typed `ICache<T>` / `IHashCache<T>` / `ISetCache<T>` façades are unchanged —
  they never had a `policy` parameter, since they resolve one at construction.
- **BREAKING:** `CacheKey.Equals` and `GetHashCode` are now ordinal rather than
  `InvariantCultureIgnoreCase`. Insensitive keys are still lowercased at construction, so the stored
  key and every comparison between insensitive keys are unchanged; what changes is that equality is
  now consistent with `GetHashCode` (the previous pairing could report two keys equal while hashing
  them into different buckets) and that keys built with `CacheKeyCasing.Sensitive` compare
  case-sensitively.
- **`Microsoft.Extensions.*` dependency floor for `net10.0` raised to 10.0.11** (from 10.0.10).
  The `net8.0` floor is unchanged at 8.0.x.
- **OpenTelemetry packages moved to 1.18.0** (`OpenTelemetry.Instrumentation.StackExchangeRedis` to 1.18.0-beta.1).
- **BREAKING (source, not data):** every cache now serializes through `ISerializerProxy<byte[]>`.
  `RedisCache`, `RedisHashCache`, `RedisSetCache`, the memory set tier and all four providers take
  `ISerializerProxy<byte[]>` in place of `ISerializerProxy<RedisValue>`, and the broadcast change
  token is built as `ChangeTokenFactory<byte[]>`. **No stored entry changes format and nothing needs
  migrating** — the default serializer emits exactly what `SystemJsonSerializerProxy` emitted.
  Consumers who pass these constructors a serializer directly, or who register a custom one, need a
  one-line type change; `AddCaching` now throws at startup if a leftover
  `ISerializerProxy<RedisValue>` registration is present, rather than ignoring it and silently
  falling back to JSON.

### Removed

- **BREAKING:** `SystemJsonSerializerProxy` and the `ISerializerProxy<RedisValue>` registration.
  `SystemJsonByteSerializerProxy` (in `UiPath.Caching.Abstractions`) is the single default and writes
  the same bytes. The generic `ISerializerProxy<T>` interface itself is unchanged.
- **BREAKING:** `IEventFormatterProxy<T>.Decode(string)` and `EncodeAsString(T)`, both obsolete since
  the broadcast path moved to `ReadOnlyMemory<byte>` and both unused — nothing in the library called
  them and no implementation overrode the default bodies. Use `Decode(ReadOnlyMemory<byte>)` and
  `Encode(T)`.
- **BREAKING:** `CacheClock.DefaultDateTimeOffset()`, `CacheClock.ToTimeSpan(DateTimeOffset?)` and
  `CacheClock.ToTimeSpan(TimeSpan?)`. Nothing called them, and the first `ToTimeSpan` was another
  place a configured `TimeSpan.MaxValue` overflowed. `ToDateTimeOffset` is the conversion that remains,
  now as a `TimeProvider` extension.
- **BREAKING:** `CacheClock` itself. `System.TimeProvider` is the clock; nothing wrapped
  `Microsoft.Extensions.Internal.ISystemClock` any more, so the type had no job left.
- **BREAKING:** the per-options `Clock` properties: `ICacheOptions.Clock` and its implementations on
  `InMemoryCacheOptions`, `InMemoryRedisCacheOptions` and `RedisCacheOptions`,
  `RedisConnectionOptions.Clock`, `InMemoryQueueCacheOptions.Clock` and
  `InMemoryRedisQueueCacheOptions.Clock`. Time comes from the one `TimeProvider` in DI — see
  **Changed**. `MemoryCacheFactory`, `UiPathDistributedCache`, the provider constructors,
  `MultilayerCacheBase`, `RedisCacheBase`, `RedisProfiler` and `RedisStreamHealthMaintainer` take an
  `TimeProvider` instead of an `ISystemClock?` or nothing.
- **BREAKING:** the `[Obsolete]` options properties. The rename aliases `PrimaryMaxExpiration`,
  `PrimaryMaxExpirationDisconnected` and `UsePrimaryOnlyWhenDisconnected` on `IMultilayerCacheOptions`,
  `InMemoryCacheOptions` and `InMemoryRedisCacheOptions` — use `LocalMaxExpiration`,
  `LocalMaxExpirationDisconnected` and `UseLocalOnlyWhenDisconnected`, which they forwarded to — and
  `RedisConnectionOptions.ThreadPoolSocketManager`, a no-op since StackExchange.Redis 3.0. Configuration
  bound under the old keys is silently ignored by the binder, so rename those keys as well.

### Fixed

- **`RefreshAsync` on Redis reported nothing.** Both Redis caches sent their standalone
  `KEYEXPIRE`/`PERSIST` fire-and-forget, so the call returned `false` whether the refresh succeeded, failed
  or the key did not exist, the server's reply was never seen — a rejected command was neither logged nor
  retried by the resilience pipeline — and telemetry recorded every refresh as unsuccessful. Setting
  `RedisCacheOptions.AwaitRefresh` makes the result meaningful; the default is unchanged, so existing
  callers see the previous behavior until they opt in. The fire-and-forget flags inside the write
  transactions are untouched: those replies are discarded, but the transaction itself is awaited.
- **`AddCaching(IConfigurationSection)` never bound `CacheOptions`.** The overload passed its options
  binder positionally, so it landed on the `configure` parameter and type-checked as
  `Action<ICachingBuilder>` — leaving `configureOptions` null, the section unread, and the config bound
  onto the builder object instead. Every `CacheOptions` value supplied through that entry point was
  silently ignored; `Enabled`, `AppShortName`, `KeyCasing` and the rest now take effect, which changes
  behavior for anyone who was unknowingly running on the defaults.
### Documentation

- New recipe [`conditional-add.md`](docs/recipes/conditional-add.md) — claiming a key exactly once with
  `TryAddAsync`, why check-then-set is not equivalent, and when to reach for `IDistributedLock`
  instead. `interfaces.md` documents the member on both cache surfaces with a per-provider table of
  who arbitrates and how far exclusion reaches; `concepts.md` places it next to the two lock
  abstractions, whose goal is reducing redundant work rather than a decision the caller can branch on.
  The recipe's worked example is a daily digest rather than a payment capture, and says why: a claim
  marker records that someone *started*, never that anyone finished, so an operation that must
  eventually happen needs a recorded outcome instead. Both documents also state plainly that the
  ambiguity in `false` is not recoverable — a serialization or command failure returns `false` with
  the connection snapshot still healthy — rather than pointing at `IConnectionState` as an escape
  hatch, and both explain why the `NX` write is safe to retry where `SPOP` is not.
- New `IConnectionState` section in `interfaces.md`, a public type that was documented nowhere (and
  that the `TryAddAsync` guidance linked to through an anchor resolving to `IDistributedLock`). It is
  described as what it is — a cache-health snapshot for probes, metrics and backoff — not as a way to
  explain a negative result.
- Corrected `interfaces.md`, which described the `IHashCache<T>.SetAsync(…, HashCacheEntryOptions, …)`
  overload as offering "conditional set, individual field TTL". `HashCacheEntryOptions` carries
  neither: `HashCacheSetOption` selects write *scope* (`HashReplace` merges fields, `KeyReplace` drops
  the key first), and there is no per-field TTL.

## [1.3.0] - 2026-08-19

### Added

- **Batch `GetOrAddAsync` with caller state.** `ICache`, `ICache<T>` and `Cache<T>` gained multi-key
  `GetOrAddAsync` overloads that pair each key with an opaque caller state (`TState`) — a database id,
  a request object, whatever identifies the entry in the caller's own vocabulary. The generator is
  invoked at most once on the calling path, receiving only the states of the entries that missed every
  cache layer, never the keys; results come back keyed by state, one entry per distinct requested
  state, in first-occurrence order. Cache operations de-duplicate by `CacheKey` (one probe, one write
  per distinct key); results de-duplicate by state — when two states share a key the generator is
  asked once, about the first state seen for that key, and the value is reported under both. A state
  the generator omits is returned as `default(T)` and left uncached, so the next call retries the
  source. A caller whose keys are their own identity pairs each key with
  itself (`TState = CacheKey`); the blocking `GetOrAdd` facade on `ICache<T>` accepts `CacheKey[]`
  directly and does that pairing for you. On `MultilayerCache` the generator call is guarded by
  a single composite lock derived from the missing key set, and proactive rehydration of hit keys is
  coalesced into one background call that locks per key, so overlapping aging sets on different
  nodes refresh each shared key once. Added to `ICache` as default interface methods, so existing
  `ICache` implementations keep compiling; the `ICache<T>` overloads are **abstract** — see the
  breaking note under **Changed**.
- **`InMemoryRedisCacheOptions.BroadcastEnable`** (default `true`) turns cross-node L1 invalidation off for the `InMemoryRedis` provider alone, so L1+L2 tiering can be used without broadcast traffic. Intended for single-node deployments, where cross-node invalidation buys nothing, and for Redis-compatible backends whose Streams support does not cover `XREADGROUP`. When `false` the provider gets `NullTopicFactory` / `NullChangeTokenFactory` / `NullCacheEventFactory`, mirroring what `InMemoryCacheProvider` already did for `InMemoryCacheOptions.BroadcastEnable`. The flag can only narrow `CacheOptions.BroadcastEnabled`, never widen it; note its default is the opposite of the `InMemory` equivalent, which is opt-in. ([#103](https://github.com/UiPath/dotnet-caching/issues/103))

### Changed

- **BREAKING:** `ICache<T>` gains three **abstract** multi-key `GetOrAddAsync` members. They could not
  ship as default interface methods: `ICache<T>` has no `GetCacheEntriesAsync`, so a default body
  could not tell a miss from a cached null. This is **source- and binary-breaking** for any
  hand-written `ICache<T>` implementation (test fakes, typically) — source-breaking because such a
  type no longer compiles until the members are added, and binary-breaking because an already-compiled
  assembly implementing `ICache<T>` throws `TypeLoadException` when loaded against the new
  `UiPath.Caching.Abstractions`, with no recompile involved. Consumers that pick the new abstractions
  up transitively must rebuild, not just restore. The `ICache` overloads are default interface
  methods, so `ICache` implementations are unaffected.

### Fixed

- The Redis Streams fetch loop no longer hammers a server that does not implement `XREADGROUP`. It previously re-issued the command and logged `Fetch events loop error` once per `PollInterval` (250 ms by default) forever. The unsupported-command case is now reported once at `Critical` with the available remedies, then retried every 30 s; a successful fetch lifts the quarantine and logs recovery, and a connection-restored/reconnected event wakes the loop early so recovery does not wait out the backoff. Recovery does not depend on those events: with the connection monitor off (the default) the timed retry still gets there. Other consecutive fetch failures now back off exponentially from `PollInterval` up to 30 s instead of retrying at the poll rate. ([#103](https://github.com/UiPath/dotnet-caching/issues/103))
- **`CacheOptions.BroadcastEnabled` now actually disables broadcast.** It was read only by `RedisStreamHealthMaintainer`, so setting it through the `AddCaching` options lambda stopped the maintainer while the Streams fetch loop kept running, despite sharing a name with the configuration key that does perform the real opt-out. `TopicFactory` now resolves every topic to `NullTopicProvider` and reports no provider names when the flag is `false`. ([#103](https://github.com/UiPath/dotnet-caching/issues/103))

### Documentation

- `concepts.md` now states that `AddInMemoryRedis()` wires broadcast internally and that a code-only `AddBroadcast(enabled: false)` does not stick, and links to the broadcast how-to. The how-to gains a section on disabling broadcast for `InMemoryRedis` alone and a troubleshooting entry for `ERR unknown command` against Redis-compatible backends. ([#103](https://github.com/UiPath/dotnet-caching/issues/103))

## [1.2.0] - 2026-08-04

### Added

- Set caches in the `UiPath.Caching.Queue` package gained **`InMemory` and `InMemoryRedis` backings** alongside Redis, via a provider model that mirrors the core cache. New per-backing registrations `AddQueueMemory` / `AddQueueRedis` / `AddQueueInMemoryRedis` on `ICachingBuilder` (each with a config-section overload binding the same `KnownCacheProviderNames` sections the core providers use); a new `IQueueCacheProvider` (one per backing — `InMemoryQueueCacheProvider` / `RedisQueueCacheProvider` / `InMemoryRedisQueueCacheProvider`); and `QueueCacheFactory` now selects a provider by name via `CacheOptions.DefaultCache` (or an explicit name, falling back to the default then `NullSetCache`), and is `IDisposable` with `AddProvider` — exactly like `ICacheFactory`. The `InMemoryRedis` backing is a multilayer set cache: a local in-process snapshot (L1) over the Redis set cache (L2), with the same connection-monitor / local-only-when-disconnected behavior as the core `InMemoryRedis` cache. New options `InMemoryQueueCacheOptions` / `InMemoryRedisQueueCacheOptions` (shared by all collection kinds of a backing, like `InMemoryCacheOptions`) and `RedisSetCacheOptions.Enabled`.

### Changed

- Bumped the OpenTelemetry package family from 1.16.0 to 1.17.0 (`OpenTelemetry`, `OpenTelemetry.Exporter.OpenTelemetryProtocol`, `OpenTelemetry.Exporter.Console`, `OpenTelemetry.Extensions.Hosting`, `OpenTelemetry.Instrumentation.AspNetCore`, `OpenTelemetry.Instrumentation.Http`, `OpenTelemetry.Instrumentation.Runtime`, and `OpenTelemetry.Instrumentation.StackExchangeRedis` 1.16.0-beta.1 → 1.17.0-beta.1). Bumped as one set so the family stays version-consistent.
- **BREAKING:** `SetCacheCollectionExtensions` and its `AddRedisSetCache` overloads (both `IServiceCollection` and `ICachingBuilder`) are removed. Register the Redis set cache via `AddCaching(... builder.AddQueueRedis())` (same `RedisSetCacheOptions`, same behavior). Registration now always goes through the `ICachingBuilder`, which guarantees the core caching services are present and honors the builder's `Enabled` flag. (`AddRedisSetCache` shipped only in 1.1.0.)
- **BREAKING:** the parameterless `IQueueCacheFactory.CreateSetCache()` / `QueueCacheFactory.CreateSetCache()` members and the parameterless `CreateSetCache<T>(this IQueueCacheFactory)` extension are removed in favor of a single `CreateSetCache(string? providerName = null)`, exactly like `ICacheFactory.CreateCache`. Source-compatible (`factory.CreateSetCache()` still compiles and means "default provider"), but binary-breaking — recompile against the new package. (Shipped only in 1.1.0.)
- **BREAKING:** the `QueueCacheFactory(ISetCache)` constructor is removed — the factory is provider-based only, like `CacheFactory`. To pin a fixed `ISetCache` (e.g. in tests), register it through a stub `IQueueCacheProvider` and point `CacheOptions.DefaultCache` at its name. (Shipped only in 1.1.0.)

## [1.1.0] - 2026-07-17

### Added

- `RedisCollectionExtensions.AddRedisConfigurationOptionsProvider(this ICachingBuilder, Func<IServiceProvider, IRedisConfigurationOptionsProvider>)` — registers a custom `IRedisConfigurationOptionsProvider` via a factory delegate, replacing the default `RedisConfigurationOptionsProvider`. Order-independent (uses `Replace`), so it can be called before or after `AddRedisConnection` and the supplied factory always wins. Previously a custom provider required manually pre-registering it so the `TryAdd` default backed off.

### Changed

- Bumped `StackExchange.Redis` from 2.10.1 to 2.13.17 (latest 2.x), the first step of the staged upgrade toward 3.0. No public API or runtime behavior change.
- Bumped `Microsoft.Extensions.Logging.Abstractions` on `net8.0` from 8.0.3 to 10.0.10 (matching the `net10.0` pin). Prerequisite for `StackExchange.Redis` 3.0, which requires `>= 10.0.5`; `net8.0` consumers now transitively resolve the 10.x abstractions package (compatible — it targets net8.0).
- Bumped `StackExchange.Redis` from 2.13.17 to 3.0.17. 3.0 is an internal IO-core rewrite that mirrors 2.13.17's public API, so there is no public API change here. The live hang-detection and profiling reflection paths were verified against a real Redis on both target frameworks.
- **Obsolete:** `RedisConnectionOptions.ThreadPoolSocketManager` no longer has any effect and is marked `[Obsolete]`. StackExchange.Redis 3.0 removed `SocketManager` (its IO core no longer uses one), so the setting is now a no-op; it will be removed in a future major release.

### Performance

- Serialization now stays on UTF-8 bytes end-to-end instead of round-tripping through UTF-16 `string`s. JSON is UTF-8 on the wire, so the string hop was a pure allocation + transcode on every read/write. Measured with BenchmarkDotNet (`SerializerBenchmark`), this roughly **halves allocations** on the serialize/deserialize paths (e.g. large-payload deserialize `1,385,629 B → 607,714 B`, serialize `778,689 B → 389,660 B`) with equal-or-better throughput. No public API changes.
  - `SystemJsonSerializerProxy` (the default proxy shared by `RedisCache`, `RedisHashCache`, `RedisSetCache`, `RedisCacheProvider`) serializes via `JsonSerializer.SerializeToUtf8Bytes` and deserializes from the `RedisValue` payload as `ReadOnlySpan<byte>`. The benchmark also measured a `ReadOnlySequence<byte>`/`Utf8JsonReader` variant; it allocated identically to the span path but was slower for these (single-segment) payloads, so the span path was chosen.
  - `CacheEventFormatter.Encode` uses `SerializeToUtf8Bytes` (dropping the intermediate string + `Encoding.UTF8.GetBytes`); the Redis broadcast publish paths (`RedisPubSubTopic`, `RedisStreamsTopic`) publish the encoded bytes as a `RedisValue` directly instead of via `EncodeAsString`; and the subscribe paths (`RedisPubSubSubjectWriter`, `RedisStreamSubjectWriter`) decode from the `RedisValue` bytes instead of `value.ToString()`.
  - Wire format is unchanged everywhere — values written by the previous string paths still deserialize.

### Deprecated

- `IEventFormatterProxy<T>.Decode(string)` and `IEventFormatterProxy<T>.EncodeAsString(T)` are marked `[Obsolete]`. They forced a UTF-16 transcode and are no longer used by the library; use the byte-based `Decode(ReadOnlyMemory<byte>)` / `Encode(T)` instead.

## [1.0.1] - 2026-07-15

### Changed

- Repository extracted from `UiPath/ServiceCommon` to the public `UiPath/dotnet-caching` repository under the MIT license. PackageIds are renamed `UiPath.Platform.Caching.*` → `UiPath.Caching.*` at the first nuget.org release.
- **BREAKING:** `IConnectionMultiplexerFactory` is now async-only — the synchronous `Create(ConfigurationOptions)` member is removed and `CreateAsync(ConfigurationOptions, CancellationToken)` is the sole member. Custom factory implementors must implement `CreateAsync`. `RedisConnector` now caches the connection as `Lazy<Task<IConnectionMultiplexer>>` (one shared creation for async warm-up and sync first-use, with faulted-task self-heal), `IsConnected`/`GetEndPoints` are non-blocking snapshots (never block or throw), and `RedisPlannedMaintenance` is an `IHostedService` that connects/subscribes off the startup critical path with bounded retry + warning logging and deterministic shutdown.

### Added

- `UiPath.Caching.Azure` package + `ICachingBuilder.AddAzureEntraAuthentication(...)` — Microsoft Entra ID authentication for Azure Managed Redis. The core stays Azure-free via a new async extension point `IRedisConnectionConfigurator` (in `UiPath.Caching`), applied to the `ConfigurationOptions` once per connect (before `IConnectionMultiplexerFactory`); the Azure package implements it through `Microsoft.Azure.StackExchangeRedis`, which owns automatic token refresh. External apps implement `IRedisConnectionConfigurator` for other schemes (AWS IAM, custom tokens) with no core change. With no configurator registered, behavior is unchanged. `IRedisConnector` gains a `ConnectAsync` warm-up as a default-interface method (existing implementors unaffected). Entra connections default to TLS (`AzureEntraOptions.RequireSsl`) and RESP3 (`AzureEntraOptions.RequireResp3`) so pub/sub survives token rotation. Credential selection: an explicit `Credential`, else `ManagedIdentityCredential` from `ManagedIdentityOptions` when set (its `ManagedIdentityId` — set on `ManagedIdentityCredentialOptions` at construction — chooses the identity, defaulting to system-assigned; sovereign-cloud `AuthorityHost` supported here), else a user-assigned `ManagedIdentityCredential` from a non-empty `ManagedIdentityClientId`, else `DefaultAzureCredential`. `ManagedIdentityOptions` takes precedence over `ManagedIdentityClientId`; to combine a user-assigned identity with custom options, construct `ManagedIdentityOptions` with `ManagedIdentityId.FromUserAssignedClientId(clientId)`.
- `RedisConnectionOptions.WarmUpOnStart` (default `false`) — opt-in hosted-service warm-up that establishes the Redis connection at host startup (running any registered `IRedisConnectionConfigurator`s); best-effort, never blocks or fails startup. Left `false`, the connection is established lazily on first cache use. `RedisConnectionOptions.PlannedMaintenanceConnectionRetryCount` (default `5`) / `PlannedMaintenanceConnectionRetryDelay` (default `5s`, negative/zero clamped to `1s`) tune the planned-maintenance subscription's background retry.

- The hit/miss metric (`Caching.Stats.Hits`/`Misses`) is now emitted **once per read operation** with a `Keys` dimension carrying the number of keys in the operation (a batch's outcome is hit when any key hit). A multi-key `MGET` no longer fans out into one elapsed-time sample per key, which would skew the metric's latency distribution by batch size. Every hit/miss metric carries the `Keys` dimension (`1` for single-key and non-read operations such as writes/deletes/TTL, the batch size for multi-key reads and bulk set/remove) so the dimension is consistent across the metric name and group-by-`Keys` queries don't bucket operations into a null dimension.
- `RedisCacheOptions.KeyReadTelemetryEnabled` (default `false`) — opt-in per-key read attribution for `RedisCache` and `RedisHashCache`. When enabled, the read paths additionally emit a dependency **per key** with `type = "Redis"`, the Redis key in `data`, hit/miss carried by `resultCode` (`Hit`/`Miss`) plus an `Outcome` property, `Provider`/`Method`/`Type` properties, and a `BatchId` shared by all keys of one operation so the keys read together can be correlated (`summarize by BatchId` — a `Guid` minted per operation, since neither Redis nor StackExchange.Redis exposes a transaction id). The dependency's `success` is always `true` (a cache miss is not a failure, and `type = "Redis"` is shared with the profiler's dependency telemetry, so misses must not skew dependency-failure dashboards). For `RedisHashCache` it is **one dependency per hash key** (not per field) — an HGETALL on a hash with thousands of fields must not fan out into thousands of telemetry items — so `data` is the hash key and the field set is not enumerated. It lands in the same `dependencies` table the Redis profiler uses, so existing key-level KQL keeps working — restoring the "which keys are being read" attribution the profiler can no longer provide once batched reads are bundled into a single `MULTI`/`EXEC` transaction (the keys are no longer visible on the wire). It is opt-in because raw keys are high-cardinality. Surfaced through two new `ITelemetryOperation` members — `Track(bool hit, int keyCount)` (per-operation hit/miss metric with key count) and `TrackKeyReads((string Key, bool Hit)[] reads)` (opt-in per-key dependencies sharing a `BatchId`) — implemented by `TelemetryOperation` and no-op'd by `NullTelemetryOperation`. The per-key dependency's start time is captured at operation start (not at emit time).
- `IQueueCacheFactory` / `QueueCacheFactory` (+ `QueueCacheFactoryExtensions.CreateSetCache<T>()`) in `UiPath.Platform.Caching.Queue` — a dedicated set-cache factory that lets you create a typed set the same ergonomic way you create a cache: `queueCacheFactory.CreateSetCache<T>()`. It mirrors `CacheFactory.CreateCache` / `CacheFactoryExtensions.CreateCache<T>` for Redis sets. It is a separate factory (not a method on `ICacheFactory`) because the entire set implementation lives in the optional `Caching.Queue` package, which is downstream of `ICacheFactory` in `Caching.Abstractions` — so `ICacheFactory` cannot reference `ISetCache`. `QueueCacheFactory` hands out the singleton `ISetCache` registered by `AddRedisSetCache` (sets have a single Redis backing); `CreateSetCache<T>()` wraps it in `SetCache<T>` with no policy factory, exactly like `CreateCache<T>` (the underlying `RedisSetCache` still applies the global default policy). Registered automatically by `AddRedisSetCache`; inject `IQueueCacheFactory` where you need sets.
- `NullQueueCacheFactory` (in `UiPath.Caching`) — a no-op `IQueueCacheFactory` whose `CreateSetCache()` returns the singleton `NullSetCache.Instance`. `AddRedisSetCache` now registers `NullSetCache`, `NullQueueCacheFactory`, and `SetCache<>` when caching is disabled (`ICachingBuilder.Enabled == false`), mirroring how the core cache degrades to no-ops. Previously the disabled branch registered nothing, so any service depending on `ISetCache` / `IQueueCacheFactory` failed to resolve (and `ValidateOnBuild` threw) when caching was turned off; dependents now construct against the null set cache instead.
- `IResiliencePipelineProvider` — a name-based factory (`IResiliencePipeline Get(string? name)`) that replaces `IResiliencePipelineHolder`. It builds and caches a `ResiliencePipelineWrapper` for any **registered** name on first use, and returns a noop `EmptyResiliencePipeline` for a null/empty/unregistered name, so every component resolves the pipeline it needs through DI instead of receiving a fixed holder. The default registration is `EmptyResiliencePipelineProvider.Instance` (all-noop); `AddResilienceStrategies` registers the real `ResiliencePipelineProvider`. **BREAKING:** `IResiliencePipelineHolder` / `ResiliencePipelineHolder` are removed in favor of `IResiliencePipelineProvider`; the cache and topic components now take an `IResiliencePipelineProvider` constructor parameter. Consumers using the shipped DI wiring are unaffected; direct callers must pass the provider (use `EmptyResiliencePipelineProvider.Instance` to opt out of resilience).
- `CachingBuilderExtensions.AddResiliencePipeline(name, Action<ResiliencePoliciesOptions>)` — registers a named resilience pipeline, where `name` is the scope passed to `IResiliencePipelineProvider.Get(name)`. Each name gets its own `ResiliencePoliciesOptions` (resolved via `IOptionsMonitor` named options), so different scopes can carry different retry/timeout/circuit-breaker settings. `AddResilienceStrategies` predefines `ResiliencePipelineNames.Read` and `Write` from the base configuration; consumers can add new pipelines or retune the built-ins. Only registered names resolve to a real pipeline. The `ResiliencePipelineFactory` constructor now takes `IOptionsMonitor<ResiliencePoliciesOptions>` instead of `IOptions<ResiliencePoliciesOptions>` so it can resolve per-scope options; `Create<TResult>` selects the options for its scope via `optionsMonitor.Get(scope)`.
- `RedisSetCacheOptions.ResilienceKeyName` — the name of the resilience pipeline used for the **destructive-read** `SPOP` operations behind `RedisSetCache.PopAsync` (both overloads). The pipeline is resolved generically via `IResiliencePipelineProvider.Get(ResilienceKeyName)`, so a consumer who needs special handling for non-idempotent commands (e.g. a no-retry pipeline — a retry after a lost response would re-run `SPOP` server-side and silently drop members the first attempt already popped) registers their own scope and points `ResilienceKeyName` at it. When `ResilienceKeyName` is null/empty (the default), pops run with no resilience pipeline (`EmptyResiliencePipeline`), i.e. **at-most-once** with no retry. Wire it via `AddRedisSetCache(opt => opt.ResilienceKeyName = "...")`. The idempotent read/write operations continue to use the `Read` / `Write` pipelines.
- `ICachingTelemetryProvider` is now span-based: `TrackDependency`, `TrackEvent`, `TrackException`, and `TrackMetric` take `ReadOnlySpan<KeyValuePair<string, string>>` / `ReadOnlySpan<KeyValuePair<string, double>>` instead of `IDictionary`. `NullTelemetryProvider` is span-aware as a true no-op (zero allocation when telemetry is disabled). `CachingTelemetryProvider` (the adapter to `UiPath.Platform.Telemetry.ITelemetryProvider`) materializes the span once at the boundary via `TelemetryTags.ToDictionaryOrNull` to forward to the dict-based upstream. All hot-path call sites in `Caching.Runtime` migrated to span — cold-miss `RedisDistributedLock` events, Redis connection/maintenance events, memory-cache setter failure events, and stream subject writer events no longer allocate a tag dictionary at the call site.
- `ICache.GetCacheEntryAsync<T>` and `ICache.GetCacheEntriesAsync<T>` — bundled GET + TTL reads. Implementations may fetch the value and its remote expiration in a single network round-trip; `RedisCache` uses a `MULTI`/`EXEC` transaction (mirroring the hash-cache pattern).
- `RedisStreamsTopic` supports an optional Pub/Sub notify doorbell (`NotifyEnabled`) that drops publish-to-deliver latency from the poll interval to network RTT, while preserving stream durability and consumer-group semantics. Channel name defaults to the stream's Redis key joined with `NotifyChannelName` (default `"notify"`) using the same `CacheOptions.Separator` as the rest of the key scheme. Set `NotifyShardedPubSub = true` to use sharded Pub/Sub (`SPUBLISH`/`SSUBSCRIBE`, requires Redis 7.0+) so the doorbell does not fan out across cluster nodes; the sharded strategy wraps the stream key as a Redis Cluster hash tag (or inherits an existing one) so the channel and stream share the same slot — `XADD` and `SPUBLISH` go to the same node. Otherwise regular `PUBLISH`/`SUBSCRIBE` is used. Override the channel entirely via `NotifyChannelStrategy`. Pub/Sub is best-effort — the existing poll continues to run as a safety net.
- Single-flight locking on `MultilayerCache.GetOrAddAsync` / `MultilayerHashCache.GetOrAddAsync` via two new abstractions: `ILocalLock` (in-process serialization per cache key, default impl `AsyncKeyedLocalLock` backed by `AsyncKeyedLock`) and `IDistributedLock` (cross-process coordination, default impl `RedisDistributedLock` using `LockTakeAsync`/`LockReleaseAsync` with a `SourceUri`-prefixed token). Both are layered behind double-checked reads so a single generator runs per key on cache miss; lock-take failures fall back to a no-op disposable so a Redis outage degrades to thundering-herd behavior rather than stalling. New options on `IMultilayerCacheOptions`: `LocalLockEnabled`, `DistributedLockEnabled`, `DistributedLockTimeout` (default 500 ms), `DistributedLockExpiry` (default 5 s), `LockKeyStrategy` (default appends `:lck`). New options on `CacheOptions`: `LocalLockPoolSize` (default 100), `LocalLockPoolInitialFill` (default 10). Wire via `AddLocalLock()` / `AddRedisDistributedLock()` on `ICachingBuilder`; `AddMemory` and `AddInMemoryRedis` register them automatically. `CachingBuilder.Complete` registers `NullLocalLock` / `NullDistributedLock` as singletons via `TryAddSingleton`, so providers that do not register a lock get a safe no-op. Direct (non-DI) callers can also pass `NullLocalLock.Instance` / `NullDistributedLock.Instance` (both `public`) to opt out of locking.

### Changed

- **BREAKING:** `ITelemetryOperation` gains two members — `Track(bool hit, int keyCount)` (per-operation hit/miss metric carrying the key count) and `TrackKeyReads((string Key, bool Hit)[] reads)` (opt-in per-key dependencies). External implementers of `ITelemetryOperation` must add them; the shipped implementations (`TelemetryOperation`, `NullTelemetryOperation`) already do. `TrackKeyReads` is invoked only when `RedisCacheOptions.KeyReadTelemetryEnabled` is enabled; the keyless `Track(bool)` path is unchanged for existing consumers.
- `MultilayerCache` and `MultilayerHashCache` no longer issue a separate `ExpireTimeAsync` call after a cache-miss read. They now use the bundled `ICacheEntry.Expiration` returned by the inner cache. For Redis-backed multilayer caches this collapses N + 1 sequential commands to one transaction (one round-trip, one `EXEC` dependency in telemetry) when fetching N keys, eliminating per-key `PTTL`/`EXPIRETIME` traffic on the read path. Public APIs are unchanged.
- **BREAKING:** `ICachingBuilder.RegisterOnCompleteCallback(Action<ICachingBuilder>)` is replaced by `RegisterOnCompleteCallback(object key, Action<ICachingBuilder>)`. `CachingBuilder` keeps a per-builder set of seen keys and ignores re-registrations against an already-seen key on the same instance. Callers should pass a deterministic key (typically `typeof(YourExtensionsClass)`).
- **BREAKING:** `Caching.Polly.CachingBuilderExtensions.ConfigureTelemetry(this ICachingBuilder, bool, Action<TelemetryOptions>?)` is removed. Telemetry is now configured exclusively through `AddResilienceStrategies(..., configureTelemetryOptions: ...)` and `ResiliencePoliciesOptions.TelemetryEnabled`, which flow through `IOptions<TelemetryOptions>` / `IOptions<ResiliencePoliciesOptions>` per-container instead of process-static fields.
- **BREAKING:** `MultilayerCache` and `MultilayerHashCache` constructors take two new required parameters — `ILocalLock localLock` and `IDistributedLock distributedLock` — inserted before `ILogger logger` (now the last parameter). Apps using the built-in DI extensions (`AddMemory`, `AddInMemoryRedis`) are wired automatically through the new `AddLocalLock()` / `AddRedisDistributedLock()` registrations. Callers that instantiate these caches directly must pass both arguments; use the now-public `NullLocalLock.Instance` / `NullDistributedLock.Instance` to opt out (or resolve `ILocalLock` / `IDistributedLock` from DI). No overload was added on purpose so the breakage is loud rather than silent.
- **BREAKING:** `InMemoryCacheProvider` and `InMemoryRedisCacheProvider` constructors take new **required** parameters for the lock services — `ILocalLock` on the former, `ILocalLock` + `IDistributedLock` on the latter. Same rationale as the multi-layer cache ctors: DI users are unaffected (`CachingBuilder.Complete` `TryAddSingleton`-registers `NullLocalLock` / `NullDistributedLock`), but direct callers must now pass them explicitly. The previous optional / nullable parameter shape was rejected because it preserves source compatibility while still binary-breaking, which is the worst combination — silent for source readers, loud for production consumers.
- **BREAKING:** `IMultilayerCacheOptions` gains five lock-related members — `LocalLockEnabled`, `DistributedLockEnabled`, `DistributedLockTimeout`, `DistributedLockExpiry`, `LockKeyStrategy`. External types that implement this interface (rather than deriving from `InMemoryRedisCacheOptions` / `InMemoryCacheOptions`) must add the new properties; default interface implementations were avoided so missing storage cannot silently drop user-set values.
- **BREAKING:** `ICachingTelemetryProvider`'s `TrackDependency` / `TrackEvent` / `TrackException` / `TrackMetric` members no longer take `IDictionary<string, string>?` / `IDictionary<string, double>?` — they now take `ReadOnlySpan<KeyValuePair<string, string>>` / `ReadOnlySpan<KeyValuePair<string, double>>`. The dict-based members are removed from the interface entirely. The members carry empty default interface bodies (no-op) so that proxy-based mocking frameworks (NSubstitute / Castle.DynamicProxy) can build proxies for the interface — those frameworks cannot generate valid IL for abstract methods with ref-struct parameters. **External implementers must override the span methods** to do real work; relying on a previous dict-based implementation will silently drop telemetry because the dict members are no longer part of the interface contract. Migration: implement the four span methods directly; for callers building tag bags dynamically, use `List<KeyValuePair<string, string>>` plus `CollectionsMarshal.AsSpan(list)`, or convert an existing dictionary via `dict.ToArray()`.

### Fixed

- `RedisCache.GetCacheEntryAsync` / `GetCacheEntriesAsync` and `RedisHashCache.GetCacheEntryAsync` now pass `CommandFlags.PreferReplica` to `transaction.ExecuteAsync(...)`, so the bundled GET + TTL transaction actually routes to a replica. SE.Redis ignores the flag set on inner queued commands when picking a server for the transaction; without an explicit flag on `ExecuteAsync` the read landed on the master. These read paths now also use the `_read` resilience pipeline instead of `_write`.
- Bulk write transactions in `RedisCache.SetAsync(KeyValuePair<>[])` and `RedisHashCache` Set/Refresh paths now pass `CommandFlags.DemandMaster` to `transaction.ExecuteAsync(...)` for symmetry with the read fix and to make the master-only routing intent explicit (inner-command flags do not propagate to transaction routing in SE.Redis).
- `AddInMemoryRedis` and `AddResilienceStrategies` no longer use a process-static `_callbackRegistered` flag to gate their on-complete callback. The static guard silently broke L1 invalidation / resilience pipelines for any second host wired up in the same process: only the first builder's callback ever ran, leaving subsequent builders with `NullChangeTokenFactory` and `EmptyResiliencePipelineProvider.Instance`. Both extensions now register their callbacks against a deterministic per-extension key (e.g., `typeof(InMemoryRedisCollectionExtensions)` / `typeof(CachingBuilderExtensions)`) via the keyed `RegisterOnCompleteCallback` overload.
- `AddResilienceStrategies` no longer stores telemetry enable/config in process-static fields. `IResiliencePipelineFactory` now resolves `IOptions<TelemetryOptions>` and `IOptions<ResiliencePoliciesOptions>` from its own container, so two hosts in the same process keep independent telemetry settings. Previously the second `AddResilienceStrategies` call would silently overwrite the first host's telemetry configuration (the factory singleton read the statics at resolution time, not at registration time).

### Documentation

- Restructured `docs/` into a layered surface: `quickstart.md`, `concepts.md`, `how-to/`, `recipes/`, `reference/`. `docs/basics.md`, `docs/advanced-usage.md`, and `docs/telemetry.md` are removed; their content is migrated. See `docs/index.md` for the decision tree.
- `Sample.AspNetCore/appsettings.all.json` now lists every binding-visible option on every shipped options class with shipped defaults. Verified by `Caching.Tests/AppSettingsAllJsonBindingTests.cs`.

