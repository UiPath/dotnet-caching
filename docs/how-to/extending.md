# Extending the library

Most consumers configure the library through `appsettings.json` and the `ConfigureCaching` chain — they don't extend it. This page is for the cases that go beyond configuration: adding a new storage backend, a new broadcast transport, a different serialization format, or a custom telemetry sink.

## When to extend vs. configure

Triage what you actually need before reaching for the seams below.

| You want to … | Use … |
|---|---|
| Change how keys look on Redis (prefixing, sharding, namespacing) | `ICacheKeyStrategy` — see [telemetry-and-strategies.md](telemetry-and-strategies.md#cache-key-strategies) |
| Use a different Redis instance, custom multiplexer, or OTel hookup | `IConnectionMultiplexerFactory` — see [recipes/opentelemetry-multiplexer-factory.md](../recipes/opentelemetry-multiplexer-factory.md) |
| Route telemetry events to a non-OTel surface | `ICachingTelemetryProvider` — see [recipes/custom-telemetry-provider.md](../recipes/custom-telemetry-provider.md) |
| Change how cache values are serialized to Redis | `ISerializerProxy<RedisValue>` — see [Serializers](#custom-serializer) below |
| Add a brand-new storage backend (Memcached, S3, local file, in-memory test fake) | `ICacheProvider` — see [Cache providers](#custom-cache-provider) below |
| Add a brand-new cross-node broadcast transport (Kafka, NATS, RabbitMQ) | `ITopicProvider` — see [Topic providers](#custom-topic-provider) below |
| Override the lock backend for cross-node single-flight | `IDistributedLock` — register a custom impl via `services.AddSingleton<IDistributedLock, MyLock>()` |
| Override how a `CachePolicy` resolves at construction | `ICachePolicyFactory` — implement, then wire via `builder.UseCachePolicyFactory<T>()`. See [Swapping the default factories](#swapping-the-default-factories) below. |
| Replace the entire `ICacheFactory` (custom storage routing, multi-tenant fan-out) | Implement `ICacheFactory`, then wire via `builder.UseCacheFactory<T>()`. See [Swapping the default factories](#swapping-the-default-factories) below. |

The seams in the bottom half of the table are heavier work than the ones at the top. If your need fits a top-half seam, take that path first.

## Custom cache provider

`ICacheProvider` is the contract for a storage backend. Implement it to add a backend the library doesn't ship — for example a Memcached cache, a file-backed cache for offline scenarios, or an in-memory fake for integration tests that need deterministic eviction.

### Interface

```csharp
namespace UiPath.Caching;

public interface ICacheProvider : IDisposable
{
    string Name { get; }
    bool Enabled { get; }
    ICache CreateCache();
    IHashCache CreateHashCache();
}
```

`Name` is the string consumers pass to `CacheOptions.DefaultCache` (or to `ICacheFactory.CreateCache(providerName)`) to select this provider. `Enabled` lets the provider opt out at runtime (e.g. when its configuration is missing) without throwing at startup. `CreateCache` / `CreateHashCache` return the typed cache instances — typically singletons resolved through a `Lazy<>` so consumers can call the factory many times without re-creating the cache.

### Skeleton implementation

```csharp
using UiPath.Caching;

public sealed class MemcachedCacheProvider(IMemcachedClient client, IOptions<MemcachedCacheOptions> options)
    : ICacheProvider
{
    private readonly Lazy<ICache> _cache = new(() => new MemcachedCache(client, options.Value));
    private readonly Lazy<IHashCache> _hashCache = new(() => new MemcachedHashCache(client, options.Value));

    public string Name => "Memcached";
    public bool Enabled => options.Value.Enabled;

    public ICache CreateCache() => _cache.Value;
    public IHashCache CreateHashCache() => _hashCache.Value;

    public void Dispose()
    {
        if (_cache.IsValueCreated) _cache.Value.Dispose();
        if (_hashCache.IsValueCreated) _hashCache.Value.Dispose();
    }
}
```

`MemcachedCache` and `MemcachedHashCache` implement `ICache` / `IHashCache` against the underlying client. Refer to `RedisCache` and `RedisHashCache` in `Caching.Runtime` for production-grade examples of every method.

### Registration

Wire the provider via a builder extension that adds it to the DI container and then calls `ICacheFactory.AddProvider`:

```csharp
using UiPath.Caching;
using UiPath.Caching.Config;

public static class MemcachedCachingBuilderExtensions
{
    public static ICachingBuilder AddMemcached(this ICachingBuilder builder)
    {
        // Register the provider as a singleton AND in the IEnumerable<ICacheProvider> that
        // CacheFactory injects to discover providers. TryAddEnumerable de-dupes by impl type,
        // so this is safe to call multiple times.
        builder.Services.TryAddSingleton<MemcachedCacheProvider>();
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<ICacheProvider, MemcachedCacheProvider>(
                sp => sp.GetRequiredService<MemcachedCacheProvider>()));
        return builder;
    }
}
```

Consumers wire it like any other provider:

```csharp
services.AddCaching(section, b => b.AddRedisConnection().AddMemcached().AddInMemoryRedis(), o => { section.Bind(o); o.AppShortName = "my-service"; });
```

And select it by name in `appsettings.json`:

```json
{
  "Caching": {
    "DefaultCache": "Memcached"
  }
}
```

### Lifecycle and contract notes

- **Singleton scope.** Providers are stored on the `ICacheFactory`, which is a singleton. Implement `IDisposable` carefully — the provider lives for the application's lifetime, and its caches must outlive any consumer.
- **Thread safety.** Both `CreateCache` and `CreateHashCache` may be called concurrently. Cache the inner instances behind `Lazy<>` (as above) or another lock-free pattern.
- **The `Enabled` flag is a hard gate at the factory.** `CacheFactory.GetProvider(name)` returns `null` for any registered provider whose `Enabled` is `false`. `CreateCache(name)` then falls back to the `DefaultCache` provider, and if that one is also disabled it falls back to `NullCache.Instance`. Your provider's `CreateCache()` / `CreateHashCache()` are not called when `Enabled` is `false` — there is no "graceful no-op inside the provider" code path to write.
- **Match the integration points.** A provider that wants to participate in cross-node L1 invalidation must consume an `ITopic<ICacheEvent>` and broadcast cache events on writes — see how `InMemoryRedisCacheProvider` does it in `Caching.Runtime`. A provider that wants single-flight should accept `ILocalLock` / `IDistributedLock` in its constructor and route `GetOrAddAsync` through them.

## Custom topic provider

`ITopicProvider` is the contract for a broadcast transport. Implement it to add transports the library doesn't ship — Kafka, NATS, RabbitMQ, or an in-memory transport for tests.

### Interface

```csharp
namespace UiPath.Caching.Broadcast;

public interface ITopicProvider : IDisposable
{
    string Name { get; }
    bool Enabled { get; }
    ICollection<TopicKey> Keys { get; }
    ITopic<ICacheEvent> Create(TopicKey topicKey);
    void Remove(TopicKey topicKey);
}
```

`Name` is the string consumers pass to `CacheOptions.DefaultTopic` (or `ITopicFactory.Get(providerName)`) to select this provider. `Keys` lets the factory enumerate the topics this provider has produced so far. `Create` returns an `ITopic<ICacheEvent>` for a given `TopicKey` — typically idempotent: repeated calls with the same key return the same `ITopic` instance. `Remove` tears a topic down.

### Skeleton implementation

```csharp
using UiPath.Caching.Broadcast;

public sealed class KafkaTopicProvider(IKafkaClient kafka, IOptions<KafkaTopicOptions> options)
    : ITopicProvider
{
    private readonly ConcurrentDictionary<TopicKey, ITopic<ICacheEvent>> _topics = new();

    public string Name => "Kafka";
    public bool Enabled => options.Value.Enabled;
    public ICollection<TopicKey> Keys => _topics.Keys.ToList();

    public ITopic<ICacheEvent> Create(TopicKey topicKey) =>
        _topics.GetOrAdd(topicKey, k => new KafkaTopic(kafka, k, options.Value));

    public void Remove(TopicKey topicKey)
    {
        if (_topics.TryRemove(topicKey, out var topic))
        {
            topic.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (var topic in _topics.Values)
        {
            topic.Dispose();
        }
        _topics.Clear();
    }
}
```

`KafkaTopic` implements `ITopic<ICacheEvent>` — refer to `RedisStreamsTopic` and `RedisPubSubTopic` in `Caching.Runtime/Broadcast/Redis/` for production-grade examples. The key obligations: serialize the `ICacheEvent` payload on publish, deserialize on receive, and dispatch to registered observers via a bounded channel for back-pressure.

### Registration

```csharp
public static class KafkaTopicBuilderExtensions
{
    public static ICachingBuilder AddKafka(this ICachingBuilder builder)
    {
        builder.Services.TryAddSingleton<KafkaTopicProvider>();
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<ITopicProvider, KafkaTopicProvider>(
                sp => sp.GetRequiredService<KafkaTopicProvider>()));
        return builder;
    }
}
```

Select via configuration:

```json
{
  "Caching": {
    "DefaultTopic": "Kafka"
  }
}
```

### Lifecycle and contract notes

- **Idempotent `Create`.** Multiple parts of the library (cache providers, application code) may call `Create` for the same `TopicKey` concurrently. Cache the result; do not produce a new `ITopic` per call.
- **`Remove` must be safe to call on an unknown key.** The stream maintainer (and consumer code) may call `Remove` for keys the provider never produced — return without error.
- **Back-pressure is the topic's job, not the provider's.** `ITopic<ICacheEvent>` implementations should wrap delivery in a `System.Threading.Channels` channel with the configured `ConsumerCapacity` / `FullMode`, just like `RedisStreamsTopic` does.
- **No cross-provider routing.** A `TopicKey` resolves through one provider — the one selected by `DefaultTopic` or by an explicit `Get(providerName)` call. Topics do not fan out across providers.

## Custom serializer

`ISerializerProxy<T>` is the seam for cache-value serialization. The library ships exactly one implementation — `SystemJsonSerializerProxy` (which implements `ISerializerProxy<RedisValue>`) — and consumers swap it to use MessagePack, ProtoBuf, MemoryPack, or any other format.

### Interface

```csharp
namespace UiPath.Caching;

public interface ISerializerProxy<T1>
{
    T1? Serialize(object? value);
    T? Deserialize<T>(T1? value);
    bool TryDeserialize<T>(string? value, out T? result);
    bool TryDeserialize<T>(object? value, out T? result);
}
```

The two `TryDeserialize` overloads exist so callers can attempt a deserialization without throwing on malformed input — useful for cache-poisoning recovery (drop the bad entry and rehydrate).

### Skeleton implementation

```csharp
using MessagePack;
using StackExchange.Redis;
using UiPath.Caching;

public sealed class MessagePackSerializerProxy : ISerializerProxy<RedisValue>
{
    private readonly MessagePackSerializerOptions _options = MessagePackSerializerOptions.Standard;

    public RedisValue Serialize(object? value) =>
        value is null ? RedisValue.Null : MessagePackSerializer.Serialize(value.GetType(), value, _options);

    public T? Deserialize<T>(RedisValue value) =>
        value.IsNull ? default : MessagePackSerializer.Deserialize<T>((byte[])value!, _options);

    public bool TryDeserialize<T>(string? value, out T? result)
    {
        // MessagePack is a binary format; string inputs aren't expected from RedisValue
        // (which stores byte[] directly). Return false here so callers route to the
        // RedisValue overload via the TryDeserialize<T>(object?) entry point.
        result = default;
        return false;
    }

    public bool TryDeserialize<T>(object? value, out T? result) =>
        value is RedisValue rv ? TryDeserializeRedis(rv, out result) : TryDeserialize(value?.ToString(), out result);

    private bool TryDeserializeRedis<T>(RedisValue value, out T? result)
    {
        result = default;
        if (value.IsNull) return false;
        try
        {
            result = MessagePackSerializer.Deserialize<T>((byte[])value!, _options);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
```

### Registration

```csharp
services.AddSingleton<ISerializerProxy<RedisValue>, MessagePackSerializerProxy>();
```

That single registration replaces the default JSON serializer for every cache the library creates.

### Contract notes

- **`Serialize(null)` must round-trip.** Producing a `RedisValue.Null` on input null is the convention; `Deserialize` should return `default` for `RedisValue.Null` input. If you persist null sentinels differently, the cache's `CacheNullValues` flag and the `NullCache` write-path will not behave correctly.
- **Schema evolution is your problem.** The library does not version cached payloads. If your serializer can't deserialize an older payload after a deploy, `TryDeserialize` should return `false` and the cache will treat that as a miss; the generator will run and write a fresh entry.
- **Consider `RedisValue` directly.** `RedisValue` can hold strings, bytes, or numbers without conversion. A binary serializer (MessagePack, ProtoBuf, MemoryPack) should write `byte[]` directly via `RedisValue` implicit conversion rather than going through `string`/Base64 — JSON-style proxies can stay string-based.

## Swapping the default factories

`ICacheFactory` and `ICachePolicyFactory` each have a default DI registration set up by `AddCaching` — the concrete `CacheFactory` and `DefaultCachePolicyFactory` respectively. When you need to substitute either, use the fluent `Use*Factory` extensions on `ICachingBuilder`. They internally call `Services.Replace(...)` so the swap survives the rest of the `AddCaching` pipeline (which uses `TryAddSingleton` and would otherwise lose to the default registration).

### `builder.UseCacheFactory<T>()`

Three overloads, each producing the same end-state (the registered `ICacheFactory` is your type):

```csharp
services.AddCaching(section, builder =>
{
    // (a) Generic — DI activates the type, injecting any dependencies it needs.
    builder.UseCacheFactory<MyCacheFactory>();

    // (b) Instance — pass an already-constructed factory (test fixtures, multi-tenant routers).
    builder.UseCacheFactory(new MyCacheFactory(...));

    // (c) Factory delegate — full control over construction with the service provider.
    builder.UseCacheFactory(sp => new MyCacheFactory(sp.GetRequiredService<IFoo>()));
});
```

All three overloads short-circuit when `CacheOptions.Enabled` is `false` — the `NullCacheFactory` registered by `AddCaching` for the disabled-master-switch case is preserved, matching the behavior of `AddBroadcast` / `AddTelemetry` when caching is fully disabled.

A custom `ICacheFactory` that wants to expose its policy factory through the abstraction can return one from the new `ICacheFactory.PolicyFactory` property; the default impl is `=> null` so existing implementors are not broken. The DI-registered `ICachePolicyFactory` still wins inside `Cache<T>` / `HashCache<T>` ctors — the custom factory's `PolicyFactory` is the fallback when no DI factory is registered.

### `builder.UseCachePolicyFactory<T>()`

Same three overloads, same `Enabled` short-circuit:

```csharp
services.AddCaching(section, builder =>
{
    builder.UseCachePolicyFactory<MyPolicyFactory>();
    // or
    builder.UseCachePolicyFactory(new MyPolicyFactory(...));
    // or
    builder.UseCachePolicyFactory(sp => new MyPolicyFactory(sp.GetRequiredService<IFoo>()));
});
```

The default `DefaultCachePolicyFactory` resolves policies by name against `CacheOptions.Policies` (case-insensitive) and exposes three members: `Resolve(string policyName)` returns the pre-merged named policy or `null` when the name is absent; `Default` returns `CacheOptions.DefaultCachePolicy` (also nullable); `Keys` enumerates the configured policy names so validators (and your replacement, if you implement one) can walk the registered set without re-binding the options. Each named policy is pre-merged with `Default` at construction. Replace it when you need:

- A completely different resolution source (e.g. resolve policies from a remote configuration store).
- Custom merge semantics (e.g. layered defaults: type → assembly → app-wide).
- A dynamic policy that recomputes on every `Resolve` call (the default snapshots once at startup).

The `CachePolicyMerger` static helper (public in `UiPath.Caching.Config`) exposes the canonical "primary wins, fallback fills" merge for `CachePolicy` if your custom factory wants to reuse it. `CachePolicyFactoryValidator.Validate(factory, distributedLockPollInterval)` runs the same per-policy validation that `DefaultCachePolicyFactory`'s constructor applies (lock invariants, jitter range, rehydrate fields, `LocalExpirationDisconnected ≤ LocalExpiration`); call it at the end of your own factory's constructor to opt into the rules.

## Customizing what already has a seam

These extension points are documented elsewhere — link out instead of duplicating:

- **Telemetry sink** — implement `ICachingTelemetryProvider` to bridge to an internal telemetry surface. See [recipes/custom-telemetry-provider.md](../recipes/custom-telemetry-provider.md).
- **Connection multiplexer / OTel integration** — implement `IConnectionMultiplexerFactory` to control multiplexer creation. See [recipes/opentelemetry-multiplexer-factory.md](../recipes/opentelemetry-multiplexer-factory.md).
- **Cache key shape** — implement `ICacheKeyStrategy` (or use `PrefixCacheKeyStrategy`). See [telemetry-and-strategies.md#cache-key-strategies](telemetry-and-strategies.md#cache-key-strategies).
- **Stream / channel naming** — implement `IRedisStreamKeyStrategy` / `IRedisChannelStrategy`. See [broadcast.md#code-side-overrides](broadcast.md#code-side-overrides).
- **Distributed lock backend** — implement `IDistributedLock` and register as a singleton. The default impl `RedisDistributedLock` is registered automatically by `AddInMemoryRedis()`; only opt out when you want a non-Redis lock store.
- **Cache policy resolution** — implement `ICachePolicyFactory` to control how a `CachePolicy` is resolved at `ICache<T>` construction. The default reads `CacheOptions.Policies` by `typeof(T).FullName`.

## See also

- [concepts.md](../concepts.md) — providers, layers, topics, the surface vocabulary used above.
- [reference/interfaces.md](../reference/interfaces.md) — full signatures of every interface mentioned on this page.
- [reference/settings.md](../reference/settings.md) — the `DefaultCache` / `DefaultTopic` settings that select a registered provider.
