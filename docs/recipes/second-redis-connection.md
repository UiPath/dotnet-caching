# Second Redis connection

**What:** Run two complete caching stacks in one application, each on its own Redis server: `ICache`, `IHashCache`, `ISetCache`, `IDistributedLock`, broadcast topics, connection warm-up and planned-maintenance tracking, twice. The library wires one Redis connection per container, so the second stack is built by running `AddCaching` again in a *child* `ServiceCollection` over the **same `Caching` section with only the connection replaced**, and the pieces consumers resolve are re-exposed in the application container as keyed services.

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
using Microsoft.Extensions.Options;
using UiPath.Caching;
using UiPath.Caching.Broadcast;
using UiPath.Caching.Config;
using UiPath.Caching.Locking;
using UiPath.Caching.Polly;
using UiPath.Caching.Queue.Config;
using UiPath.Caching.Redis;
using UiPath.Caching.Telemetry;

public static class SecondaryCaching
{
    public const string Key = "secondary";

    private const string PrimaryConnectionSection = "Caching:Connections:Redis";
    private const string SecondaryConnectionSection = "Caching:Connections:SecondaryRedis";

    public static IServiceCollection AddSecondaryCaching(this IServiceCollection services, IConfiguration configuration)
    {
        var childConfiguration = WithSecondaryConnection(configuration);

        services.AddKeyedSingleton<IServiceProvider>(Key, (root, _) => BuildChild(root, childConfiguration));

        services.AddKeyedSingleton<ICacheFactory>(Key, (sp, key) => Child(sp, key).GetRequiredService<ICacheFactory>());
        services.AddKeyedSingleton<IQueueCacheFactory>(Key, (sp, key) => Child(sp, key).GetRequiredService<IQueueCacheFactory>());
        services.AddKeyedSingleton<IDistributedLock>(Key, (sp, key) => Child(sp, key).GetRequiredService<IDistributedLock>());
        services.AddKeyedSingleton<ITopicFactory>(Key, (sp, key) => Child(sp, key).GetRequiredService<ITopicFactory>());
        services.AddKeyedSingleton<IRedisConnector>(Key, (sp, key) => Child(sp, key).GetRequiredService<IRedisConnector>());
        services.AddKeyedSingleton<ICache>(Key, (sp, key) => Child(sp, key).GetRequiredService<ICacheFactory>().CreateCache());
        services.AddKeyedSingleton<IHashCache>(Key, (sp, key) => Child(sp, key).GetRequiredService<ICacheFactory>().CreateHashCache());
        services.AddKeyedSingleton<ISetCache>(Key, (sp, key) => Child(sp, key).GetRequiredService<ISetCache>());

        // The child's hosted services (connection warm-up, planned maintenance, stream health maintainer)
        // only run if the root host starts them.
        services.AddHostedService(sp => new ChildHostedServices(Child(sp, Key)));
        return services;
    }

    // The application configuration, with every value under Connections:SecondaryRedis laid over Connections:Redis.
    private static IConfiguration WithSecondaryConnection(IConfiguration configuration)
    {
        var overrides = configuration.GetSection(SecondaryConnectionSection)
            .AsEnumerable(makePathsRelative: true)
            .Where(kv => kv.Value is not null)
            .ToDictionary(kv => $"{PrimaryConnectionSection}:{kv.Key}", kv => kv.Value);

        if (overrides.Count == 0)
        {
            throw new InvalidOperationException($"'{SecondaryConnectionSection}' is empty; the secondary caching stack needs at least a ConnectionString there.");
        }

        return new ConfigurationBuilder().AddConfiguration(configuration).AddInMemoryCollection(overrides).Build();
    }

    private static IServiceProvider Child(IServiceProvider root, object? key) =>
        root.GetRequiredKeyedService<IServiceProvider>(key);

    private static ServiceProvider BuildChild(IServiceProvider root, IConfiguration configuration)
    {
        var child = new ServiceCollection();

        // Shared with the primary stack: one logger, one clock, one meter and activity source.
        child.AddSingleton(root.GetRequiredService<ILoggerFactory>());
        child.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        child.AddSingleton(root.GetRequiredService<TimeProvider>());
        child.AddSingleton(root.GetRequiredService<ICachingTelemetryProvider>());

        // The same chain as the primary, over the same "Caching" section.
        child.AddCaching(configuration, b => b.AddRedisConnection().AddBroadcast().AddRedis().AddInMemoryRedis().AddMemory()
                                              .AddQueueInMemoryRedis().AddResilienceStrategies().AddCloudEvents());

        return child.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private sealed class ChildHostedServices(IServiceProvider child) : IHostedService
    {
        private readonly IHostedService[] _services = [.. child.GetServices<IHostedService>()];

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            foreach (var service in _services) await service.StartAsync(cancellationToken);
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            foreach (var service in _services.Reverse()) await service.StopAsync(cancellationToken);
        }
    }
}
```

```csharp
// Program.cs
builder.Services.AddCaching(builder.Configuration, b => b.AddRedisConnection().AddBroadcast().AddRedis()...);
builder.Services.AddSecondaryCaching(builder.Configuration);

