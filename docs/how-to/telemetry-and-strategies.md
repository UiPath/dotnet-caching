# Telemetry and strategies

Two telemetry paths and five customization seams. Pick one telemetry path — they are mutually exclusive for Redis instrumentation.

## AppInsights

Default. `.AddTelemetry()` on the builder registers `CachingTelemetryProvider` as the `ICachingTelemetryProvider` implementation. `CachingTelemetryProvider` wraps `UiPath.Platform.Telemetry.ITelemetryProvider` and bridges every `TrackDependency` / `TrackEvent` / `TrackException` / `TrackMetric` call to it, materializing `ReadOnlySpan<KeyValuePair>` tag bags into dictionaries only when the span is non-empty. No allocation occurs on the hot path when the span is empty.

**Program.cs wiring:**

```csharp
using UiPath.Platform.Caching;
using UiPath.Platform.Caching.CloudEvents;
using UiPath.Platform.Caching.Polly;
using UiPath.Platform.Caching.Telemetry;
using UiPath.Platform.Caching.Redis;
using UiPath.Platform.Telemetry.AspNetCore;
using UiPath.Platform.Telemetry.AspNetCore.DynamicFilters;
using UiPath.Platform.Caching.AspNetCore;

builder.Host.ConfigureCaching(b => b
    .AddRedisConnection()
    .AddBroadcast()
    .AddRedis()
    .AddInMemoryRedis()
    .AddMemory()
    .AddResilienceStrategies()
    .AddCloudEvents()
    .AddTelemetry());                          // wires ICachingTelemetryProvider → ITelemetryProvider

builder.Services
    .AddAspNetCoreTelemetry(builder.Configuration)
    .AddRedisDynamicFilter()                   // filter Redis dependency telemetry by duration + feature flag
    .WithAdaptiveSampling()
    .WithBuiltinProcessors();

app.UseTelemetry()
   .UseMiddleware<RedisProfilerMiddleware>();  // capture per-request Redis profiler output
```

**Minimum appsettings (telemetry section only):**

```jsonc
"TelemetrySettings": {
  "ConnectionString": "InstrumentationKey=<your-ikey>",
  "DynamicFilters": {
    "RedisDependency": { "Enabled": true, "FeatureFlag": "DynamicFilters.Redis" }
  }
}
```

### RedisDynamicFilter

`AddRedisDynamicFilter()` registers `RedisDependencyFilterProcessor` in the AppInsights telemetry pipeline. The processor suppresses any `DependencyTelemetry` item whose `Type` is `"Redis"`, `Success` is `true`, and `Duration` is at or below the duration threshold (default 100 ms). The net effect: fast, routine cache traffic — the overwhelming majority of cache operations in a healthy service — does not flood AppInsights. Only slow or failing Redis calls (which indicate real problems) pass through by default.

On top of the duration gate, an optional `FeatureFlag` key can be set. When the flag is active for the current request context, the filter disables itself and all Redis dependency items flow through regardless of duration. This allows targeted per-account or per-tenant debug capture at runtime without a redeployment. Configure the feature flag name in appsettings under `DynamicFilters:RedisDependency:FeatureFlag`; the value is looked up via the platform `IFeatureProvider`.

`RedisDependencyFilterOptions` properties:

| Property | Default | Purpose |
|---|---|---|
| `Enabled` | `true` | Master switch for the filter. Set to `false` to pass all Redis items through unconditionally. |
| `DurationThresholdMSecs` | `100` | Redis items with `Duration <= threshold` and `Success == true` are dropped. |
| `FeatureFlag` | `null` | Feature flag key. When the flag is on, the duration gate is bypassed for that request. |

### RedisProfilerMiddleware

`RedisProfilerMiddleware` manages a StackExchange.Redis profiling session around each HTTP request. All Redis commands issued within the request are collected and can be emitted as a single dependency telemetry entry per request, so N Redis round-trips within one HTTP call collapse to one telemetry row rather than N rows. This is particularly valuable for services that fan out many cache reads per API call.

The middleware gates itself behind `ProfilerEnabled` on `RedisConnectionOptions`. When `ProfilerEnabled` is `false` (the default), `CreateSession` returns a no-op `IDisposable` and the middleware has no effect on throughput. When enabled, a feature-flag key (`ProfilerFeatureFlagKey`, default `"RedisProfiler.Enabled"`) is checked per request: the flag can be used to activate profiling selectively for specific tenants or debug sessions without changing config and restarting.

**Required appsettings to activate the profiler:**

```jsonc
"Caching": {
  "Connections": {
    "Redis": {
      "ProfilerEnabled": true
    }
  }
}
```

Additional profiler knobs on `RedisConnectionOptions`:

