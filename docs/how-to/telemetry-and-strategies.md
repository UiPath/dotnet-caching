# Telemetry and strategies

Two telemetry paths and five customization seams. The two telemetry paths are complementary: the OpenTelemetry adapter routes cache-semantic signals through `ICachingTelemetryProvider`, while Redis-command instrumentation is layered on via the multiplexer-factory hook (mutually exclusive with the StackExchange.Redis profiler).

## OpenTelemetry adapter

Default. `.AddOpenTelemetry()` on the caching builder registers `UiPath.Caching.OpenTelemetry.CachingTelemetryProvider` as the `ICachingTelemetryProvider` implementation. The provider is backed by a `System.Diagnostics.ActivitySource` and a `Meter`, both named **`UiPath.Caching`**: every `TrackDependency` / `TrackEvent` / `TrackException` call starts (or annotates) an `Activity` on that source, and `TrackMetric` plus the hit/miss counters record on instruments from that `Meter`. Tag bags arrive as `ReadOnlySpan<KeyValuePair>` and are materialized only when non-empty, so no allocation occurs on the hot path when the span is empty.

You collect these signals the same way you collect any other OTel source — add the source and meter to your tracer/meter providers by name:

**Program.cs wiring:**

```csharp
using UiPath.Caching;
using UiPath.Caching.CloudEvents;
using UiPath.Caching.Polly;
using UiPath.Caching.Redis;

builder.Host.ConfigureCaching(b => b
    .AddRedisConnection()
    .AddBroadcast()
    .AddRedis()
    .AddInMemoryRedis()
    .AddMemory()
    .AddResilienceStrategies()
    .AddCloudEvents()
    .AddOpenTelemetry());                       // registers CachingTelemetryProvider (ActivitySource + Meter "UiPath.Caching")

builder.Services
    .AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddSource("UiPath.Caching"))           // collect the caching ActivitySource
    .WithMetrics(metrics => metrics
        .AddMeter("UiPath.Caching"));           // collect the caching Meter
```

The `AddSource("UiPath.Caching")` / `AddMeter("UiPath.Caching")` calls are what connect the provider's `ActivitySource` and `Meter` to your exporters. Omit them and the signals are emitted but never collected.

### When to bridge instead

