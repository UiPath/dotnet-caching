# Second Redis connection: named caching stacks

**What:** `services.AddNamedCaching("SecondaryRedis", configuration, chain)` registers a complete second caching stack on its own Redis server: `ICache`, `IHashCache`, `ISetCache`, `IDistributedLock`, `ILocalLock`, broadcast topics, connection warm-up and planned-maintenance tracking, all again, reached through keyed services under the stack's name. The stack reads the **same `Caching` section** as the primary, so every cache option is shared; only the connection differs: `Connections:SecondaryRedis` laid over `Connections:Redis`.

**When to use:**
- Two Redis servers with different lifetimes, tenants, regions or SLAs, and the application needs both.
- Migrating from one Redis to another while both are live.
- A hot, small cache and a large, cold one that must not share a connection's multiplexer or its `syncTimeout`.

## Code

```json
{
  "Caching": {
    "AppShortName": "app",
    "Connections": {
      "Redis":          { "ConnectionString": "redis-a:6379,abortConnect=false", "WarmUpOnStart": true },
      "SecondaryRedis": { "ConnectionString": "redis-b:6379,abortConnect=false" }
    }
  }
}
```

```csharp
using UiPath.Caching.Config;
using UiPath.Caching.Queue.Config;

// The primary stack, as always.
builder.Services.AddCaching(builder.Configuration, b => b
    .AddRedisConnection().AddBroadcast().AddRedis().AddInMemoryRedis().AddMemory().AddQueueInMemoryRedis());

// The second stack: the same chain minus AddRedisConnection, which the stack adds itself from Connections:SecondaryRedis.
builder.Services
    .AddNamedCaching("SecondaryRedis", builder.Configuration, b => b
        .AddBroadcast().AddRedis().AddInMemoryRedis().AddMemory().AddQueueInMemoryRedis())
    .ExposeQueueCaches();   // UiPath.Caching.Queue: ISetCache, ISetCache<T>, IQueueCacheFactory under the same name
```

```csharp
// A consumer. The primary stack is resolved as always; the secondary by key. Prefer the keyed
// ICacheFactory, as with the primary: it picks the provider from DefaultCache.
public class OrdersService(
    ICacheFactory primary,
    [FromKeyedServices("SecondaryRedis")] ICacheFactory secondary,
    [FromKeyedServices("SecondaryRedis")] IDistributedLock secondaryLock,
    [FromKeyedServices("SecondaryRedis")] ICache<Order> orders)   // typed caches are keyed too
{
    private readonly ICache _hot = primary.CreateCache();
    private readonly ICache _cold = secondary.CreateCache();
}
```

## Notes

**What is exposed.** Under the stack's name: `ICacheFactory`, `ICache`, `IHashCache`, `ICache<T>`, `IHashCache<T>`, `ICacheKeyStrategy`, `ICachePolicyFactory`, `IDistributedLock`, `ILocalLock`, `ITopicFactory`, `IRedisConnector` and `IRedisPlannedMaintenance`. The typed caches resolve the stack's own key strategy and policy factory, so they address the same keys as its unkeyed `ICache<T>` would. `ExposeQueueCaches()` from the Queue package adds `IQueueCacheFactory`, `ISetCache` and `ISetCache<T>`. Anything else the chain registers is reached with `.Expose<TService>()`, for example `.Expose<IDistributedCache>()` after `AddDistributedCache(...)` in the chain, or `.Expose<ICache>(stack => stack.GetRequiredService<ICacheFactory>().CreateCache("Redis"))` for a value built from the stack's services. Each exposure is optional, exactly as on the unkeyed side: a service the chain does not register (`IRedisPlannedMaintenance` with planned maintenance off, `ICachePolicyFactory` on a disabled stack) reads as absent rather than failing the lookup.

The stack owns what it registers and disposes it with the application container, which also tracks each exposed service, so an exposed `IDisposable` is disposed twice. `IDisposable` already requires that to be safe, and every service the library exposes honors it.

**The prerequisite.** The application's own `AddCaching` has to be there too. A stack stands beside it, reading its section, inheriting its connection and taking the one process-wide key casing from it; a stack on its own is refused when it is first resolved.