// A consumer. The primary stack is resolved as always; the secondary by key. Prefer the keyed
// ICacheFactory, as with the primary: it picks the provider from DefaultCache.
public class OrdersService(ICacheFactory primary, [FromKeyedServices(SecondaryCaching.Key)] ICacheFactory secondary,
                           [FromKeyedServices(SecondaryCaching.Key)] IDistributedLock secondaryLock)
{
    private readonly ICache _hot = primary.CreateCache();
    private readonly ICache _cold = secondary.CreateCache();
}
```

## Notes

The child container is the whole second stack. Everything `AddCaching` and the builder chain register in it, from the `RedisConnector` down to the broadcast topic providers and the L1 memory caches of `AddInMemoryRedis`, lives once per container, so nothing Redis-facing is shared. Disposing the application container disposes the keyed `IServiceProvider` and with it the child graph.

The child reads the **same** `Caching` section, so `AppShortName`, `KeyCasing`, policies, provider options and the queue options are identical by construction. Only `Connections:Redis` differs: every key under `Connections:SecondaryRedis` is laid over it, so the second server can also carry its own `ConnectionStringExtraParams`, `WarmUpOnStart` or `ProfilerEnabled` when it needs them, and inherits the primary's values otherwise. If the second server must differ in *cache* options too, bind the child from its own section instead: `child.AddCaching(configuration.GetSection("CachingSecondary"), chain, opt => section.Bind(opt))`; the two sections then have to agree on `KeyCasing`, because `AddCaching` seeds the process-wide `CacheKey.DefaultCasing`.

What is shared and what is not:

| Concern | Behaviour |
|---|---|
| Redis connection, caches, set caches, locks, topics, planned maintenance, warm-up | Separate per container. The child's L1/L2 invalidation rides the second connection. |
| Cache options, policies, `AppShortName`, provider and queue options | Shared: the same `Caching` section. Keys look identical on both servers; the server is what separates them. |
| `Connections:Redis` | Replaced by `Connections:SecondaryRedis`, key by key. |
| Logging, telemetry, clock | Forwarded from the root, as in the code above. One meter and one activity source report both stacks. |
| Resilience pipelines, CloudEvents, Entra authentication, a custom `IConnectionMultiplexerFactory` | Registered again in the child chain. Add `AddAzureEntraAuthentication()` or your factory to the child if the second server needs them. |
| `IDistributedCache` | Call `AddDistributedCache(...)` in the child chain too, then bridge `IDistributedCache` like the other services. |
| Health check | Add a second `RedisHealthCheck` over the keyed connector, per [redis-health-check.md](redis-health-check.md). |

The `ILogger<>` registration matters: the library's classes take `ILogger<T>`, and forwarding only `ILoggerFactory` would leave nothing to build them from. `AddLogging()` in the child would create a second, unconfigured logger factory instead.

`ChildHostedServices` starts the child's services after the root's own, in registration order, and stops them in reverse. Register it after the primary `AddCaching` so the primary connection warms up first.

Set `WarmUpOnStart` on the primary connection (the secondary inherits it). A connection is otherwise opened by the first *write* through `ICache` or `IHashCache`; the operations that check `IsConnected` first (every `ISetCache` call, L2 reads) do nothing while it is closed and never open it themselves. With warm-up, the bridged hosted service opens the second connection at startup, which is also the easiest way to see the bridge working: both servers log a handshake as the app starts.

Typed caches (`ICache<T>`, `IHashCache<T>`, `ISetCache<T>`) on the second stack: resolve the keyed `ICacheFactory` or `IQueueCacheFactory` and use the factory extension methods, see [factory-extension-methods.md](factory-extension-methods.md).

The keyed `IRedisConnector` is exposed for the health check and connection diagnostics only. Application code should not talk to it directly, see [avoid-raw-iredisconnector.md](avoid-raw-iredisconnector.md).

The sample wires this exactly as shown above, with a second Redis container provisioned by the Aspire host; see [sample-app.md](../sample-app.md).

## When not to use

- One Redis server, separate keyspaces: use `RedisCacheOptions.KeyPrefix` or a key strategy, see [app-version-prefix.md](app-version-prefix.md). A second connection to the same server buys nothing.
- One connection, a second *provider*: register another `ICacheProvider` and select it by name through `ICacheFactory.CreateCache(name)`, see [how-to/extending.md](../how-to/extending.md#custom-cache-provider).
- More than a handful of connections: the bridge above is per name. At that scale, ask for the library-level `AddNamedCaching` instead of copying it.

## See also

- [configure-caching-extension.md](configure-caching-extension.md) — the wiring shape both stacks use.
- [redis-health-check.md](redis-health-check.md)
- [reference/settings.md](../reference/settings.md) — every option `Connections:SecondaryRedis` can carry.
