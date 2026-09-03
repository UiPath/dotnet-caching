# Interfaces reference

The library's public interface surface. Each entry shows the namespace, signature, and when (and when not) to use it.

> ### Typical vs. power-user surface
>
> Most consumers use **`ICache<T>` / `IHashCache<T>`** via `ICacheFactory` extension methods. The typed surface gives you compile-time safety, a single key strategy per cache, and `CachePolicy` resolution by `typeof(T).FullName`.
>
> **`ICache` / `IHashCache`** are the dynamic-key power-user surface. Reach for them when keys or value types vary per call. They are not strictly more powerful than the typed surface — they are different shapes for a different problem.

---

## Cache surface

### `ICache<T>`

**Namespace:** `UiPath.Caching`

```csharp
public interface ICache<T>
{
    string Name { get; }

    ValueTask<T?> GetAsync(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<KeyValuePair<CacheKey, T?>[]> GetAsync(CacheKey[] cacheKeys, CancellationToken token = default);

    ValueTask<T?> GetOrAddAsync(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, CancellationToken token = default);

    ValueTask<T?> GetOrAddAsync(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, TimeSpan expiration, CancellationToken token = default);

    ValueTask<T?> GetOrAddAsync(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CancellationToken token = default);

    ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<TState>(KeyValuePair<CacheKey, TState>[] entries, Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, CancellationToken token = default) where TState : notnull;

    ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<TState>(KeyValuePair<CacheKey, TState>[] entries, Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, TimeSpan expiration, CancellationToken token = default) where TState : notnull;

    ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<TState>(KeyValuePair<CacheKey, TState>[] entries, Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, DateTimeOffset expiration, CancellationToken token = default) where TState : notnull;

    ValueTask<bool> RemoveAsync(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<bool> RemoveAsync(CacheKey[] cacheKeys, CancellationToken token = default);

    ValueTask<bool> SetAsync(CacheKey cacheKey, T? value, CancellationToken token = default);

    ValueTask<bool> SetAsync(CacheKey cacheKey, T? value, TimeSpan expiration, CancellationToken token = default);

    ValueTask<bool> SetAsync(CacheKey cacheKey, T? value, DateTimeOffset expiration, CancellationToken token = default);

    ValueTask<bool> SetAsync(KeyValuePair<CacheKey, T?>[] keyValues, CancellationToken token = default);

    ValueTask<bool> SetAsync(KeyValuePair<CacheKey, T?>[] keyValues, TimeSpan expiration, CancellationToken token = default);

    ValueTask<bool> SetAsync(KeyValuePair<CacheKey, T?>[] keyValues, DateTimeOffset expiration, CancellationToken token = default);

    ValueTask<bool> TryAddAsync(CacheKey cacheKey, T? value, CancellationToken token = default);

    ValueTask<bool> TryAddAsync(CacheKey cacheKey, T? value, TimeSpan expiration, CancellationToken token = default);

    ValueTask<bool> TryAddAsync(CacheKey cacheKey, T? value, DateTimeOffset expiration, CancellationToken token = default);

    ValueTask<bool> RefreshAsync(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<bool> RefreshAsync(CacheKey cacheKey, TimeSpan expiration, CancellationToken token = default);

    ValueTask<bool> RefreshAsync(CacheKey cacheKey, DateTimeOffset expiration, CancellationToken token = default);

    ValueTask<bool> ContainsAsync(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<TimeSpan?> TimeToLiveAsync(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<DateTimeOffset?> ExpireTimeAsync(CacheKey cacheKey, CancellationToken token = default);
}
```

`ICache<T>` is the primary typed cache surface for single-value key/value caching. The type parameter `T` fixes the value type for the lifetime of the cache instance, which lets the library resolve `CachePolicy` by `typeof(T).FullName` and apply a single key strategy per cache. Every operation accepts a `CancellationToken` and returns a `ValueTask`, so callers integrate naturally into async pipelines without heap allocation in the hot path. Blocking forwarders (`Get`, `GetOrAdd`, `Set`, `TryAdd`, `Remove`, `Refresh`, `Contains`, `TimeToLive`, `ExpireTime`) live on `CacheSyncExtensions` for call sites that cannot use `await`; each blocks on the async member via `.AsTask().GetAwaiter().GetResult()`, so use them only where the thread-blocking cost is acceptable. `GetOrAdd` covers both the single-key generator shape and a key-only multi-key shape (see below).

The multi-key `GetOrAddAsync<TState>` overloads pair each key with an opaque caller state (`TState`) — a database id, a request object, whatever identifies the entry in the caller's own vocabulary. The generator is invoked at most once, with only the states of the entries that missed, and results come back keyed by state. These three overloads are **abstract** on `ICache<T>` — unlike the equivalent members on `ICache`, there is no default body, because `ICache<T>` has no `GetCacheEntriesAsync` and so cannot distinguish a genuine miss from a cached `null` inside a default implementation. A hand-written `ICache<T>` implementation (a test fake, typically) must add all three. There is no key-only convenience overload on the async surface: a caller whose keys are their own identity pairs each key with itself (`TState = CacheKey`). The blocking `CacheSyncExtensions.GetOrAdd` forwarder is the one place that accepts `CacheKey[]` directly and does that pairing for you.

