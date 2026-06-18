# Quickstart

Get a working `ICache<MyDto>` injected and a sensible `appsettings.json` in under five minutes.

## 1. Install

Add the three packages a typical consumer needs:

```bash
dotnet add package UiPath.Caching
dotnet add package UiPath.Caching.Polly
dotnet add package UiPath.Caching.CloudEvents
```

`UiPath.Caching.Abstractions` is pulled in transitively by `Runtime`, so you don't need to add it explicitly.

**Optional**, depending on your platform:

- `UiPath.Caching.OpenTelemetry` — wires the lib's `ICachingTelemetryProvider` to an OpenTelemetry `ActivitySource` + `Meter` named `UiPath.Caching` via `.AddOpenTelemetry()` on the caching builder.

To collect cache telemetry, add `AddSource("UiPath.Caching")` / `AddMeter("UiPath.Caching")` to your OTel providers and follow [how-to/telemetry-and-strategies.md](how-to/telemetry-and-strategies.md).

## 2. Wire DI

The recommended shape is a static `ConfigureCaching(this IServiceCollection, IConfiguration, ...)` extension that wraps `AddCaching`. Do that.

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using UiPath.Caching;
using UiPath.Caching.CloudEvents;
using UiPath.Caching.Config;
using UiPath.Caching.Polly;
using UiPath.Caching.Redis;

public static class CachingExtensions
{
    public static IServiceCollection ConfigureCaching(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection("Caching");
        services.AddCaching(section, Configure, options =>
        {
            section.Bind(options);
            options.AppShortName = "my-service";   // REQUIRED in practice — AddCaching throws on blank
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

Then call it from `Program.cs`:

```csharp
builder.Services.ConfigureCaching(builder.Configuration);
```

**Why `AppShortName` matters:** every cache key is prefixed with this value. Without it, multiple services sharing a Redis instance step on each other's keys. Treat it as required and validate at startup.

**Why the `Configure` chain in that order:** `AddRedisConnection` must come first (the rest depend on it). `AddBroadcast` registers the topic factory (used for L1 invalidation across nodes). `AddRedis` + `AddInMemoryRedis` + `AddMemory` register the three cache providers (your `DefaultCache` setting picks one). `AddResilienceStrategies` wraps every cache call in a Polly pipeline. `AddCloudEvents` wraps broadcast events in CNCF CloudEvents envelopes.

## 3. Minimum `appsettings.json`

```json
{
  "Caching": {
    "AppShortName": "my-service",
    "Connections": {
      "Redis": {
        "ConnectionString": "localhost:6379",
        "ConnectionStringExtraParams": "abortConnect=false,connectRetry=2,syncTimeout=2500,connectTimeout=2500"
      }
    }
  }
}
```

That's it. Everything else has a sensible default. For the full set of options, see [`Sample.AspNetCore/appsettings.all.json`](../Sample.AspNetCore/appsettings.all.json) (every binding-visible knob) and [reference/settings.md](reference/settings.md) (the readable index).

## 4. Use it

> ### Use `ICache<T>` / `IHashCache<T>`, not the non-generic surface
>
> Regular consumers inject `ICache<T>` or `IHashCache<T>` via an `ICacheFactory` extension method. The typed surface is compile-safe and composes with one `ICacheKeyStrategy`. To get **named-policy resolution** (TTLs/rehydration from `CacheOptions.Policies[typeof(T).FullName]`), also pass an `ICachePolicyFactory` to the `Cache<T>` constructor — see the snippet below. Without it, `Cache<T>.Policy` stays `null` and only the underlying cache's default policy applies.
>
> `ICache` / `IHashCache` exist for dynamic-key scenarios — they're the power-user surface. If you find yourself reaching for them, double-check the typed surface won't do.

Define a typed-cache extension method on `ICacheFactory`:

```csharp
using UiPath.Caching;

public static class CacheFactoryExtensions
{
    private static readonly ICacheKeyStrategy UserStrategy =
        new PrefixCacheKeyStrategy("user", CacheOptions.KeySeparator);

    // Pre-resolve the policy by typeof(User).FullName so Cache<T>'s snapshot Policy is set
    // from CacheOptions.Policies. The base ctor takes a resolved CachePolicy?, not a factory.
    public static ICache<User> Users(this ICacheFactory factory, ICachePolicyFactory policies) =>
        new Cache<User>(factory.CreateCache(KnownCacheProviderNames.InMemoryRedis), UserStrategy,
            policies?.Resolve(typeof(User).FullName!));
}
```

Inject and use:

```csharp
public class UserService(
    ICacheFactory cacheFactory,
    ICachePolicyFactory policies,
    IUserRepository repository)
{
    private readonly ICache<User> _cache = cacheFactory.Users(policies);

    public ValueTask<User?> GetUserAsync(int userId, CancellationToken token) =>
        _cache.GetOrAddAsync(
            cacheKey: userId,
            generator: ct => repository.LoadAsync(userId, ct),
            expiration: TimeSpan.FromMinutes(5),
            token: token);
}
```

The extension-method pattern scales: each cache gets its own `ICacheKeyStrategy` (e.g. `"user:"`, `"order:"`, `"tenant:"`) so keys never collide. It's common to see a single service define dozens of these extensions, one per typed cache.

For richer patterns — provider fallback when Redis is disabled, app-version prefixes for deploy-time invalidation, MediatR caching behaviors — see [recipes/](recipes/).

## 5. Verify

Run your app and look for:

- **Logs** — `Caching.*` log scopes; SE.Redis connection events at Information level.
- **Telemetry** — `cache.*` activities and metrics on the `UiPath.Caching` OTel source/meter (if you wired `.AddOpenTelemetry()` + `AddSource`/`AddMeter`), `Redis.*` command spans in OpenTelemetry (if you wired the OTel multiplexer factory).
- **RedisInsight** — keys prefixed with `my-service:user:*` (where `my-service` is your `AppShortName` and `user` is the strategy prefix).

If you see `my-service:user:42` after a call to `GetUserAsync(42, ct)`, the typed surface is wired correctly.

## 6. What next?

Pick the next page based on what you need:

- **Build the mental model** — [concepts.md](concepts.md). Architecture overview: providers, layers, topics, locks, policies, telemetry, and the two surfaces.
- **Wire Redis into the ASP.NET health probe** — [recipes/redis-health-check.md](recipes/redis-health-check.md). The shipped `RedisHealthCheck` reports red on connection failure but stays green during Azure Redis planned maintenance.
- **Need resilience knobs** — [how-to/resilience.md](how-to/resilience.md). Single-flight, hydrating cache, jitter, named policies, Polly.
- **Need cross-node sync** — [how-to/broadcast.md](how-to/broadcast.md). Streams vs Pub/Sub, per-topic options, the notify doorbell.
- **Need per-field caching** — [how-to/hash-cache.md](how-to/hash-cache.md). `IHashCache<T>`, metadata, bundled GET+TTL.
- **Need OpenTelemetry or custom key strategies** — [how-to/telemetry-and-strategies.md](how-to/telemetry-and-strategies.md).
- **Want short patterns to paste** — [recipes/](recipes/).
