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

**Namespace:** `UiPath.Platform.Caching`

```csharp
public partial interface ICache<T>
{
    string Name { get; }

    ValueTask<T?> GetAsync(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<KeyValuePair<CacheKey, T?>[]> GetAsync(CacheKey[] cacheKeys, CancellationToken token = default);

    ValueTask<T?> GetOrAddAsync(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, CancellationToken token = default);

    ValueTask<T?> GetOrAddAsync(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, TimeSpan? expiration, CancellationToken token = default);

    ValueTask<T?> GetOrAddAsync(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, DateTimeOffset? expiration, CancellationToken token = default);

    ValueTask<bool> RemoveAsync(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<bool> RemoveAsync(CacheKey[] cacheKeys, CancellationToken token = default);

    ValueTask<bool> SetAsync(CacheKey cacheKey, T? value, CancellationToken token = default);

    ValueTask<bool> SetAsync(CacheKey cacheKey, T? value, TimeSpan? expiration, CancellationToken token = default);

    ValueTask<bool> SetAsync(CacheKey cacheKey, T? value, DateTimeOffset? expiration, CancellationToken token = default);

    ValueTask<bool> SetAsync(KeyValuePair<CacheKey, T?>[] keyValues, CancellationToken token = default);

    ValueTask<bool> SetAsync(KeyValuePair<CacheKey, T?>[] keyValues, TimeSpan? expiration = null, CancellationToken token = default);

    ValueTask<bool> SetAsync(KeyValuePair<CacheKey, T?>[] keyValues, DateTimeOffset? expiration = null, CancellationToken token = default);

    ValueTask<bool> RefreshAsync(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<bool> RefreshAsync(CacheKey cacheKey, TimeSpan? expiration, CancellationToken token = default);

    ValueTask<bool> RefreshAsync(CacheKey cacheKey, DateTimeOffset? expiration, CancellationToken token = default);

    ValueTask<bool> ContainsAsync(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<TimeSpan?> TimeToLiveAsync(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<DateTimeOffset?> ExpireTimeAsync(CacheKey cacheKey, CancellationToken token = default);
}
```

`ICache<T>` is the primary typed cache surface for single-value key/value caching. The type parameter `T` fixes the value type for the lifetime of the cache instance, which lets the library resolve `CachePolicy` by `typeof(T).FullName` and apply a single key strategy per cache. Every operation accepts a `CancellationToken` and returns a `ValueTask`, so callers integrate naturally into async pipelines without heap allocation in the hot path. Sync overloads (`Get`, `GetOrAdd`, `Set`, `Remove`, `Refresh`, `Contains`, `TimeToLive`, `ExpireTime`) are provided as blocking default interface methods for call sites that cannot use `await`.

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

**Namespace:** `UiPath.Platform.Caching`