`TryAddAsync` is the conditional-add (create-if-absent) member: it writes only when the key does not already exist, and returns `true` only to the caller that created it. On a Redis-backed cache it maps to StackExchange.Redis `When.NotExists` (`SET key value EX … NX`) — a single atomic round-trip, so exactly one caller across all nodes wins a given key. It is the primitive to reach for when you need at-most-once semantics keyed by something: a dedup marker, an idempotency key, a "who runs this job" election. See [`ICache.TryAddAsync`](#icache) for the full contract, including what a `false` return does and does not tell you.

> **Typical vs. power-user surface:** This is the standard typed surface. If you need to vary the value type or key per call rather than per cache instance, use [`ICache`](#icache) instead. `ICache<T>` and `ICache` are different shapes for different problems — neither is strictly more capable.

**Use this when:**

- You are caching a single value type (e.g. `UserProfile`, `TenantSettings`) and want compile-time safety.
- You want `CachePolicy` picked up automatically from configuration by `typeof(T).FullName`.
- You are writing application-layer code and want the simplest, most discoverable API.

**Don't use this when:**

- The value type or key structure varies per call — use [`ICache`](#icache) instead.
- You need hash-structured values (field maps inside a key) — use [`IHashCache<T>`](#ihashcachet) instead.
- You need to enumerate available providers or add a custom one at runtime — use [`ICacheFactory`](#icachefactory) directly.

**See also:** [`ICacheFactory`](#icachefactory), [`ICache`](#icache), [`IHashCache<T>`](#ihashcachet), [Quickstart](../quickstart.md), [Concepts](../concepts.md)

---

### `ICache`

**Namespace:** `UiPath.Caching`

```csharp
public interface ICache : IDisposable
{
    string Name { get; }

    ValueTask<T?> GetAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default);

    ValueTask<KeyValuePair<CacheKey, T?>[]> GetAsync<T>(CacheKey[] cacheKeys, CachePolicy? policy, CancellationToken token = default);

    ValueTask<ICacheEntry<T?>> GetCacheEntryAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default);

    ValueTask<KeyValuePair<CacheKey, ICacheEntry<T?>>[]> GetCacheEntriesAsync<T>(CacheKey[] cacheKeys, CachePolicy? policy, CancellationToken token = default);

    ValueTask<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, CachePolicy? policy, CancellationToken token = default);

    ValueTask<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default);

    ValueTask<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default);

    ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<T, TState>(KeyValuePair<CacheKey, TState>[] entries, Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, CachePolicy? policy, CancellationToken token = default)
        where TState : notnull
        => BatchGetOrAdd.RunAsync<T, TState>(this, entries, generator, (pairs, t) => SetAsync(pairs, policy, t), policy, token);

    ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<T, TState>(KeyValuePair<CacheKey, TState>[] entries, Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default)
        where TState : notnull
        => BatchGetOrAdd.RunAsync<T, TState>(this, entries, generator, (pairs, t) => SetAsync(pairs, expiration, policy, t), policy, token);

    ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<T, TState>(KeyValuePair<CacheKey, TState>[] entries, Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default)
        where TState : notnull
        => BatchGetOrAdd.RunAsync<T, TState>(this, entries, generator, (pairs, t) => SetAsync(pairs, expiration, policy, t), policy, token);

    ValueTask<bool> RemoveAsync<T>(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<bool> RemoveAsync<T>(CacheKey[] cacheKey, CancellationToken token = default);

    ValueTask<bool> SetAsync<T>(CacheKey cacheKey, T? value, CachePolicy? policy, CancellationToken token = default);

    ValueTask<bool> SetAsync<T>(CacheKey cacheKey, T? value, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default);

    ValueTask<bool> SetAsync<T>(CacheKey cacheKey, T? value, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default);

    ValueTask<bool> SetAsync<T>(KeyValuePair<CacheKey, T?>[] keyValues, CachePolicy? policy, CancellationToken token = default);

    ValueTask<bool> SetAsync<T>(KeyValuePair<CacheKey, T?>[] keyValues, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default);

    ValueTask<bool> SetAsync<T>(KeyValuePair<CacheKey, T?>[] keyValues, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default);

    ValueTask<bool> TryAddAsync<T>(CacheKey cacheKey, T? value, CachePolicy? policy, CancellationToken token = default);

    ValueTask<bool> TryAddAsync<T>(CacheKey cacheKey, T? value, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default);

    ValueTask<bool> TryAddAsync<T>(CacheKey cacheKey, T? value, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default);

    ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default);

    ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default);

    ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default);

    ValueTask<bool> ContainsAsync<T>(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<TimeSpan?> TimeToLiveAsync<T>(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<DateTimeOffset?> ExpireTimeAsync<T>(CacheKey cacheKey, CancellationToken token = default);
}
```

`ICache` is the dynamic-key, dynamic-type cache surface. Unlike `ICache<T>`, the value type is specified as a generic type argument on each method call rather than fixed at cache-creation time, and a `CachePolicy` can be supplied per call rather than resolved by `typeof(T).FullName`. It also exposes `GetCacheEntryAsync` for callers that need cache-entry metadata (hit/miss status, expiration) in addition to the value. `ICache` implements `IDisposable`, but instances returned by `ICacheFactory.CreateCache(...)` are provider-owned (typically singletons resolved through a `Lazy<>`); their lifetime is managed by the provider and the DI container, so callers should not dispose them per use.

### Expiration

`expiration` is **non-nullable** everywhere it appears on a write — `TimeSpan` or `DateTimeOffset`, never `TimeSpan?`/`DateTimeOffset?`. A caller with nothing to say about lifetime calls the overload that has no `expiration` parameter; a caller that passes one means it.

```csharp
// I want this lifetime.
await cache.SetAsync(key, order, TimeSpan.FromMinutes(5), policy, token);

// I have no opinion: resolve it from the policy, then the provider default.
await cache.SetAsync(key, order, policy, token);
```

That leaves one resolution chain with no redundant state in it:

| What the caller does | Lifetime used |
| --- | --- |
| passes `expiration` | exactly that value, no jitter |
| omits `expiration` | `CachePolicy.DistributedExpiration`, jittered by `CachePolicy.JitterMaxDuration` |
| omits it, policy has no TTL | the provider's `DefaultExpiration`, jittered |
| omits it, nothing configured | unbounded — `TimeSpan.MaxValue` / `DateTimeOffset.MaxValue`, which the providers store as "no TTL" |

Because the argument can no longer be `null`, there is nothing left for a meaningless value to mean, so it is rejected rather than absorbed: a duration that is not strictly positive, or a deadline at or before the cache's current time, raises `ArgumentOutOfRangeException` with `ParamName` `"expiration"` and nothing is written. `TimeSpan.MaxValue` and `DateTimeOffset.MaxValue` stay valid — they are how the providers spell "no TTL". `CacheExpiration` holds the guard if you need it in your own implementation. The no-op caches (`NullCache`, `NullHashCache`, `NullSetCache`) read no argument at all and so enforce nothing; they keep degrading to "caching is off, carry on".

Nullability stays where it means *inherit*: `CachePolicy.LocalExpiration` / `DistributedExpiration`, the providers' `DefaultExpiration`, and the lifetime fields on `HashCacheEntryOptions`. Reads stay nullable too — `TimeToLiveAsync` and `ExpireTimeAsync` return `null` for a key with no TTL.

Every policy-bearing member takes `policy` as a **required** parameter — there is no `= null` default on the interface. Call sites that do not want a per-call policy use the `CacheExtensions` overloads instead, which omit `policy` (and, where the interface pairs the two, `expiration`) and forward with `policy: null`:

```csharp
// Interface: policy is explicit.
await cache.GetAsync<Order>(key, policy, token);

// CacheExtensions: the same call without a policy.
await cache.GetAsync<Order>(key, token);
await cache.SetAsync(key, order, TimeSpan.FromMinutes(5), token);
```

Two reasons the interface is the strict surface. An implementation cannot silently disagree about what "no policy" means, because it never gets to declare a default. And the extensions only work if the interface is strict: instance members always beat extension members in overload resolution, so while the interface declared `policy = null` an applicable interface overload existed for every short call and the extension was never reached. With `policy` required there is exactly one way to spell each call. The extensions are pure forwarders with no behavior of their own, which is why they carry `[ExcludeFromCodeCoverage]` — the policy-bearing implementations are what the tests exercise.

The multi-key `GetOrAddAsync<T, TState>` overloads shown above pair each key with an opaque caller state (`TState`) and are **default interface methods** — each forwards to the shared `BatchGetOrAdd.RunAsync` machinery, so existing `ICache` implementations keep compiling without adding them. The generator is invoked at most once, only with the states of the entries that missed every cache layer, never with keys; results come back keyed by state, one entry per distinct requested state in first-occurrence order. Cache operations de-duplicate by `CacheKey`; results de-duplicate by state — when two states share a key the generator is asked once and the value is reported under both. Their parameter shape matches the single-key `GetOrAddAsync` exactly — `expiration` and `policy` required, `token` optional. `CacheExtensions` carries no token-positional forwarder for them: those extensions are pre-`CachePolicy` back-compat sugar, and this API predates nothing. There is no key-only convenience overload; a caller whose keys are their own identity pairs each key with itself (`TState = CacheKey`).

`TryAddAsync` writes only if the key is absent — StackExchange.Redis `When.NotExists` (`SET … NX`) on Redis-backed caches, in one atomic command with the TTL applied by the same write. Four points decide whether it fits your problem:

- **`false` is deliberately ambiguous.** It means "you did not create this key" — either it already existed, or the write could not be completed (backing store disconnected, write threw, or the value was a `null`/`default` that the cache has no way to represent). This is fail-closed by design: a caller treating `true` as "I own this key" is never wrongly told it won. The ambiguity is not recoverable: a serialization or command failure also returns `false`, with [`IConnectionState.IsConnected`](#iconnectionstate) still reporting healthy, and [`IDistributedLock.TryAcquireAsync`](#idistributedlock) conflates backend-unavailable with already-held in the same way. Design the `false` branch so that not proceeding is safe; if the two readings must be handled differently, the caller needs a primitive with a richer result than a `bool`.
- **It never deletes.** Where `SetAsync` removes the key when handed a `null` and `CacheNullValues` is off, `TryAddAsync` returns `false` and leaves the key untouched. With `CacheNullValues` on, a `null` claims the key via the cached-null sentinel.
- **It is a cache primitive, not a lock.** The entry expires on its own TTL, there is no ownership token, and any later `SetAsync`/`RemoveAsync` on the key ignores the claim. For mutual exclusion with a fencing token and explicit release, use [`IDistributedLock`](#idistributedlock).
- **An expiration that is not in the future is rejected**, not answered. `expiration` is non-nullable, so a non-positive duration or a deadline already past is a bad argument and raises `ArgumentOutOfRangeException` — reporting `false` would be indistinguishable from "somebody else holds the key". See [Expiration](#expiration).

Every provider runs the same sequence: take the local lock, probe the local tier, ask the L2, then populate the local tier on a win. Two tiers therefore contribute — the probe bounds exclusion at one winner per process, and the L2 decides how much further than that it reaches:

| Provider | Who decides | Scope of exclusion |
| --- | --- | --- |
| `Redis` | Redis (`SET … NX`) | Cross-node |
| `InMemoryRedis` | L2 Redis, after a local probe that reports a hit as a loss; L1 populated only after a win, best-effort, plus an invalidation broadcast | Cross-node |
| `InMemoryRedis`, L2 disconnected | Nobody — returns `false` | None (fails closed rather than granting a claim every node would also get) |
| `InMemory` | The local probe, since `NullCache` as the L2 tells every caller it added the key. Serialized by the local lock — taken regardless of `Lock.LocalLockEnabled`, since here it *is* the guarantee; a caller that cannot acquire it within `Lock.LocalLockTimeout` is told it lost | In-process only, and against other conditional adds only: `SetAsync` takes no lock, so a set interleaved between the probe and the write is overwritten by the claim |
| `NullCache` | Nobody — returns `true` for every caller | None |
| Any other provider whose L2 resolved to `NullCache` | Whatever that L2 answers, so `true` for every caller. Which tier arbitrates is stated by the provider composing the cache — only `InMemory` arbitrates locally; every other provider asks its L2 and takes the answer as given | None |

The L2 is asked through `ICache`, never classified by type or provider name, so a provider you register yourself participates on the same terms. Three consequences worth knowing:

- **A local hit is reported as a loss without asking the L2.** Fail-closed and a round-trip saved, but a local copy that outlived the shared one costs a win the L2 would have granted.
- **An L2 that arbitrates in-process only is trusted as though it arbitrated for every sharer,** so pointing a provider's distributed tier at something in-process narrows that provider's exclusion with it.
- **A local write the L1 declines does not deny the win.** A size-limited `IMemoryCache` drops an entry it cannot fit without throwing; denying the claim there would strand a key the L2 already granted. On `InMemory`, where the L1 copy is the only copy, a `SizeLimit` too small to hold the value therefore means every caller wins.

`NullCache.TryAddAsync` returns `true`, as its `SetAsync` does: the null store accepts every write and retains none, so no key can pre-exist and no caller loses the race — the same "caching is off, carry on" degradation the rest of that type applies. It provides no exclusion whatsoever, and it is what `ICacheFactory.CreateCache` falls back to when the requested provider is absent or has `Enabled=false`, so a mistyped or switched-off provider turns at-most-once into at-least-once. **Assert the provider you expect at startup** whenever the `true` branch runs a side effect that must not repeat:

```csharp
if (cacheFactory.CreateCache(KnownCacheProviderNames.Redis) is NullCache)
{
    throw new InvalidOperationException("Webhook dedup needs the Redis provider registered and Enabled.");
}
```

**Retries are safe here,** unlike the destructive reads in `UiPath.Caching.Queue`, so the `NX` write stays on the shared `Write` pipeline. That pipeline retries on exceptions only — a `false` reply is never retried — and in the one ambiguous case the retry changes nothing: if attempt 1 creates the key and its reply is lost, the retried attempt is refused by that key and reports `false`, which is exactly what the un-retried exception would have reported. Where the first attempt never reached Redis, the retry recovers the correct `true` instead. Contrast `SPOP` behind `ISetCache.PopAsync`, where a retry pops a *second* item and loses the first — which is why `RedisSetCacheOptions.ResilienceKeyName` exists and defaults to no pipeline.

The three overloads are **required** members of `ICache` and `ICache<T>`, not default interface methods: a probe followed by a write is not atomic, and no fallback body could stand in for one without either voiding the guarantee or hiding which stores can arbitrate. A hand-written implementation therefore states what its own store can do — and existing implementations must add the member, which is a source-breaking change for them. There is no multi-key overload — Redis has no atomic multi-key `NX`, and "all-or-nothing" versus "per-key" would be a coin flip on the caller's behalf.

> **Typical vs. power-user surface:** `ICache` is the power-user surface. For most application code where the value type is fixed and you want automatic policy resolution, prefer [`ICache<T>`](#icachet) — it is simpler and less error-prone.

**Use this when:**

- The value type or key varies per call rather than per cache instance (e.g. a generic caching middleware that handles multiple types).
- You need to pass a `CachePolicy` constructed at call time rather than looked up from configuration.
- You need `GetCacheEntryAsync` to inspect cache-entry metadata (source layer, expiration) alongside the value.

**Don't use this when:**

- You are writing application-layer code with a fixed value type — use [`ICache<T>`](#icachet) for compile-time safety and automatic policy resolution.
- You need hash-structured values — use [`IHashCache`](#ihashcache) instead.

**See also:** [`ICache<T>`](#icachet), [`IHashCache`](#ihashcache), [`ICacheFactory`](#icachefactory), [Concepts](../concepts.md)

---

### `IHashCache<T>`

**Namespace:** `UiPath.Caching`

```csharp
public interface IHashCache<T>
{
    string Name { get; }

    ValueTask<T?> GetItemAsync(CacheKey cacheKey, string field, CancellationToken token = default);

    ValueTask<IDictionary<string, T?>> GetAsync(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<IDictionary<string, T?>> GetAsync(CacheKey cacheKey, string[] fields, CancellationToken token = default);

    ValueTask<IDictionary<string, T?>> GetOrAddAsync(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, CancellationToken token = default);

    ValueTask<IDictionary<string, T?>> GetOrAddAsync(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan expiration, CancellationToken token = default);

    ValueTask<IDictionary<string, T?>> GetOrAddAsync(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, CancellationToken token = default);

    ValueTask<ICacheEntry<IDictionary<string, T?>>> GetCacheEntryAsync(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<bool> SetAsync(CacheKey cacheKey, IDictionary<string, T?> values, CancellationToken token = default);

    ValueTask<bool> SetAsync(CacheKey cacheKey, IDictionary<string, T?> values, TimeSpan expiration, CancellationToken token = default);

    ValueTask<bool> SetAsync(CacheKey cacheKey, IDictionary<string, T?> values, DateTimeOffset expiration, CancellationToken token = default);

    ValueTask<bool> SetAsync(CacheKey cacheKey, IDictionary<string, T?> values, HashCacheEntryOptions options, CancellationToken token = default);

    ValueTask<bool> RefreshAsync(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<bool> RefreshAsync(CacheKey cacheKey, TimeSpan expiration, CancellationToken token = default);

    ValueTask<bool> RefreshAsync(CacheKey cacheKey, DateTimeOffset expiration, CancellationToken token = default);

    ValueTask<bool> RefreshAsync(CacheKey cacheKey, HashCacheEntryOptions options, CancellationToken token = default);

    ValueTask<bool> RemoveAsync(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<bool> ContainsAsync(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<TimeSpan?> TimeToLiveAsync(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<DateTimeOffset?> ExpireTimeAsync(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<IDictionary<string, string?>?> GetMetadataAsync(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<bool> SetMetadataAsync(CacheKey cacheKey, IDictionary<string, string?> metadata, CancellationToken token = default);
}
```

`IHashCache<T>` is the typed hash-cache surface. Each cache key maps to a dictionary of named fields rather than a single value — the backing store is a Redis hash (or an in-memory equivalent). `GetItemAsync` retrieves a single field by name; `GetAsync` retrieves all fields or a subset. `SetAsync` accepts a `HashCacheEntryOptions` overload for per-write control of expiration, metadata, and write scope — `HashCacheSetOption.HashReplace` merges the given fields into the existing hash, `KeyReplace` drops the key first so the written fields are the whole hash. Note that this is write *scope*, not a precondition: the hash surface has no conditional-add member, and `TryAddAsync` exists only on [`ICache`](#icache) / [`ICache<T>`](#icachet). Metadata (`GetMetadataAsync` / `SetMetadataAsync`) provides a side-channel string dictionary attached to the same key, useful for audit or versioning data. Blocking forwarders (`Get`, `GetItem`, `GetOrAdd`, `Set`, `Refresh`, `Remove`, `Contains`, etc.) live on `HashCacheSyncExtensions`, each blocking on the async member via `.AsTask().GetAwaiter().GetResult()`.

> **Typical vs. power-user surface:** `IHashCache<T>` is the standard typed hash surface. If you need to vary the value type per call, use [`IHashCache`](#ihashcache) instead. The two surfaces are different shapes for different problems.

**Use this when:**

- Your cached entity is naturally a keyed set of fields (e.g. per-tenant feature flags, a user-attribute bag) that you want to retrieve or write atomically or partially.
- You need to fetch only a subset of fields to avoid deserializing the full object.
- You want compile-time type safety and automatic `CachePolicy` resolution for the value type.

**Don't use this when:**

- You are caching a single serialized value per key — use [`ICache<T>`](#icachet) instead.
- The value type varies per call — use [`IHashCache`](#ihashcache) instead.
- You need per-field independent expiration beyond what `HashCacheEntryOptions` provides.

**See also:** [`IHashCache`](#ihashcache), [`ICache<T>`](#icachet), [`ICacheFactory`](#icachefactory), [How-to: hash cache](../how-to/hash-cache.md)

---

### `IHashCache`

**Namespace:** `UiPath.Caching`

```csharp
public interface IHashCache : IDisposable
{
    string Name { get; }

    ValueTask<T?> GetItemAsync<T>(CacheKey cacheKey, string field, CachePolicy? policy, CancellationToken token = default);

    ValueTask<IDictionary<string, T?>> GetAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default);

    ValueTask<IDictionary<string, T?>> GetAsync<T>(CacheKey cacheKey, string[] fields, CachePolicy? policy, CancellationToken token = default);

    ValueTask<ICacheEntry<IDictionary<string, T?>>> GetCacheEntryAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default);

    ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, CachePolicy? policy, CancellationToken token = default);

    ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default);

    ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default);

    ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, HashCacheSetOption? setOption, CachePolicy? policy, CancellationToken token = default);

    ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, CachePolicy? policy, CancellationToken token = default);

    ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default);

    ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default);

    ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, HashCacheEntryOptions options, CachePolicy? policy, CancellationToken token = default);

    ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default);

    ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default);

    ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default);

    ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, HashCacheEntryOptions options, CachePolicy? policy, CancellationToken token = default);

    ValueTask<bool> RemoveAsync<T>(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<bool> ContainsAsync<T>(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<TimeSpan?> TimeToLiveAsync<T>(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<DateTimeOffset?> ExpireTimeAsync<T>(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<IDictionary<string, string?>?> GetMetadataAsync<T>(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<bool> SetMetadataAsync<T>(CacheKey cacheKey, IDictionary<string, string?> metadata, CancellationToken token = default);
}
```

`IHashCache` is the dynamic-type hash-cache surface. Like [`ICache`](#icache), the value type is specified per method call as a generic type argument, and a `CachePolicy` may be supplied at call time. It extends hash semantics with `GetCacheEntryAsync` for cache-entry metadata inspection. The extra `GetOrAddAsync` overload that accepts `HashCacheSetOption` enables conditional-set semantics (e.g. set-if-not-exists) at the call site. `IHashCache` implements `IDisposable`, but instances returned by `ICacheFactory.CreateHashCache(...)` are provider-owned (typically singletons resolved through a `Lazy<>`); their lifetime is managed by the provider and the DI container, so callers should not dispose them per use.

As on [`ICache`](#icache), `policy` is a **required** parameter on every policy-bearing member; `HashCacheExtensions` supplies the no-policy overloads and forwards with `policy: null`.

> **Typical vs. power-user surface:** `IHashCache` is the power-user hash surface. For most application code with a fixed value type, prefer [`IHashCache<T>`](#ihashcachet) for compile-time safety and automatic policy resolution. The two surfaces are different shapes for different problems.

**Use this when:**

- You are building infrastructure-layer code (middleware, generic adapters) where the hash value type varies per call.
- You need to pass a `CachePolicy` constructed dynamically at call time.
- You need `GetCacheEntryAsync` for metadata inspection alongside hash values.

**Don't use this when:**

- You are writing application-layer code with a fixed value type — use [`IHashCache<T>`](#ihashcachet) instead.
- You need single-value (non-hash) caching — use [`ICache`](#icache) or [`ICache<T>`](#icachet) instead.

**See also:** [`IHashCache<T>`](#ihashcachet), [`ICache`](#icache), [`ICacheFactory`](#icachefactory), [How-to: hash cache](../how-to/hash-cache.md)

---

## Factory surface

### `ICacheFactory`

**Namespace:** `UiPath.Caching`

```csharp
public interface ICacheFactory : IDisposable
{
    IEnumerable<string> ProviderNames { get; }

    ICachePolicyFactory? PolicyFactory => null;

    ICache CreateCache(string? providerName = null);

    IHashCache CreateHashCache(string? providerName = null);

    void AddProvider(ICacheProvider provider);
}
```

`ICacheFactory` is the entry point for obtaining cache instances. `CreateCache` and `CreateHashCache` return the untyped `ICache` / `IHashCache` interfaces; the typed `ICache<T>` / `IHashCache<T>` wrappers are obtained through the `ICacheFactory` extension methods (`CreateCache<T>`, `CreateHashCache<T>`) defined in the `UiPath.Caching` namespace. When `providerName` is omitted, the factory uses the default registered provider. `AddProvider` supports registering additional provider implementations at runtime, for example in test fixtures or multi-tenant scenarios where provider instances are created dynamically.

`PolicyFactory` is nullable with a default interface implementation of `null` — existing `ICacheFactory` implementors do not need to add anything to keep compiling. `Cache<T>` / `HashCache<T>` ctors prefer the DI-registered `ICachePolicyFactory` (injected via the optional `policyFactory` parameter) and fall back to `cacheFactory.PolicyFactory`. A custom factory that returns `null` here still gets configured policy resolution applied to its caches via DI.

**Use this when:**

- You need to instantiate a cache object at the start of a service or request scope, typically once in a constructor or `IHostedService.StartAsync`.
- You need to enumerate or switch between named providers at runtime.
- You are writing a test that registers a custom in-memory provider via `AddProvider`.

**Don't use this when:**

- You already hold an `ICache<T>` or `IHashCache<T>` injected by DI — there is no need to call the factory again.
- You need broadcast (pub/sub) functionality — use [`ITopicFactory`](#itopicfactory) instead.

**See also:** [`ICacheProvider`](../concepts.md), [`ITopicFactory`](#itopicfactory), [Quickstart](../quickstart.md), [Concepts](../concepts.md)

---

### `ICachePolicyFactory`

**Namespace:** `UiPath.Caching`

```csharp
public interface ICachePolicyFactory
{
    CachePolicy? Resolve(string policyName);

    CachePolicy? Default { get; }

    IEnumerable<string> Keys { get; }
}
```

`ICachePolicyFactory` resolves per-cache `CachePolicy` instances by name. `Cache<T>` / `HashCache<T>` look up `typeof(T).FullName` (or an explicit override) at construction and bind the result for the lifetime of the cache wrapper.

- **`Resolve(name)`** returns the pre-merged named policy (`Policies[name]` merged with `Default`), or `null` when the name is absent from the configured set. Cache implementations treat `null` as "fall back to this cache instance's effective default" (provider-specific options merged with `Default`, with hardcoded fallbacks for lock fields).
- **`Default`** is the user-configured `CacheOptions.DefaultCachePolicy`. Nullable: `null` means "no app-wide override; each provider supplies its own defaults." `MultilayerCacheBase` merges this with the provider snapshot at construction to produce its effective default; consumers shouldn't read `Default` directly when computing TTLs.
- **`Keys`** enumerates configured policy names so validators (and replacement factories) can walk the registered set. The default implementation returns the underlying `CacheOptions.Policies` dictionary keys.

**Use this when:**

- You're implementing a custom `ICache<T>` wrapper outside the library's `Cache<T>` base and need to resolve a policy by name.
- You're writing a custom `ICachePolicyFactory` that needs to participate in the factory-level validation pipeline (`CachePolicyFactoryValidator.Validate(factory, distributedLockPollInterval)` walks `Keys` + `Resolve` + `Default`).

**Don't use this when:**

- You're a regular cache consumer — `ICache<T>` already resolves its policy at construction. Don't call `Resolve` on every request.
- You need to mutate or refresh policies at runtime — the default implementation snapshots at startup. Replace the factory via `builder.UseCachePolicyFactory<T>()` if you need dynamic resolution; see [how-to/extending.md#swapping-the-default-factories](../how-to/extending.md#swapping-the-default-factories).

**See also:** [`ICacheFactory.PolicyFactory`](#icachefactory), [how-to/extending.md](../how-to/extending.md), [Concepts — Policies](../concepts.md#policies)

---

### `ITopicFactory`

**Namespace:** `UiPath.Caching.Broadcast`

```csharp
public interface ITopicFactory
{
    IEnumerable<string> ProviderNames { get; }

    ITopicProvider Get(string? providerName = null);

    void AddProvider(ITopicProvider provider);
}
```

`ITopicFactory` is the entry point for the broadcast (pub/sub) subsystem. `Get` returns an `ITopicProvider` bound to the named provider (or the default provider when `providerName` is omitted), from which consumers obtain individual `ITopic` channels. Like `ICacheFactory`, `AddProvider` supports registering custom provider instances at runtime. The broadcast subsystem is independent of the cache subsystem: a process can use topics without using caches, and vice versa.

**Use this when:**

- You need to publish or subscribe to cache-invalidation events or custom application events across nodes.
- You need to switch between multiple topic providers (e.g. in-memory for tests, Redis Streams for production).
- You are writing infrastructure code that needs to enumerate or plug in topic providers dynamically.

**Don't use this when:**

- You only need key/value or hash caching with no pub/sub — use [`ICacheFactory`](#icachefactory) instead.
- You already hold an `ITopic` or `ITopicProvider` injected by DI — there is no need to call the factory again.

**See also:** [`ICacheFactory`](#icachefactory), [How-to: broadcast](../how-to/broadcast.md), [Concepts](../concepts.md)

---

## Key-strategy seams

### `ICacheKeyStrategy`

**Namespace:** `UiPath.Caching`

```csharp
public interface ICacheKeyStrategy
{
    CacheKey GetCacheKey<T>(CacheKey key);
}
```

`ICacheKeyStrategy` is the pluggable seam for transforming a caller-supplied `CacheKey` into the key actually stored in the backing store. The type parameter `T` carries the cached value type so that implementations can inject the type name, tenant identifier, or other ambient context into the final key. The default implementation prefixes with `typeof(T).FullName`. Custom implementations are registered in `ICachingBuilder` and apply globally to all caches built from that configuration.

**Use this when:**

- You need to namespace keys by tenant, region, or schema version without modifying every call site.
- You need to inject ambient context (e.g. tenant ID from an ambient principal) into cache keys.
- You are writing a multi-tenant service that shares a single Redis instance across tenants.

**Don't use this when:**

- You only need per-call key customization — pass a qualified `CacheKey` value directly at the call site instead.
- You need different key strategies per topic (pub/sub) — see [`IRedisStreamKeyStrategy`](#iredisstreamkeystrategy) and [`IRedisChannelStrategy`](#iredischannelstrategy).

**See also:** [`IDistributedLockKeyStrategy`](#idistributedlockkeystrategy), [How-to: telemetry and strategies](../how-to/telemetry-and-strategies.md), [Concepts](../concepts.md)

---

### `IRedisStreamKeyStrategy`

**Namespace:** `UiPath.Caching.Broadcast.Redis`

```csharp
public interface IRedisStreamKeyStrategy
{
    RedisKey GetRedisKey(TopicKey topicKey);
}
```

`IRedisStreamKeyStrategy` controls how a logical `TopicKey` maps to a Redis stream key. It is the stream-specific counterpart to `IRedisChannelStrategy` and is used exclusively by the Redis Streams broadcast provider. Implementing this interface allows callers to inject namespacing, environment prefixes, or tenant segments into the Redis key without changing topic-publish or topic-subscribe call sites.

**Use this when:**

- You are using the Redis Streams broadcast provider and need to customize how topic keys are mapped to Redis stream names (e.g. per-environment or per-tenant prefixing).
- You are writing integration tests that need to isolate stream keys between test runs.

**Don't use this when:**

- You are using the Redis Pub/Sub broadcast provider — use [`IRedisChannelStrategy`](#iredischannelstrategy) instead.
- You need to customize cache (not topic) key mapping — use [`ICacheKeyStrategy`](#icachekeystrategy) instead.

**See also:** [`IRedisChannelStrategy`](#iredischannelstrategy), [`ICacheKeyStrategy`](#icachekeystrategy), [How-to: broadcast](../how-to/broadcast.md)

---

### `IRedisChannelStrategy`

**Namespace:** `UiPath.Caching.Broadcast.Redis`

```csharp
public interface IRedisChannelStrategy
{
    RedisChannel GetRedisChannel(TopicKey topicKey);
}
```

`IRedisChannelStrategy` controls how a logical `TopicKey` maps to a Redis Pub/Sub channel name. It is the channel-specific counterpart to `IRedisStreamKeyStrategy` and is used exclusively by the Redis Pub/Sub broadcast provider. Custom implementations can inject environment prefixes, tenant segments, or any other ambient context into the channel name.

**Use this when:**

- You are using the Redis Pub/Sub broadcast provider and need to customize channel name derivation.
- You need environment-level or tenant-level isolation of pub/sub channels on a shared Redis instance.

**Don't use this when:**

- You are using the Redis Streams broadcast provider — use [`IRedisStreamKeyStrategy`](#iredisstreamkeystrategy) instead.
- You need to customize cache key mapping — use [`ICacheKeyStrategy`](#icachekeystrategy) instead.

**See also:** [`IRedisStreamKeyStrategy`](#iredisstreamkeystrategy), [`ICacheKeyStrategy`](#icachekeystrategy), [How-to: broadcast](../how-to/broadcast.md)

---

### `IDistributedLockKeyStrategy`

**Namespace:** `UiPath.Caching.Locking`

```csharp
public interface IDistributedLockKeyStrategy
{
    string GetLockKey(CacheKey cacheKey);
}
```

`IDistributedLockKeyStrategy` controls how a `CacheKey` maps to the string key used to acquire a distributed lock. The default implementation derives the lock key from the cache key using the same namespacing conventions as `ICacheKeyStrategy`. Custom implementations allow injecting tenant context, adding lock-specific prefixes, or scoping locks to a region to prevent cross-tenant lock contention on a shared Redis instance.

**Use this when:**

- You need distributed lock keys to carry tenant or environment namespacing that differs from the default derivation.
- You need to prevent lock key collisions between tenants or environments sharing a single Redis instance.

**Don't use this when:**

- You need to customize cache (not lock) key mapping — use [`ICacheKeyStrategy`](#icachekeystrategy) instead.
- You are using only in-process locking — [`ILocalLock`](#ilocallock) does not use this strategy.

**See also:** [`ICacheKeyStrategy`](#icachekeystrategy), [`IDistributedLock`](#idistributedlock), [How-to: telemetry and strategies](../how-to/telemetry-and-strategies.md)

---

## Lock seams

### `ILocalLock`

**Namespace:** `UiPath.Caching.Locking`

```csharp
public interface ILocalLock
{
    ValueTask<IDisposable> AcquireAsync(string key, CancellationToken token);
}
```

`ILocalLock` is the in-process mutual-exclusion seam used internally by the cache runtime to serialize concurrent `GetOrAdd` calls for the same key within a single process. The returned `IDisposable` lease releases the lock when disposed. Because the lock is in-process only, it does not protect against concurrent writes from multiple nodes — for cross-node coordination, see [`IDistributedLock`](#idistributedlock). Consumers rarely need to call `ILocalLock` directly; the runtime acquires it automatically when `CachePolicy.LocalLockEnabled` is set.

**Use this when:**

- You are implementing a custom cache provider and need to plug in a different in-process lock mechanism (e.g. a `SemaphoreSlim`-backed implementation for testing).
- You need to instrument or mock in-process lock acquisition in integration tests.

**Don't use this when:**

- You need cross-node locking — use [`IDistributedLock`](#idistributedlock) instead.
- You are writing application code — configure `CachePolicy.LocalLockEnabled = true` and let the runtime manage `ILocalLock` automatically.

**See also:** [`IDistributedLock`](#idistributedlock), [Reference: settings](settings.md), [Concepts](../concepts.md)

---

### `IDistributedLock`

**Namespace:** `UiPath.Caching.Locking`

```csharp
public interface IDistributedLock
{
    ValueTask<IAsyncDisposable> AcquireAsync(string key, TimeSpan expiry, TimeSpan waitTimeout, CancellationToken token);

    ValueTask<IAsyncDisposable?> TryAcquireAsync(string key, TimeSpan expiry, CancellationToken token) =>
        new(default(IAsyncDisposable));
}
```

`IDistributedLock` is the cross-node mutual-exclusion seam. `AcquireAsync` blocks until the lock is acquired or `waitTimeout` elapses (throwing on timeout), and returns a lease that releases the lock when disposed asynchronously. `TryAcquireAsync` is the non-blocking variant: it returns `null` immediately if the lock is already held or the backend is unavailable, rather than waiting. The default `TryAcquireAsync` implementation returns `null` so external implementers degrade safely; real distributed-lock backends (e.g. Redis-based) should override both methods. Consumers rarely call this interface directly — the runtime acquires it when `CachePolicy.DistributedLockEnabled` is set.

**Use this when:**

- You are implementing a custom distributed lock backend (e.g. based on Redis `SET NX`, Redlock, or a database).
- You need to distinguish "lock acquired" from "lock not acquired" without blocking — use `TryAcquireAsync` directly from infrastructure-layer code that handles the `null` case explicitly.
- You are writing integration tests that need to simulate lock contention.

**Don't use this when:**

- You only need in-process coordination — use [`ILocalLock`](#ilocallock) instead.
- You are writing application code — configure `CachePolicy.DistributedLockEnabled = true` and let the runtime manage `IDistributedLock` automatically.

**See also:** [`ILocalLock`](#ilocallock), [`IDistributedLockKeyStrategy`](#idistributedlockkeystrategy), [Reference: settings](settings.md), [Concepts](../concepts.md)

---

## Connection state

### `IConnectionState`

```csharp
public interface IConnectionState
{
    event EventHandler? OnConnectionFailed;

    event EventHandler? OnConnectionRestored;

    event EventHandler? OnReconnected;

    bool IsConnected { get; }
}
```

A non-blocking snapshot of whether the backing store is reachable, plus the transitions as events. `IsConnected` never blocks and never throws. Implemented by `RedisCacheBase`, so `Redis`-provider caches expose it; the multilayer caches and `NullCache` do not, and neither does `Cache<T>`, which holds its underlying `ICache` privately.

What it is *not*: a way to explain a negative result. It is a cached snapshot refreshed on connection events and a timer, it says nothing about whether any particular command succeeded, and it is `true` both where there is nothing to disconnect from and where `ConnectionMonitorEnabled` is off. A `false` from `SetAsync` or [`TryAddAsync`](#icache) can perfectly well coincide with `IsConnected == true` — a serialization failure or a rejected command does that — so reading it afterwards does not recover why the call failed.

**Use this when:** you are reporting or reacting to cache *health* — a readiness probe, a metric, a log line, or backing off writes while a tier is known down. Subscribe to `OnConnectionFailed` / `OnConnectionRestored` for the transitions rather than polling.

## Telemetry seam

### `ICachingTelemetryProvider`

**Namespace:** `UiPath.Caching.Telemetry`

```csharp
public interface ICachingTelemetryProvider
{
    ITelemetryOperation StartOperation(string providerName, Type cacheObject, string methodName = "");

    void TrackDependency(string type, string target, string name, string data,
        DateTimeOffset startTime, TimeSpan duration, string resultCode, bool success,
        ReadOnlySpan<KeyValuePair<string, string>> properties = default,
        ReadOnlySpan<KeyValuePair<string, double>> metrics = default);

    void TrackEvent(string eventName,
        ReadOnlySpan<KeyValuePair<string, string>> properties = default,
        ReadOnlySpan<KeyValuePair<string, double>> metrics = default);

    void TrackException(Exception ex,
        ReadOnlySpan<KeyValuePair<string, string>> properties = default,
        ReadOnlySpan<KeyValuePair<string, double>> metrics = default);

    void TrackMetric(string name, double value,
        ReadOnlySpan<KeyValuePair<string, string>> properties = default);
}
```

`ICachingTelemetryProvider` is the single seam through which the cache runtime emits all observability signals: dependency traces, custom events, exceptions, and metrics. The `properties` and `metrics` parameters use `ReadOnlySpan<KeyValuePair<...>>` — a zero-allocation, stack-allocated tag list — to avoid heap pressure on hot paths. Default no-op implementations are provided for all methods so implementers can override only the signals they care about. `StartOperation` returns an `ITelemetryOperation` scope that wraps a dependency trace; the runtime calls it automatically around each cache operation.

**Use this when:**

- You are integrating with an observability backend (OpenTelemetry, Datadog, a structured logging pipeline) and need to route cache telemetry into that backend.
- You are writing a custom telemetry adapter that forwards signals to multiple sinks.
- You need to mock or capture telemetry in unit or integration tests.

**Don't use this when:**

- You want to configure *which* cache operations emit telemetry — that is controlled via `CachePolicy` settings and the `ICachingBuilder` configuration, not this interface.
- You are consuming cache telemetry from an external monitoring dashboard — no code change is needed; register the appropriate `ICachingTelemetryProvider` implementation once in DI.

**See also:** [How-to: telemetry and strategies](../how-to/telemetry-and-strategies.md), [Concepts](../concepts.md), [Reference: settings](settings.md)

---

## DI builder

### `ICachingBuilder`

**Namespace:** `UiPath.Caching.Config`

```csharp
public interface ICachingBuilder
{
    IServiceCollection Services { get; }

    IConfiguration Configuration { get; }

    bool Enabled { get; set; }

    void RegisterOnCompleteCallback(object key, Action<ICachingBuilder> callback);
}
```

`ICachingBuilder` is the fluent configuration handle passed to the `services.AddCaching(...)` lambda. It exposes the `IServiceCollection` and `IConfiguration` so the builder extensions that ship in the library — `AddRedisConnection()`, `AddBroadcast()`, `AddRedis()`, `AddInMemoryRedis()`, `AddMemory()`, `AddResilienceStrategies()`, `AddCloudEvents()`, `AddOpenTelemetry()`, `AddRedisDistributedLock()`, `AddLocalLock()` — can register services, bind options, and wire up providers against the same DI container. `Enabled` acts as a feature flag — setting it to `false` causes the builder to skip provider registration, which is useful for conditional configuration (e.g. disabling caching in integration-test hosts). `RegisterOnCompleteCallback` defers arbitrary builder actions until all `AddCaching` calls in the startup chain have run, allowing later registrations to override earlier ones without ordering constraints.

**Use this when:**

- You are writing a caching extension library or plugin that needs to register additional services when caching is configured (e.g. a custom telemetry adapter, a custom key strategy).
- You need to conditionally disable the caching subsystem from a test fixture or feature-flag-driven startup.
- You need to defer a registration action until all caching providers have been added.

**Don't use this when:**

- You are writing application code that only consumes `ICache<T>` or `IHashCache<T>` — you do not interact with `ICachingBuilder` directly; `AddCaching` handles it.
- You need to inspect or modify cache behavior at request time — `ICachingBuilder` operates at startup only.

**See also:** [`ICacheFactory`](#icachefactory), [Quickstart](../quickstart.md), [Reference: settings](settings.md), [Concepts](../concepts.md)