**The connection.** `AddNamedCaching` binds `Connections:Redis` first and `Connections:{name}` over it, so a key the stack's section leaves unset (`ConnectionStringExtraParams`, `WarmUpOnStart`, `ProfilerEnabled`, …) keeps the primary connection's value. Registration fails unless `Connections:{name}` carries a `ConnectionString` of its own, or `configureConnection` supplies one: inheriting every key would land the stack on the primary's server. The stack is refused again at first resolution if that connection resolves to nothing, or to the application's resolved primary one, wherever either was set, so a misspelled section or a copied value cannot silently put two stacks on one server. Neither refusal names the value, which can carry credentials. The chain must not call `AddRedisConnection`; a chain that does fails when the stack is first resolved. Settings that need code (`ConnectionFactory`, a token provider) go through `configureConnection`, applied after both sections.

**The stack's own container.** Everything the chain registers lives once per stack: the connector, the caches, the L1 memory caches of `AddInMemoryRedis` and their invalidation topics, the locks, the hosted services. Nothing Redis-facing and no memory cache is shared with the primary, so a key written through one stack is a miss through the other even on the same machine. Registrations the stack needs beyond the chain (a custom `IConnectionMultiplexerFactory`, Entra authentication, a serializer) go through `configureServices`, which runs before the chain and receives the application container for anything that must be shared:

```csharp
services.AddNamedCaching("SecondaryRedis", configuration, chain,
    configureServices: (child, root) => child.AddTransient<IConnectionMultiplexerFactory>(sp =>
        new OpenTelemetryConnectionMultiplexerFactory(sp.GetRequiredService<IOptions<RedisConnectionOptions>>(), root)));
```

| Concern | Behaviour |
|---|---|
| Redis connection, caches, L1 memory caches, set caches, locks, topics, planned maintenance, warm-up | Separate per stack. |
| Cache options, policies, `AppShortName`, provider and queue options | Shared: the same `Caching` section. Keys look identical on both servers; the server is what separates them. |
| `Connections:Redis` | Replaced, key by key, by `Connections:{name}`. |
| Logging, telemetry, clock, key masking, `JsonSerializerOptions` | Forwarded from the application container. One meter and one activity source report both stacks. |
| Resilience pipelines, CloudEvents, Entra authentication, a custom `IConnectionMultiplexerFactory` | Per stack: add them to the chain or through `configureServices`. |
| `IDistributedCache` | `AddDistributedCache(...)` in the chain, then `.Expose<IDistributedCache>()`. |
| Health check | A second `RedisHealthCheck` over the keyed connector, per [redis-health-check.md](redis-health-check.md). |

**A separate section.** When the second server needs different *cache* options too, pass `sectionName`: `AddNamedCaching("cold", configuration, chain, sectionName: "CachingCold")` binds everything from `CachingCold`, with the connection at `CachingCold:Connections:cold`. The two sections must agree on `KeyCasing`, because `AddCaching` seeds the process-wide `CacheKey.DefaultCasing`; a stack that differs is refused, naming both values, whether the casing came from its section or from a `Configure<CacheOptions>` in `configureServices`.

**Warm-up.** Set `WarmUpOnStart` on the primary connection (the stack inherits it). A connection is otherwise opened by the first *write* through `ICache` or `IHashCache`; the operations that check `IsConnected` first (every `ISetCache` call, L2 reads) do nothing while it is closed and never open it themselves. With warm-up, the stack's hosted services, started with the host, open the second connection at startup, and both servers log a handshake as the app starts.

**Names.** The name is the DI key and the connection section, so it is case-sensitive as a key. `Redis` is refused, since it names the primary connection, and so is a second `AddNamedCaching` with the same name.

The keyed `IRedisConnector` is exposed for the health check and connection diagnostics only. Application code should not talk to it directly, see [avoid-raw-iredisconnector.md](avoid-raw-iredisconnector.md).

The sample wires this exactly as shown above, with a second Redis container provisioned by the Aspire host; see [sample-app.md](../sample-app.md).

## When not to use

- One Redis server, separate keyspaces: use `RedisCacheOptions.KeyPrefix` or a key strategy, see [app-version-prefix.md](app-version-prefix.md). A second connection to the same server buys nothing.
- One connection, a second *provider*: register another `ICacheProvider` and select it by name through `ICacheFactory.CreateCache(name)`, see [how-to/extending.md](../how-to/extending.md#custom-cache-provider).

## See also

- [configure-caching-extension.md](configure-caching-extension.md) — the wiring shape both stacks use.
- [redis-health-check.md](redis-health-check.md)
- [reference/settings.md](../reference/settings.md#cachingconnectionsname-named-caching-stacks) — the connection section of a named stack.