| Property | Default | Purpose |
|---|---|---|
| `ProfilerEnabled` | `false` | Enables per-request profiling sessions. |
| `ProfilerHasDefaultSession` | `true` | When `true`, a background default session captures commands that fall outside any request (e.g. background workers). |
| `ProfilerFlushInterval` | `1 s` | How often the background session is flushed and emitted as telemetry. |
| `ProfilerSessionMaxLifespan` | `1 min` | Maximum lifetime of a single profiling session before it is force-closed. |
| `ProfilerFeatureFlagKey` | `"RedisProfiler.Enabled"` | Feature flag key for per-request profiling overrides. |

### When to bridge instead

If your service has its own platform telemetry surface and you want cache events on it, implement `ICachingTelemetryProvider` and register the implementation instead of calling `.AddTelemetry()`. See [Custom `ICachingTelemetryProvider`](#custom-icachingtelemetryprovider) below and [recipes/custom-telemetry-provider.md](../recipes/custom-telemetry-provider.md).

## OpenTelemetry

Redis instrumentation is provided by the upstream OTel package (`OpenTelemetry.Instrumentation.StackExchangeRedis`). It hooks into the `IConnectionMultiplexer` at construction time via `StackExchangeRedisInstrumentation.AddConnection`. The lib creates the multiplexer internally via `IConnectionMultiplexerFactory`, which is the seam for injecting this hook. Registering `IConnectionMultiplexerFactory` is the only required code change — no other caching builder changes are needed.

The mutual exclusivity with AppInsights is a StackExchange.Redis constraint: only one profiling hook can be attached to a multiplexer. AppInsights profiling (via `RedisProfilerMiddleware`) and OTel instrumentation both use the profiler callback, so they cannot coexist. Set `ProfilerEnabled: false` in `RedisConnectionOptions` when using OTel.

### In-code wiring

```csharp
using OpenTelemetry.Instrumentation.StackExchangeRedis;
using OpenTelemetry.Trace;
using StackExchange.Redis;
using UiPath.Platform.Caching;
using UiPath.Platform.Caching.Redis;

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
    // No .AddTelemetry() here — OTel owns Redis instrumentation.
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

Reach for this when your service has its own platform telemetry surface — an internal `ITelemetryProvider` that bridges to a structured logging pipeline, an internal metrics bus, or a non-AppInsights backend — and you want cache events routed through it. Implement `ICachingTelemetryProvider` directly and register the implementation:

```csharp
services.AddSingleton<ICachingTelemetryProvider, MyBridge>();
```

Skip `.AddTelemetry()` on the caching builder — the two are mutually exclusive registrations. See [recipes/custom-telemetry-provider.md](../recipes/custom-telemetry-provider.md) for a full implementation skeleton.

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

All four tracking methods accept tag bags as `ReadOnlySpan<KeyValuePair<string, string>>` and `ReadOnlySpan<KeyValuePair<string, double>>`. This is intentional: `NullTelemetryProvider` (the default when `.AddTelemetry()` is not called) is a true no-op — the compiler elides the span parameters when the receiver is sealed and has empty method bodies, so there is zero allocation when telemetry is disabled.

The interface methods carry a `[ExcludeFromCodeCoverage]` no-op default body on the interface itself (required because Castle.Proxies cannot generate mocks for ref-struct parameters). Implementations override only the methods they want to handle; un-overridden methods inherit the no-op default.

### Materializing spans in bridge implementations

When implementing a bridge that forwards to a downstream API expecting `IDictionary<string, string>`, use `TelemetryTags.ToDictionaryOrNull` to materialize the span. The helper returns `null` (no allocation) for empty spans, and allocates a `Dictionary<TKey, TValue>` only when there are entries:

```csharp
using UiPath.Platform.Caching.Telemetry;

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

`TelemetryTags.ToDictionaryOrNull` is in `UiPath.Platform.Caching.Telemetry`. This is the same helper `CachingTelemetryProvider` uses when bridging to `ITelemetryProvider`.

### What the lib emits

The lib tracks cache operations via `ITelemetryOperation`. Each `StartOperation` call wraps a `Stopwatch`; calling `Track(hit: bool)` emits a metric with name `Caching.Stats.Hits.<provider>.<method>.<type>` (on a hit) or `Caching.Stats.Misses.<provider>.<method>.<type>` (on a miss), with the elapsed time as the value. These hit/miss counters are one category of signal the lib emits through `ICachingTelemetryProvider`; the runtime also calls `TrackEvent` and `TrackException` directly for rehydration outcomes, distributed-lock acquire/release, Redis connection-monitor state changes, and stream receipt events. Redis command telemetry (timing, command text, slot, etc.) is captured by `RedisProfilerMiddleware` or by the OTel instrumentation, not by `ICachingTelemetryProvider`.

## Cache key strategies

### Final key shape on Redis

Three segments are stitched together by the default `IRedisKeyStrategyFactory` before a key hits Redis:

```
<AppShortName>:<RedisTypePrefix>:<your key after ICacheKeyStrategy>
```

`<RedisTypePrefix>` is a short literal from `UiPath.Platform.Caching.Redis.RedisTypePrefixes` that identifies the Redis data type the key holds:

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
using UiPath.Platform.Caching;
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