If your service has its own platform telemetry surface and you want cache events on it directly, implement `ICachingTelemetryProvider` and register the implementation instead of calling `.AddOpenTelemetry()`. See [Custom `ICachingTelemetryProvider`](#custom-icachingtelemetryprovider) below and [recipes/custom-telemetry-provider.md](../recipes/custom-telemetry-provider.md).

## OpenTelemetry Redis instrumentation

The adapter above emits cache-semantic signals; to also capture raw Redis **command** spans (timing, command text, slot), add the upstream OTel package (`OpenTelemetry.Instrumentation.StackExchangeRedis`). It hooks into the `IConnectionMultiplexer` at construction time via `StackExchangeRedisInstrumentation.AddConnection`. The lib creates the multiplexer internally via `IConnectionMultiplexerFactory`, which is the seam for injecting this hook. Registering `IConnectionMultiplexerFactory` is the only required code change — no other caching builder changes are needed.

Redis command instrumentation hooks the StackExchange.Redis profiler callback, and only one profiling hook can be attached to a multiplexer. Set `ProfilerEnabled: false` in `RedisConnectionOptions` when using OTel Redis instrumentation so the two do not contend for the callback.

### In-code wiring

```csharp
using OpenTelemetry.Instrumentation.StackExchangeRedis;
using OpenTelemetry.Trace;
using StackExchange.Redis;
using UiPath.Caching;
using UiPath.Caching.Redis;

builder.Services
    .AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddRedisInstrumentation(inst => inst.SetVerboseDatabaseStatements = true));

builder.Host.ConfigureCaching(b =>
{
    b.Services.AddTransient<IConnectionMultiplexerFactory, OpenTelemetryConnectionMultiplexerFactory>();
    b.AddRedisConnection()
     .AddBroadcast()
     .AddRedis()
     .AddInMemoryRedis()
     .AddMemory()
     .AddResilienceStrategies()
     .AddCloudEvents();
    // No profiler-based instrumentation here — OTel owns Redis command spans.
});
```

`AddRedisInstrumentation` must be called before the multiplexer is created (i.e., before the host starts). Calling it after `ConnectionMultiplexer.Connect` has already run means the `StackExchangeRedisInstrumentation` instance is not yet registered in DI, so `serviceProvider.GetService<StackExchangeRedisInstrumentation>()` in your factory will return `null` and no spans will be emitted. The setup above is safe because the multiplexer is created lazily on first use, after the host finishes building.

See [recipes/opentelemetry-multiplexer-factory.md](../recipes/opentelemetry-multiplexer-factory.md) for the full `OpenTelemetryConnectionMultiplexerFactory` implementation. A working reference is also available at `Sample.AspNetCore/OpenTelemetryConnectionMultiplexerFactory.cs`.

### Appsettings-only wiring

For services that prefer config over code, register the multiplexer-factory type by assembly-qualified string:

```jsonc
"Caching": {
  "Connections": {
    "Redis": {
      "ConnectionMultiplexerFactoryType":
        "MyApp.Telemetry.OpenTelemetryConnectionMultiplexerFactory, MyApp.Telemetry"
    }
  }
}
```

The lib reads `ConnectionMultiplexerFactoryType` from `RedisConnectionOptions` and registers the type with DI as `IConnectionMultiplexerFactory` (`TryAddTransient`). Any constructor the container can satisfy is valid — the sample's `(IOptions<RedisConnectionOptions>, IServiceProvider)` shape is just one working example. If the type string is missing or the assembly cannot be found, the default `ConnectionMultiplexerFactory` is used. **Note:** a misformatted assembly-qualified name (e.g. missing the `, AssemblyName` suffix) makes `Type.GetType` return `null` and the lib silently falls back to the default factory with no log warning — verify the name parses by spot-checking that OTel Redis spans actually appear in your tracing backend after the change.

## Custom `ICachingTelemetryProvider`

Reach for this when your service has its own telemetry surface — a structured logging pipeline, an internal metrics bus, or a backend you address directly rather than through OpenTelemetry — and you want cache events routed through it. Implement `ICachingTelemetryProvider` directly and register the implementation:

```csharp
services.AddSingleton<ICachingTelemetryProvider, MyBridge>();
```

Skip `.AddOpenTelemetry()` on the caching builder — the two are mutually exclusive registrations of `ICachingTelemetryProvider`. See [recipes/custom-telemetry-provider.md](../recipes/custom-telemetry-provider.md) for a full implementation skeleton.

### Interface

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

All four tracking methods accept tag bags as `ReadOnlySpan<KeyValuePair<string, string>>` and `ReadOnlySpan<KeyValuePair<string, double>>`. This is intentional: `NullTelemetryProvider` (the default when no `ICachingTelemetryProvider` is registered) is a true no-op — the compiler elides the span parameters when the receiver is sealed and has empty method bodies, so there is zero allocation when telemetry is disabled.

The interface methods carry a `[ExcludeFromCodeCoverage]` no-op default body on the interface itself (required because Castle.Proxies cannot generate mocks for ref-struct parameters). Implementations override only the methods they want to handle; un-overridden methods inherit the no-op default.

### Materializing spans in bridge implementations

When implementing a bridge that forwards to a downstream API expecting `IDictionary<string, string>`, use `TelemetryTags.ToDictionaryOrNull` to materialize the span. The helper returns `null` (no allocation) for empty spans, and allocates a `Dictionary<TKey, TValue>` only when there are entries:

```csharp
using UiPath.Caching.Telemetry;

public sealed class MyBridge(IMyMetricsSink sink) : ICachingTelemetryProvider
{
    public void TrackEvent(
        string eventName,
        ReadOnlySpan<KeyValuePair<string, string>> properties = default,
        ReadOnlySpan<KeyValuePair<string, double>> metrics = default)
    {
        sink.Emit(eventName,
            TelemetryTags.ToDictionaryOrNull(properties),
            TelemetryTags.ToDictionaryOrNull(metrics));
    }

    // implement TrackDependency, TrackException, TrackMetric similarly
}
```

`TelemetryTags.ToDictionaryOrNull` is in `UiPath.Caching.Telemetry`. This is the same helper the OpenTelemetry adapter's `CachingTelemetryProvider` uses when materializing tag bags.

### What the lib emits

The lib tracks cache operations via `ITelemetryOperation`. Each `StartOperation` call wraps a `Stopwatch`; the operation emits a metric named `Caching.Stats.Hits.<provider>.<method>.<type>` (on a hit) or `Caching.Stats.Misses.<provider>.<method>.<type>` (on a miss), with the elapsed time as the value. The metric is emitted **once per operation** (a multi-key read is a hit when any key hit) and carries a `Keys` dimension with the operation's key count. These hit/miss counters are one category of signal the lib emits through `ICachingTelemetryProvider`; the runtime also calls `TrackEvent` and `TrackException` directly for rehydration outcomes, distributed-lock acquire/release, Redis connection-monitor state changes, and stream receipt events. Redis command telemetry (timing, command text, slot, etc.) is captured by the OTel Redis instrumentation via the multiplexer-factory hook, not by `ICachingTelemetryProvider`.

When `RedisCacheOptions.KeyReadTelemetryEnabled` is set, read paths additionally emit a per-key `Redis` dependency (key in `data`, hit/miss via `resultCode`, a `BatchId` shared across the operation), so per-key read attribution is available even when batched `MGET`+TTL reads — bundled into one `MULTI`/`EXEC` transaction — no longer surface the individual keys on the wire. It is opt-in because raw keys are high-cardinality. Hash reads emit one dependency per hash key, never per field.

## Cache key strategies

### Final key shape on Redis

Three segments are stitched together by the default `IRedisKeyStrategyFactory` before a key hits Redis:

```
<AppShortName>:<RedisTypePrefix>:<your key after ICacheKeyStrategy>
```

`<RedisTypePrefix>` is a short literal from `UiPath.Caching.Redis.RedisTypePrefixes` that identifies the Redis data type the key holds:

| Constant | Value | Used for |
|---|---|---|
| `RedisTypePrefixes.String` | `s` | `ICache` / `ICache<T>` (Redis STRING via `SET`/`GET`) |
| `RedisTypePrefixes.Hash` | `h` | `IHashCache` / `IHashCache<T>` (Redis HASH via `HSET`/`HGET`) |
| `RedisTypePrefixes.Streams` | `st` | Redis Streams topic keys (`XADD` / `XREADGROUP`) |
| `RedisTypePrefixes.PubSub` | `ps` | Pub/Sub channel names |

Worked example. With `AppShortName: "my-service"`, separator `:`, and an `ICacheFactory` extension that wraps `ICache<User>` in a `PrefixCacheKeyStrategy("user")`, the call `cache.SetAsync("42", user, ...)` produces the Redis key `my-service:s:user:42`. The corresponding `IHashCache<UserField>` extension produces `my-service:h:user:42`. **They cannot collide**: the same logical key in code resolves to a different Redis key per cache type, so an `ICache<T>` write (STRING) and an `IHashCache<T>` write (HASH) never produce a `WRONGTYPE` error against the same Redis key. Stream keys (`my-service:st:<topic>`) and Pub/Sub channels (`my-service:ps:<channel>`) live in their own segments for the same reason.

The type prefix is inserted by `DefaultRedisKeyStrategyFactory` based on whether the registered cache implements `ICache` or `IHashCache`. Overriding `RedisCacheOptions.RedisKeyStrategyFactory` lets you replace this behavior wholesale, but the default is the right call for almost every consumer — opt out only when you have a legacy Redis layout you must match.

### The `ICacheKeyStrategy` seam

`ICacheKeyStrategy` rewrites the *logical* key (the third segment above) before the Redis key factory adds the type-prefix and app-prefix segments. Two built-ins are provided:

- `DefaultCacheKeyStrategy` — pass-through; the key reaches the store unmodified (apart from any `AppShortName` / `KeyPrefix` prepended by the provider).
- `PrefixCacheKeyStrategy(prefix, separator)` — prepends `prefix + separator` to every key. Used as the default for typed caches constructed via `Cache<T>(provider, strategy)`.

Register a custom strategy per-cache-provider, e.g.:

```csharp
builder.Host.ConfigureCaching(b => b
    .AddRedis(opt => opt.CacheKeyStrategy = new PrefixCacheKeyStrategy("v2", CacheOptions.KeySeparator)));
```

For an app-wide default, set `CacheKeyStrategy` on each provider's options (`RedisCacheOptions`, `InMemoryRedisCacheOptions`, `InMemoryCacheOptions`) in the builder action — the library does not honor a single global key-strategy factory.

### App-version prefix

A `PrefixCacheKeyStrategy` that bakes the assembly version into the prefix gives you free deploy invalidation — old keys cannot collide with new keys because the version segment in the key changes with every deploy.

```csharp
using UiPath.Caching;
using System.Reflection;

public static class CacheKeyStrategies
{
    private static readonly string ApplicationVersion =
        Assembly.GetEntryAssembly()!.GetName().Version!.ToString();

    public static ICacheKeyStrategy AppVersionPrefix(string prefix) =>
        new PrefixCacheKeyStrategy(
            string.Join(CacheOptions.KeySeparator, prefix, ApplicationVersion),
            CacheOptions.KeySeparator);
}
```

`PrefixCacheKeyStrategy` accepts `char?` as the separator parameter (not `string`). `CacheOptions.KeySeparator` is `const char ':'`, so it satisfies the `char?` parameter directly. The prefix string is lowercased by the constructor.

Every cache key carries the deployed version, so a new deploy writes to `users:1.2.0:...` while the prior deploy's `users:1.1.0:...` keys age out via TTL. There is no explicit invalidation step — the old keyspace expires on its own. The tradeoffs:

- Every deploy starts with a cold cache. For services where warm-up latency is critical, consider a blue/green key scheme (write new-version keys during rollout while reads still fall back to old-version keys) or a pre-warming job.
- Caches that need to survive deploys — for example, session caches keyed by an external session ID that clients hold across service upgrades — must not use this pattern. Those caches require explicit invalidation logic keyed to the session lifecycle, not to the deployed binary version.

See [recipes/app-version-prefix.md](../recipes/app-version-prefix.md) for the full pattern including when not to use it.

## Redis-side strategies

Four code-only seams shape how the library names things on Redis. None bind from JSON config; all are set via builder options or directly on options objects in code.

| Seam | Interface | Purpose | Set on |
|---|---|---|---|
| Redis key factory | `IRedisKeyStrategyFactory` | Builds the function combining `AppShortName + differentiator + cache key` into the final Redis key string. Default handles `ICache` (string type prefix) and `IHashCache` (hash type prefix), with optional shard-key support. | `RedisCacheOptions.RedisKeyStrategyFactory` |
| Streams key | `IRedisStreamKeyStrategy` | Builds the Redis key for a Streams topic (the key of the stream itself). | `RedisStreamsTopicOptions.RedisStreamKeyStrategy` |
| Pub/Sub channel | `IRedisChannelStrategy` | Builds the channel name for a Pub/Sub topic or the streams notify doorbell. | `RedisPubSubTopicOptions.RedisChannelStrategy` / `RedisStreamsTopicOptions.NotifyChannelStrategy` |
| Distributed lock key | `IDistributedLockKeyStrategy` | Builds the lock key for `RedisDistributedLock`. Default appends `":lck"` (separator + `"lck"`) to `CacheKey.Name`. | `IMultilayerCacheOptions.LockKeyStrategy` |

Register on the relevant builder method, for example:

```csharp
builder.Host.ConfigureCaching(b => b
    .AddRedisStreams(opt => opt.RedisStreamKeyStrategy = new MyStreamKeyStrategy())
    .AddRedisPubSub(opt => opt.RedisChannelStrategy = new MyChannelStrategy()));
```

### Sharded Pub/Sub constraint

When `NotifyShardedPubSub` is `true` on `RedisStreamsTopicOptions`, the stream key and the doorbell channel must share a Redis Cluster hash tag so they hash to the same slot. If you supply a custom `IRedisStreamKeyStrategy` whose keys contain `{`/`}` but do not form a valid hash tag (e.g. `app:st:{}topicA`), topic construction throws `InvalidOperationException`. Either produce well-formed hash-tagged keys, or supply a matching `NotifyChannelStrategy` that derives the channel name from the same hash tag. See [how-to/broadcast.md](broadcast.md#sharded-pubsub-redis-7) for the full ruleset.

### When to reach for these seams

Most consumers never override any of these seams. The built-in `DefaultRedisKeyStrategyFactory` covers `ICache` and `IHashCache` and handles shard-key routing automatically when `CacheOptions.ShardKeyEnabled` is `true`. Reasons to override:

- **Tenant/region shard routing** — you want Redis keys to encode tenant or region info as a Redis Cluster hash tag (`{tag}`) so related keys cluster to the same slot and cross-slot multi-key commands work correctly.
- **Legacy key layout compatibility** — you are sharing a Redis instance with an existing system that has a fixed key schema you must match. Overriding the factory lets you produce keys that match the legacy layout without changing the cache call sites.
- **Test isolation** — a strategy that prepends a per-test-run GUID prefix keeps parallel integration test runs from sharing state or interfering with each other's keys.

Overriding `IRedisKeyStrategyFactory` changes the key layout for all caches on that provider. When you do this, the streams maintainer's `MaintainerSearchPattern` (a Redis glob) must be updated to match your custom key format — otherwise the maintainer will either miss topics or sweep unrelated keys. Set `RedisStreamsTopicOptions.MaintainerSearchPattern` explicitly after overriding the factory. See [how-to/broadcast.md](broadcast.md) for details on the maintainer.

Similarly, overriding `IDistributedLockKeyStrategy` while keeping the default `ICacheKeyStrategy` (or vice versa) can cause lock keys to diverge from the cache keys they protect. If your `ICacheKeyStrategy` adds a prefix, supply a matching `IDistributedLockKeyStrategy` that adds the same prefix so lock and value keys are co-located in the keyspace. The default lock strategy does not apply your `ICacheKeyStrategy` prefix — it appends `:lck` directly to `CacheKey.Name`.
