# Extending the library

Most consumers configure the library through `appsettings.json` and the `ConfigureCaching` chain — they don't extend it. This page is for the cases that go beyond configuration: adding a new storage backend, a new broadcast transport, a different serialization format, or a custom telemetry sink.

## When to extend vs. configure

Triage what you actually need before reaching for the seams below.

| You want to … | Use … |
|---|---|
| Change how keys look on Redis (prefixing, sharding, namespacing) | `ICacheKeyStrategy` — see [telemetry-and-strategies.md](telemetry-and-strategies.md#cache-key-strategies) |
| Use a different Redis instance, custom multiplexer, or OTel hookup | `IConnectionMultiplexerFactory` — see [recipes/opentelemetry-multiplexer-factory.md](../recipes/opentelemetry-multiplexer-factory.md) |
| Route telemetry events to a non-OTel surface | `ICachingTelemetryProvider` — see [recipes/custom-telemetry-provider.md](../recipes/custom-telemetry-provider.md) |
| Change how cache values are serialized to Redis | `ISerializerProxy<byte[]>` — see [Serializers](#custom-serializer) below |
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
- **Match the integration points.** A provider that wants to participate in cross-node L1 invalidation must consume an `ITopic<ICacheEvent>` and broadcast cache events on writes — see how `InMemoryRedisCacheProvider` does it in `Caching.Runtime`. A provider that wants single-flight should accept `IDistributedLock` in its constructor and route `GetOrAddAsync` through it.

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

`ISerializerProxy<byte[]>` is the seam for cache-value serialization — one seam for every cache the
library creates, including the `IDistributedCache` adapter. Consumers swap it to use MessagePack,
ProtoBuf, MemoryPack, or any other format.

The seam lives in `UiPath.Caching.Abstractions` and names no Redis type, so a custom serializer needs
no dependency on StackExchange.Redis.

Two implementations ship in `UiPath.Caching`:

| | Byte payloads | Everything else | Used by |
|---|---|---|---|
| `SystemJsonByteSerializerProxy` (default) | base64 inside a JSON string | UTF-8 JSON | every cache, unless you replace it |
| `RawByteSerializerProxy` | stored verbatim | UTF-8 JSON | the provider `AddDistributedCache` builds |

The default is precisely the format the library has always written, so upgrading moves no cache entry.
(A null value is the one exception: it now serializes to a null payload rather than the four-byte
JSON `null` literal, following the contract note below. Both read back as `default`.)
`RawByteSerializerProxy` exists because an `IDistributedCache`
payload is caller bytes that the caller has already serialized: base64-ing them would cost a third
again in size for no benefit, and a consumer who swaps in MessagePack should not have their session
bytes re-encoded. Neither implementation inspects the payload — the requested type argument decides. A
format that is not JSON belongs in a [value serializer](#value-serializers), which tags what it writes.

You can register `RawByteSerializerProxy` app-wide to get raw byte storage everywhere, but that *is*
a wire-format change: it returns stored bytes as-is rather than base64-decoding them, so a 1.x
`byte[]` entry would come back as the base64 ASCII, and without throwing — nothing degrades it to a
cache miss. Relocate the keyspace (a version segment in `AppShortName` or in the cache-key strategy)
so reads cannot land on entries written the other way.

### Lending memory: `IMemorySerializerProxy`

`IMemorySerializerProxy` refines `ISerializerProxy<byte[]>` with one member:

```csharp
ReadOnlyMemory<byte> SerializeToMemory<T>(T? value);
```

It exists for a serializer that can hand back memory *without* materializing an array — above all a
caller's own `ReadOnlyMemory<byte>`, returned as-is. `RedisCache` and `RedisHashCache` call it instead of
`Serialize` when the configured serializer offers it, which is how a buffer the `IDistributedCache` adapter
is handed reaches the wire with no array in between. Both shipped serializers implement it:
`SystemJsonByteSerializerProxy` returns its array output wrapped (a byte payload is still base64 inside
JSON — the wire format does not depend on which member a tier calls), and `RawByteSerializerProxy` passes
`ReadOnlyMemory<byte>` and `Memory<byte>` through untouched. The member is generic so that a struct value
is not boxed on the way in.

Implementing it is optional; a serializer that only implements `ISerializerProxy<byte[]>` keeps the array
path everywhere. If you do implement it, the memory you return is **borrowed**: it may alias the input,
and the caller reclaims it as soon as the operation completes. The Redis provider never retains it — the
connection copies the value into its own buffer as the command is written, and the write is awaited to
completion before the caller is told it is done. That await is also a contract on `IResiliencePipeline`:
`ExecuteAsync` must not complete before its callback has, so a timeout has to be cooperative. The Polly
pipeline this library ships is; a custom pipeline that returned around a callback still in flight would
let the connection send a buffer that has since been reused.

`ReadOnlyMemory<byte>` is itself a cacheable value type for the same reason the raw serializer can pass
it through: its default is empty memory, which the tiers already read as absent. An empty value is
therefore indistinguishable from a miss, as a zero-length payload is. The memory tiers keep whatever value
they are handed, exactly as they keep a `byte[]`, so a caller writing a pooled buffer to one of them copies
it first — the adapter does.

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

`TryDeserialize(object)` no longer sees cache event properties that the built-in event formatters decode: they read `_version_`, the expirations, the metadata and the peer fields as typed values, and only a custom event formatter that leaves them as `JsonElement` still reaches it. So does the CloudEvents proxy's stock decode when the fast path declines an event (an extra, missing or duplicate attribute).

### Skeleton implementation

Most binary serializers natively produce `byte[]`, so an implementation is mostly delegation:

```csharp
using MessagePack;
using UiPath.Caching;

public sealed class MessagePackByteSerializerProxy(MessagePackSerializerOptions? options = null)
    : ISerializerProxy<byte[]>
{
    public byte[]? Serialize(object? value) =>
        value is null ? null : MessagePackSerializer.Serialize(value.GetType(), value, options);

    public T? Deserialize<T>(byte[]? value) =>
        value is null or { Length: 0 } ? default : MessagePackSerializer.Deserialize<T>(value, options);

    public bool TryDeserialize<T>(string? value, out T? result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }
        try
        {
            result = MessagePackSerializer.Deserialize<T>(Convert.FromBase64String(value), options);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool TryDeserialize<T>(object? value, out T? result)
    {
        result = default;
        try
        {
            if (value is byte[] bytes)
            {
                result = MessagePackSerializer.Deserialize<T>(bytes, options);
                return true;
            }
            return TryDeserialize(value?.ToString(), out result);
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
services.AddSingleton<ISerializerProxy<byte[]>>(new MessagePackByteSerializerProxy());
```

That single registration replaces the default JSON serializer for the caches the library resolves
from DI — `ICache`, `IHashCache` and `ISetCache`. It does **not** reach the `IDistributedCache`
adapter, which constructs its own `RawByteSerializerProxy`; see the contract note below.

### Contract notes

- **`Serialize(null)` must return a non-null payload.** A null return reaches StackExchange.Redis as `RedisValue.Null`, which throws `ArgumentException` on `SADD` and on the multi-field `HSET`, and silently stores nothing on the single-value paths. Encode null as a sentinel your own `Deserialize` maps back to `default` — the default proxy uses JSON's four-byte `null` literal. `Deserialize` should also return `default` for a null or empty buffer, except where a raw type argument makes the empty buffer meaningful: `RawByteSerializerProxy.Deserialize<byte[]>` returns the empty array, because an empty payload is a legitimate value there rather than an absent one.
- **Schema evolution is your problem.** The library does not version cached payloads. If your serializer can't deserialize an older payload after a deploy, `TryDeserialize` should return `false` and the cache will treat that as a miss; the generator will run and write a fresh entry. Note that `Deserialize` is the hot path for reads, and a throw there is caught by the caches and reported as a miss — but a payload your serializer *misreads* without throwing is returned to the caller as-is.
- **Round-trip `byte[]` symmetrically, and decide deliberately.** Passthrough avoids a per-entry encoding layer; base64 (what the default does) keeps the format the library has always written. Either works, but the two are mutually unreadable and neither can detect the other, so a serializer swap that changes this needs a keyspace relocation, not a compatibility shim.
- **Only replace the default globally.** `AddDistributedCache` gives its own provider `RawByteSerializerProxy` explicitly rather than resolving one from DI, so replacing the app-wide registration does not change what the `IDistributedCache` adapter stores in its `data` field. That is deliberate: those bytes belong to the caller.
- **Set members are compared by their serialized bytes.** `ISetCache`'s local tier keys its snapshot on `Serialize` output using structural byte equality, so a serializer must be deterministic: two equal values must serialize identically, or set membership and de-duplication will disagree between the memory and Redis tiers.

### Named value serializers: `ICacheValueSerializer`

`AddValueSerializer(name, factory)` registers an `ICacheValueSerializer` that a policy selects with
`Serializer.Write` (see [Policies\[\<name\>\].Serializer](../reference/settings.md#policiesnameserializer-serializerpolicy)).
The library writes and strips the format header; the serializer only produces and reads the payload.

```csharp
builder.AddValueSerializer("fast", _ => new MyValueSerializer());
```

The registry owns whatever the factory returns and disposes it with the container, through `DisposeAsync` when
the serializer has one and `Dispose` otherwise. So the factory must return a new instance, not one resolved from
DI, which the container would dispose a second time. The factory runs when the registry is built, on first cache
use; a build that throws disposes what it created, and the next use runs the factory again.

### Distributed cache keyspace

The distributed cache stores each entry as a Redis hash with a `data` field plus `absexp`/`sldexp`
expiration metadata, the conventional layout for this contract, so
`Refresh` reads the metadata without transferring the payload. Its keyspace is deliberately disjoint
from the application's own caches, at two overridable levels — `RedisKeyDifferentiator` for the
physical Redis keyspace, and `CacheKeyStrategy` for the `CacheKey` itself, so an entry cannot be
reached through the application's own `ICache`/`IHashCache` with the bare caller key:

```csharp
builder.AddDistributedCache(KnownCacheProviderNames.Redis, o =>
{
    o.CacheKeyStrategy = new PrefixCacheKeyStrategy("sessions");
    o.RedisKeyDifferentiator = "sess";
});
```

Both default to values that keep the two keyspaces apart (`"d"` and `"dh"`); overriding them makes
that separation yours to preserve. A differentiator matching a keyspace any registered package has
reserved is rejected at registration, whichever order the two were added in, whether or not they came
from the same `AddCaching` call, and even when `CacheOptions.Enabled` is false, since a collision is a
configuration error rather than a runtime condition: the core reserves its `RedisKeyspaces`, and
`UiPath.Caching.Queue` reserves `"se"` for its set cache from both `AddQueueRedis` and
`AddQueueInMemoryRedis`, including when the backing is disabled. A custom provider that occupies a
keyspace of its own should declare it the same way, from its registration extension, so the check
covers it wherever it was added:

```csharp
builder.Services.ReserveRedisKeyspace("li", "IListCache (My.Package)");
```

The keyspace is one segment of letters and digits, so no reservation can span segments under a
punctuation separator. `CacheOptions.Separator` may itself be alphanumeric, which that rule cannot
anticipate, so one keyspace nesting inside another under the configured separator is rejected when
the options are resolved.
The owner string names you in the error and is what a repeat reservation is matched on, so a package
can reserve from several registration methods. Qualify it with the package name: two packages sharing
one owner string on one keyspace read as one package reserving twice.

For full control of the Redis key — a mandated layout, or a cluster hash-tag scheme —
`RedisKeyStrategyFactory` replaces the factory the key is built from. It still receives
`RedisKeyDifferentiator`, and resolving the cache proves the composed key differs from what the
application's caches and every reserved keyspace would produce, so a factory that lands on one fails
instead of sharing the keyspace. That check runs when `IDistributedCache` is first resolved, which is
at startup only if something resolves it then — otherwise on the first request that does. Resolve it
during startup if you want the failure there.

It is a probe, not a proof. Each reserved keyspace is rendered through the application's factory,
which is exact for the caches and approximate for anything that composes its keys elsewhere, such as
broadcast streams going through `RedisStreamKeyStrategy`. A custom factory that lands on one of those
layouts without matching the probed one is not caught:

```csharp
o.RedisKeyStrategyFactory = new MyRedisKeyStrategyFactory();   // gets "dh" (or your differentiator)
```

Left null it inherits the application's own `RedisCacheOptions.RedisKeyStrategyFactory`, so
`AppShortName`, the separator and sharding behave as they do everywhere else.

### Value serializers

`ICacheValueSerializer` is the seam for a format that is not JSON: MessagePack, MemoryPack, a compressing
wrapper. It writes into the buffer the library hands it, and the library puts a one-byte format id in front
of what it wrote. The reader picks the serializer by that id, not by the policy's current choice, so a
policy can switch formats at any time and the values already stored still read.

```csharp
using System.Buffers;
using MessagePack;
using UiPath.Caching;

public sealed class MessagePackValueSerializer(MessagePackSerializerOptions? options = null) : ICacheValueSerializer
{
    public IReadOnlyList<int> FormatIds { get; } = [0x10];

    public int Serialize<T>(T value, IBufferWriter<byte> target)
    {
        MessagePackSerializer.Serialize(target, value, options);
        return 0x10;
    }

    public T? Deserialize<T>(int formatId, ReadOnlySequence<byte> source) =>
        MessagePackSerializer.Deserialize<T>(source, options);
}
```

Register it by name and choose it per policy:

```csharp
services.AddCaching(
    b =>
    {
        b.AddRedis(_ => { });
        b.AddValueSerializer("msgpack", _ => new MessagePackValueSerializer());
    },
    o => o.Policies["Sessions"] = new CachePolicy { Serializer = new SerializerPolicy { Write = "msgpack" } });
```

A policy without `Serializer` keeps writing JSON, exactly as before. `Write` accepts a registered name or
`json`, and a name nobody registered fails when the policies are built; a policy passed to a single call
that names an unregistered serializer throws `OptionsValidationException` on that call, before anything is
written.

Rules a serializer follows:

- **Format ids.** A serializer lists the ids it writes in `FormatIds`, all with the same header length.
  Consumer serializers take `0x10`-`0x1E` (one byte) or `0x100`-`0x1FF` (the marker byte `0x1F`, then the low
  byte). `0x01`-`0x0F`, except `0x09`, `0x0A` and `0x0D`, belong to the library. Every other first byte,
  including `0x00`, JSON whitespace (`0x09`, `0x0A`, `0x0D`) and `0x20` onward, is JSON, which has no header. An
  id is never reused: a leftover value written under a retired id would be misread by whatever took it over.
- **`Serialize` returns the id it wrote**, one of `FormatIds`; a serializer that picks between a plain and a
  compressed id says which after it has written.
- **Never write zero bytes.** An empty payload means a cached null.
- **Never retain `source`.** `Deserialize` receives a sequence over a buffer the library owns and reclaims
  when the call returns, so copy what you keep.
- **Handle null.** A single value that is null never reaches `Serialize`, but a hash field can be null while
  nulls are not cached; write something `Deserialize` maps back to `default` (MessagePack's nil does).

What a node does with a value it cannot read depends on why:

- **An id no registered serializer owns** is data no node with this registry can read. It is a miss, the key
  is removed (best effort) only if it still holds the bytes that were read, `cache.serializer.unreadable`
  counts it and the generator runs. A newer value written in between survives. A node with no serializer
  registered does not look for ids: it logs a `JsonException` miss and keeps the key.
- **A registered serializer that throws** is a logged miss and the key stays, as for JSON that does not parse:
  the bytes may be good for another version of the type that other nodes run during a rolling deploy.
  An `OutOfMemoryException` from a registered serializer's payload is never treated as a failed read; one from a value with no header (JSON) is a logged miss, as with no serializer registered.
- **A hash with such a field** misses as a whole and is left in place; an unknown id is still counted.

Update the nodes that read before any policy writes a new format: a node that cannot read one keeps missing
and regenerating until it is updated.

Under a policy that writes a serializer, reads of a custom app-wide `ISerializerProxy<byte[]>` look for a
format header, so the shared keys of such a policy must not also be written by that proxy: a byte it writes
first that happens to be a format id would be handed to the wrong reader. The gate is this. With the default
JSON proxy, every read looks for a header once a serializer is registered. With a custom proxy, a subclass or
`RawByteSerializerProxy`, a read looks only when the call's effective policy (its own `Serializer.Write`, else
the default policy's) writes a serializer other than `json`, compared without regard to case. `Write = "json"`
and `Serializer.Read` do not open it, so such a proxy's values, and what `AddDistributedCache` stores, are never
inspected under them.

A policy that sets no `Serializer` takes the default policy's `Write`, and one that sets only `Read` does too.

### MessagePack

`UiPath.Caching.MessagePack` is a ready `ICacheValueSerializer`: MessagePack, LZ4-compressed above a size threshold. It
needs no attributes on your types. In this repo's benchmarks a small object went from 172 bytes as JSON to 102 as MessagePack, a medium one from 799 to 645, and a large one from 388,780 to 277,208. Compression then cuts repetitive rows to roughly a third at 16 KB and above: 1 KB of rows 1,257 to 626 bytes, 16 KB 19,327 to 7,124, 128 KB 156,651 to 56,385.

```csharp
services.AddCaching(
    b =>
    {
        b.AddRedis(_ => { });
        b.AddMessagePackSerializer(); // registered as "messagepack"; pass a name and a callback to change either
    },
    o => o.Policies["Sessions"] = new CachePolicy { Serializer = new SerializerPolicy { Write = "messagepack" } });
```

Or in configuration, with the serializer registered in code: `"Policies": { "Sessions": { "Serializer": { "Write": "messagepack" } } }`.
Only the policies that name it write MessagePack; every other policy keeps writing JSON.
The package depends on the exact `UiPath.Caching` version it was built with, so upgrade the two together.

| Option | Default | Notes |
|---|---|---|
| `CompressionThresholdBytes` | `1024` | A value whose MessagePack bytes reach this size is compressed when that makes it smaller, so an incompressible value is stored plain. `0` tries every value, `int.MaxValue` none. |
| `MaxDecompressedBytes` | `67108864` (64 MiB) | A compressed value that claims more than this uncompressed is rejected on read, as is any claim above 255 times its compressed size. Deserializing can allocate several times the decompressed size (measured up to about 8x for typed collections, more for an `object[]` of nils), so size it to available memory. It replaces `MaximumDecompressedSize` on a supplied `MessagePackSecurity`. |
| `SerializerOptions` | contractless resolver, `MessagePackSecurity.UntrustedData` | Replace it to use your own resolvers. `UntrustedData` adds hash-collision-resistant comparers; the object-graph depth limit is the same as `TrustedData`. A supplied instance that carries `MessagePackSecurity.TrustedData` is switched to `UntrustedData`; to run trusted, pass your own `MessagePackSecurity` instance. Where MessagePack has no resistant comparer for a key type (`TimeSpan`, `decimal`, `Uri`, tuples, custom structs), the default comparer is used instead of failing the read. Compression set on it is ignored: the threshold decides. A resolver you supply that resolves `object` by type name (Typeless) or polymorphically reopens type confusion on bytes read from Redis. |

The serializer owns format ids `0x02` (plain) and `0x03` (LZ4); the id stored with each value says which, so a node reads
both whatever its threshold.

By default (`FormatNegotiation:Mode` `Auto`) a policy that sets `Write = "messagepack"` writes it only while every node on the Streams or Pub/Sub transport is known to read it, and JSON until then, so this is safe to deploy in one step; see [choosing when a format is written](../concepts.md#choosing-when-a-format-is-written). With `Force`, roll it out readers first. A node reads a format only once the serializer is registered on it, so register
`AddMessagePackSerializer` on every node and finish that deploy before any policy sets `Write = "messagepack"`; a node that
cannot read the value misses and regenerates it (see [rolling a serializer out](../concepts.md#rolling-a-serializer-out)).
Setting `Write` back to `json` keeps values already stored as MessagePack readable while the default JSON proxy is in use; with a custom `ISerializerProxy` registered, headers are only recognized under a policy that writes a non-JSON format, so under `Write = "json"` those values go to your proxy and read as misses.

The default resolver is contractless, which writes a type's public property names, as JSON does. Renaming a
property, or changing its type, therefore silently loses that data for values written before the change, exactly as
it would with JSON: the old values read back with the new property empty. Mark a type with `[MessagePackObject]` and
`[Key(n)]` to make the format independent of the names.

### MessagePack events

`AddMessagePackEvents(write)` wraps the event formatter with one that reads both formats, told apart by the first
byte: `{` or whitespace is JSON, `0x02` is binary. A binary event is a MessagePack map with integer keys, a binary GUID
id, an integer event type and the peer fields as raw bytes, so a `CacheSet` without peer fields is about a third of its
JSON size and a peer-field event saves the 33% base64 adds. Properties it has no compact form for travel as JSON bytes
and read back as `JsonElement`, as they do from a JSON event, and an expiry reads back in UTC.

```csharp
b.AddMessagePackEvents(write: true);  // reads both; writes binary once every live node can read it
b.AddMessagePackEvents(write: false); // reads both; always writes JSON
```

There is no deploy order. With `write: true` a node writes JSON until the node list shows that every node reads binary events, for the
`UpgradeHold` (an hour by default) after the last node joined, and writes JSON again as soon as a node that cannot read them appears. Each node
advertises the event formats it decodes: one with this formatter registered lists binary, one without lists JSON only, and a node before
3.2.0 lists nothing and is treated as unable to read. See [choosing when a format is written](../concepts.md#choosing-when-a-format-is-written),
including how a Pub/Sub deployment counts subscribers. A node on an older version acknowledges and skips a binary event, logging a warning for every 100th one it cannot decode and tracking telemetry, but never treating it as a loss, so it keeps
what it has until `LocalMaxExpiration`; one on 3.2.0 or later without the formatter treats the event as lost and drops its whole local tier for that
topic. An old node blocks binary events while it is live in the node list, so what is left is the window a join leaves open, about one view interval; a Pub/Sub node that advertises nothing and takes a freed subscription slot, which stays hidden for up to the advertisement lifetime plus two views (about 180 s by default), during which it skips binary events and may serve a stale local value; and a node judged dead that returns on the same source id: the
stale local entry it keeps is bounded by that old node's own `LocalMaxExpiration`. Set a short one, a few minutes, on 3.0 and 3.1 nodes before enabling `write: true`.

`FormatNegotiation:Mode` `Force` writes binary at once with `write: true`: then every node must read both first, as before. An app with no Redis connection has no node list to read, and `write: true` writes JSON events unless `FormatNegotiation:AssumeAllNodesCanRead` attests that every node reads binary or `Mode` is `Force`. Switching back to `write: false` is safe at any time. Never remove the JSON decoder:
a restarted node replays its stream backlog, and a JSON entry it could not read is skipped without counting as a loss, so the node would keep what that entry invalidated. The setting is
app-wide, not per topic.

It wraps whichever JSON formatter would otherwise be registered, `AddCloudEvents` included, whether that is called
before or after `AddMessagePackEvents`, so the JSON on the wire is what it was. Calling `AddMessagePackEvents` again
replaces the earlier call's `write`.

An event whose body starts with a format id this library writes (`0x02`, `0x03`) or one of the consumer ids, but does
not decode, such as a binary event on a node that never registered the formatter, is reported as a loss, not skipped.
The stream reader acknowledges the entries and expires the local copies once per batch; the Pub/Sub reader expires
them through the same coalesced path as an event dropped at a full channel, so it holds for caches that keep their
tier over a subscription gap too. A body that does not start with one of those ids, such as JSON, garbage or another
producer's first byte on a shared stream, is still dropped as junk. Binary events are read as untrusted data: nesting
is limited by `MessagePackSecurity.UntrustedData`, and a count the body cannot hold is rejected before anything is
read. A map's initial capacity is capped at 16 entries, so allocation follows the entries the body holds, not the count it claims.

The integer event type values on the wire (`CacheSet` 0, `CacheRefreshed` 1, `CacheRemoved` 2, `TopicInvalidated` 3)
are fixed and only ever appended to. A node that reads a type number it does not know refuses the event, which is a
loss, so a release that adds a type needs every node on it before any node writes binary events: use `Force` only after that, and in `Auto` upgrade every node before the hold has passed.

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

A custom `ICacheFactory` that wants to expose its policy factory implements `IPolicyDefaults` as well, as `CacheFactory` does; a `PolicyFactory` property on a class that implements only `ICacheFactory` is never read. The DI-registered `ICachePolicyFactory` still wins inside `Cache<T>` / `HashCache<T>` ctors — the factory's `PolicyFactory` is the fallback when no DI factory is registered.

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

## Controlling time

Every expiration decision in the library reads one clock: the `System.TimeProvider` that `AddCaching`
registers, `TimeProvider.System` unless the container already has one. It is also what the in-memory
tier's `MemoryCache` judges deadlines by, so a deadline is always computed and evaluated against the
same "now". Nothing reads `DateTimeOffset.UtcNow` and no options object carries a clock of its own.

To drive time in tests or a simulation, register the clock before `AddCaching`:

```csharp
services.AddSingleton<TimeProvider>(new FakeTimeProvider(start));
services.AddCaching(b => b.AddMemory());
```

Constructing a cache by hand takes the same `TimeProvider` as a constructor argument.
`TimeProviderExtensions.ToDateTimeOffset(...)` turns a lifetime into an expiration, saturating at
`DateTimeOffset.MaxValue` so `TimeSpan.MaxValue` stays "no TTL".
