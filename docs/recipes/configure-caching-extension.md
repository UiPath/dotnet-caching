# ConfigureCaching extension wrapper

**What:** Wrap `AddCaching(...)` in a static `ConfigureCaching(this IServiceCollection, IConfiguration, ...)` extension. This is the canonical shape consumer services use.

**When to use:**
- Always — this is the canonical wiring shape.
- The `IConfigurationSection`-bound overload makes the binding source explicit (vs. global `IConfiguration` look-ups inside the chain).
- Keeps `Program.cs` to a single call: `services.ConfigureCaching(configuration);`.

## Code

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using UiPath.Caching;
using UiPath.Caching.CloudEvents;
using UiPath.Caching.Config;
using UiPath.Caching.Polly;

public static class CachingExtensions
{
    public static IServiceCollection ConfigureCaching(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var section = configuration.GetSection("Caching");

        services.AddCaching(
            section,
            Configure,
            options =>
            {
                section.Bind(options);
                options.AppShortName = "my-service";  // REQUIRED in practice
            });

        return services;

        static void Configure(ICachingBuilder builder) => builder
            .AddRedisConnection()
            .AddBroadcast()
            .AddRedis()
            .AddInMemoryRedis()
            .AddMemory()
            .AddResilienceStrategies()
            .AddCloudEvents();
    }
}
```

## Notes

The `AddCaching(IConfigurationSection?, Action<ICachingBuilder>?, Action<CacheOptions>?)` overload binds every options class from the section automatically — `CacheOptions` from the root, `RedisConnectionOptions` from `Connections:Redis`, etc. The `Action<CacheOptions>` delegate is your hook to stamp values from code, typically `AppShortName` derived from a service name constant.

The local `static void Configure` function captures nothing from the enclosing scope, so the builder chain can be tested in isolation and refactored without touching `ConfigureCaching`'s signature.

The chain order matters: `AddRedisConnection` registers the `IRedisConnector`, which everything else depends on. `AddBroadcast` registers the topic factory. Cache providers (`AddRedis`, `AddInMemoryRedis`, `AddMemory`) come after, in any order — your `DefaultCache` setting picks which one `factory.CreateCache()` returns by default.

`AddResilienceStrategies` and `AddCloudEvents` are pure decorators on top of whatever cache providers are present, so they go last.

## When not to use

- Single-purpose tools or tests that don't have an `IServiceCollection` at all — wire the cache directly via `new MultilayerCache(...)` instead.
- Services that intentionally don't bind from configuration (rare; you can still use this shape with an inline `IConfiguration` source).

## See also

- [quickstart.md](../quickstart.md)
- [how-to/resilience.md](../how-to/resilience.md)
- [reference/settings.md](../reference/settings.md)