```csharp
public partial interface ICache : IDisposable
{
    string Name { get; }

    ValueTask<T?> GetAsync<T>(CacheKey cacheKey, CachePolicy? policy = null, CancellationToken token = default);

    ValueTask<KeyValuePair<CacheKey, T?>[]> GetAsync<T>(CacheKey[] cacheKeys, CachePolicy? policy = null, CancellationToken token = default);

    ValueTask<ICacheEntry<T?>> GetCacheEntryAsync<T>(CacheKey cacheKey, CachePolicy? policy = null, CancellationToken token = default);

    ValueTask<KeyValuePair<CacheKey, ICacheEntry<T?>>[]> GetCacheEntriesAsync<T>(CacheKey[] cacheKeys, CachePolicy? policy = null, CancellationToken token = default);

    ValueTask<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, CachePolicy? policy = null, CancellationToken token = default);

    ValueTask<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, TimeSpan? expiration = null, CachePolicy? policy = null, CancellationToken token = default);

    ValueTask<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, DateTimeOffset? expiration = null, CachePolicy? policy = null, CancellationToken token = default);

    ValueTask<bool> RemoveAsync<T>(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<bool> RemoveAsync<T>(CacheKey[] cacheKey, CancellationToken token = default);

    ValueTask<bool> SetAsync<T>(CacheKey cacheKey, T? value, CachePolicy? policy = null, CancellationToken token = default);

    ValueTask<bool> SetAsync<T>(CacheKey cacheKey, T? value, TimeSpan? expiration = null, CachePolicy? policy = null, CancellationToken token = default);

    ValueTask<bool> SetAsync<T>(CacheKey cacheKey, T? value, DateTimeOffset? expiration = null, CachePolicy? policy = null, CancellationToken token = default);

    ValueTask<bool> SetAsync<T>(KeyValuePair<CacheKey, T?>[] keyValues, CachePolicy? policy = null, CancellationToken token = default);

    ValueTask<bool> SetAsync<T>(KeyValuePair<CacheKey, T?>[] keyValues, TimeSpan? expiration = null, CachePolicy? policy = null, CancellationToken token = default);

    ValueTask<bool> SetAsync<T>(KeyValuePair<CacheKey, T?>[] keyValues, DateTimeOffset? expiration = null, CachePolicy? policy = null, CancellationToken token = default);

    ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, CachePolicy? policy = null, CancellationToken token = default);

    ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, TimeSpan? expiration = null, CachePolicy? policy = null, CancellationToken token = default);

    ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, DateTimeOffset? expiration = null, CachePolicy? policy = null, CancellationToken token = default);

    ValueTask<bool> ContainsAsync<T>(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<TimeSpan?> TimeToLiveAsync<T>(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<DateTimeOffset?> ExpireTimeAsync<T>(CacheKey cacheKey, CancellationToken token = default);
}
```

`ICache` is the dynamic-key, dynamic-type cache surface. Unlike `ICache<T>`, the value type is specified as a generic type argument on each method call rather than fixed at cache-creation time, and a `CachePolicy` can be supplied per call rather than resolved by `typeof(T).FullName`. It also exposes `GetCacheEntryAsync` for callers that need cache-entry metadata (hit/miss status, expiration) in addition to the value. `ICache` implements `IDisposable`, but instances returned by `ICacheFactory.CreateCache(...)` are provider-owned (typically singletons resolved through a `Lazy<>`); their lifetime is managed by the provider and the DI container, so callers should not dispose them per use.

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

**Namespace:** `UiPath.Platform.Caching`

```csharp
public partial interface IHashCache<T>
{
    string Name { get; }

    ValueTask<T?> GetItemAsync(CacheKey cacheKey, string field, CancellationToken token = default);

    ValueTask<IDictionary<string, T?>> GetAsync(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<IDictionary<string, T?>> GetAsync(CacheKey cacheKey, string[] fields, CancellationToken token = default);

    ValueTask<IDictionary<string, T?>> GetOrAddAsync(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, CancellationToken token = default);

    ValueTask<IDictionary<string, T?>> GetOrAddAsync(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan? expiration, CancellationToken token = default);

    ValueTask<IDictionary<string, T?>> GetOrAddAsync(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset? expiration, CancellationToken token = default);

    ValueTask<ICacheEntry<IDictionary<string, T?>>> GetCacheEntryAsync(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<bool> SetAsync(CacheKey cacheKey, IDictionary<string, T?> values, CancellationToken token = default);

    ValueTask<bool> SetAsync(CacheKey cacheKey, IDictionary<string, T?> values, TimeSpan? expiration, CancellationToken token = default);

    ValueTask<bool> SetAsync(CacheKey cacheKey, IDictionary<string, T?> values, DateTimeOffset? expiration, CancellationToken token = default);

    ValueTask<bool> SetAsync(CacheKey cacheKey, IDictionary<string, T?> values, HashCacheEntryOptions options, CancellationToken token = default);

    ValueTask<bool> RefreshAsync(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<bool> RefreshAsync(CacheKey cacheKey, TimeSpan? expiration, CancellationToken token = default);

    ValueTask<bool> RefreshAsync(CacheKey cacheKey, DateTimeOffset? expiration, CancellationToken token = default);

    ValueTask<bool> RefreshAsync(CacheKey cacheKey, HashCacheEntryOptions options, CancellationToken token = default);

    ValueTask<bool> RemoveAsync(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<bool> ContainsAsync(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<TimeSpan?> TimeToLiveAsync(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<DateTimeOffset?> ExpireTimeAsync(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<IDictionary<string, string?>?> GetMetadataAsync(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<bool> SetMetadataAsync(CacheKey cacheKey, IDictionary<string, string?> metadata, CancellationToken token = default);
}
```

`IHashCache<T>` is the typed hash-cache surface. Each cache key maps to a dictionary of named fields rather than a single value — the backing store is a Redis hash (or an in-memory equivalent). `GetItemAsync` retrieves a single field by name; `GetAsync` retrieves all fields or a subset. `SetAsync` accepts a `HashCacheEntryOptions` overload for fine-grained per-write control (e.g. conditional set, individual field TTL). Metadata (`GetMetadataAsync` / `SetMetadataAsync`) provides a side-channel string dictionary attached to the same key, useful for audit or versioning data. Sync overloads (`Get`, `GetItem`, `GetOrAdd`, `Set`, `Refresh`, `Remove`, `Contains`, etc.) are provided as blocking default interface methods.

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

**Namespace:** `UiPath.Platform.Caching`

```csharp
public partial interface IHashCache : IDisposable
{
    string Name { get; }

    ValueTask<T?> GetItemAsync<T>(CacheKey cacheKey, string field, CachePolicy? policy = null, CancellationToken token = default);

    ValueTask<IDictionary<string, T?>> GetAsync<T>(CacheKey cacheKey, CachePolicy? policy = null, CancellationToken token = default);

    ValueTask<IDictionary<string, T?>> GetAsync<T>(CacheKey cacheKey, string[] fields, CachePolicy? policy = null, CancellationToken token = default);

    ValueTask<ICacheEntry<IDictionary<string, T?>>> GetCacheEntryAsync<T>(CacheKey cacheKey, CachePolicy? policy = null, CancellationToken token = default);

    ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, CachePolicy? policy = null, CancellationToken token = default);

    ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan? expiration = null, CachePolicy? policy = null, CancellationToken token = default);

    ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset? expiration = null, CachePolicy? policy = null, CancellationToken token = default);

    ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset? expiration = null, HashCacheSetOption? setOption = null, CachePolicy? policy = null, CancellationToken token = default);

    ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, CachePolicy? policy = null, CancellationToken token = default);

    ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, TimeSpan? expiration = null, CachePolicy? policy = null, CancellationToken token = default);

    ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, DateTimeOffset? expiration = null, CachePolicy? policy = null, CancellationToken token = default);

    ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, HashCacheEntryOptions options, CachePolicy? policy = null, CancellationToken token = default);

    ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, CachePolicy? policy = null, CancellationToken token = default);

    ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, TimeSpan? expiration = null, CachePolicy? policy = null, CancellationToken token = default);

    ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, DateTimeOffset? expiration = null, CachePolicy? policy = null, CancellationToken token = default);

    ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, HashCacheEntryOptions options, CachePolicy? policy = null, CancellationToken token = default);

    ValueTask<bool> RemoveAsync<T>(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<bool> ContainsAsync<T>(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<TimeSpan?> TimeToLiveAsync<T>(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<DateTimeOffset?> ExpireTimeAsync<T>(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<IDictionary<string, string?>?> GetMetadataAsync<T>(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<bool> SetMetadataAsync<T>(CacheKey cacheKey, IDictionary<string, string?> metadata, CancellationToken token = default);
}
```

`IHashCache` is the dynamic-type hash-cache surface. Like [`ICache`](#icache), the value type is specified per method call as a generic type argument, and a `CachePolicy` may be supplied at call time. It extends hash semantics with `GetCacheEntryAsync` for cache-entry metadata inspection. The extra `GetOrAddAsync` overload that accepts `HashCacheSetOption` enables conditional-set semantics (e.g. set-if-not-exists) at the call site. `IHashCache` implements `IDisposable`, but instances returned by `ICacheFactory.CreateHashCache(...)` are provider-owned (typically singletons resolved through a `Lazy<>`); their lifetime is managed by the provider and the DI container, so callers should not dispose them per use.

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

**Namespace:** `UiPath.Platform.Caching`

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

`ICacheFactory` is the entry point for obtaining cache instances. `CreateCache` and `CreateHashCache` return the untyped `ICache` / `IHashCache` interfaces; the typed `ICache<T>` / `IHashCache<T>` wrappers are obtained through the `ICacheFactory` extension methods (`CreateCache<T>`, `CreateHashCache<T>`) defined in the `UiPath.Platform.Caching` namespace. When `providerName` is omitted, the factory uses the default registered provider. `AddProvider` supports registering additional provider implementations at runtime, for example in test fixtures or multi-tenant scenarios where provider instances are created dynamically.

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

**Namespace:** `UiPath.Platform.Caching`

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

**Namespace:** `UiPath.Platform.Caching.Broadcast`

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

**Namespace:** `UiPath.Platform.Caching`

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

**Namespace:** `UiPath.Platform.Caching.Broadcast.Redis`

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

**Namespace:** `UiPath.Platform.Caching.Broadcast.Redis`

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

**Namespace:** `UiPath.Platform.Caching.Locking`

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

**Namespace:** `UiPath.Platform.Caching.Locking`

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

**Namespace:** `UiPath.Platform.Caching.Locking`

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

## Telemetry seam

### `ICachingTelemetryProvider`

**Namespace:** `UiPath.Platform.Caching.Telemetry`

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

- You are integrating with an observability backend (Application Insights, OpenTelemetry, Datadog) and need to route cache telemetry into that backend.
- You are writing a custom telemetry adapter that forwards signals to multiple sinks.
- You need to mock or capture telemetry in unit or integration tests.

**Don't use this when:**

- You want to configure *which* cache operations emit telemetry — that is controlled via `CachePolicy` settings and the `ICachingBuilder` configuration, not this interface.
- You are consuming cache telemetry from an external monitoring dashboard — no code change is needed; register the appropriate `ICachingTelemetryProvider` implementation once in DI.

**See also:** [How-to: telemetry and strategies](../how-to/telemetry-and-strategies.md), [Concepts](../concepts.md), [Reference: settings](settings.md)

---

## DI builder

### `ICachingBuilder`

**Namespace:** `UiPath.Platform.Caching.Config`

```csharp
public interface ICachingBuilder
{
    IServiceCollection Services { get; }

    IConfiguration Configuration { get; }

    bool Enabled { get; set; }

    void RegisterOnCompleteCallback(object key, Action<ICachingBuilder> callback);
}
```

`ICachingBuilder` is the fluent configuration handle passed to the `services.AddCaching(...)` lambda. It exposes the `IServiceCollection` and `IConfiguration` so the builder extensions that ship in the library — `AddRedisConnection()`, `AddBroadcast()`, `AddRedis()`, `AddInMemoryRedis()`, `AddMemory()`, `AddResilienceStrategies()`, `AddCloudEvents()`, `AddTelemetry()`, `AddRedisDistributedLock()`, `AddLocalLock()` — can register services, bind options, and wire up providers against the same DI container. `Enabled` acts as a feature flag — setting it to `false` causes the builder to skip provider registration, which is useful for conditional configuration (e.g. disabling caching in integration-test hosts). `RegisterOnCompleteCallback` defers arbitrary builder actions until all `AddCaching` calls in the startup chain have run, allowing later registrations to override earlier ones without ordering constraints.

**Use this when:**

- You are writing a caching extension library or plugin that needs to register additional services when caching is configured (e.g. a custom telemetry adapter, a custom key strategy).
- You need to conditionally disable the caching subsystem from a test fixture or feature-flag-driven startup.
- You need to defer a registration action until all caching providers have been added.

**Don't use this when:**

- You are writing application code that only consumes `ICache<T>` or `IHashCache<T>` — you do not interact with `ICachingBuilder` directly; `AddCaching` handles it.
- You need to inspect or modify cache behavior at request time — `ICachingBuilder` operates at startup only.

**See also:** [`ICacheFactory`](#icachefactory), [Quickstart](../quickstart.md), [Reference: settings](settings.md), [Concepts](../concepts.md)
