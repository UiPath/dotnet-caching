# App-version cache key prefix

**What:** Bake the assembly version into the cache key prefix so deploys auto-invalidate. Old keys can never collide with new keys.

**When to use:**
- Caches whose value shape might change between deploys (e.g. cached DTOs that follow the service's contract).
- Caches that hold computed or projected data tied to the service's logic.
- When you don't want to write explicit invalidation code at every deploy.

## Code

```csharp
using System.Reflection;
using UiPath.Caching;

public static class CacheKeyStrategies
{
    private static readonly string ApplicationVersion =
        Assembly.GetEntryAssembly()!.GetName().Version!.ToString();

    public static ICacheKeyStrategy AppVersionPrefix(string prefix) =>
        new PrefixCacheKeyStrategy(
            string.Join(CacheOptions.KeySeparator, prefix, ApplicationVersion));
}
```

Use it like any other strategy:

```csharp
public static class MyCacheFactoryExtensions
{
    private static readonly ICacheKeyStrategy OrdersStrategy =
        CacheKeyStrategies.AppVersionPrefix("orders");

    public static ICache<Order> Orders(this ICacheFactory f) =>
        new Cache<Order>(f.CreateCache(KnownCacheProviderNames.InMemoryRedis), OrdersStrategy);
}
```

Cache keys for `Orders` will now look like `<AppShortName>:s:orders:1.2.3.0:<key>` instead of `<AppShortName>:s:orders:<key>` (the `s` / `h` segment between the app name and the strategy prefix is injected by `DefaultRedisKeyStrategyFactory` to disambiguate `ICache<T>` string keys from `IHashCache<T>` hash keys; an `IHashCache<Order>` keyed by the same strategy would land at `<AppShortName>:h:orders:1.2.3.0:<key>`).

## Notes

`Assembly.GetEntryAssembly()` returns the host's entry assembly — for an ASP.NET Core app that's typically the web project. Use `Assembly.GetExecutingAssembly()` if you want to version against the library's own assembly instead.

The version is captured once at type-init via `static readonly`. A redeploy equals a new process, which equals a new version captured. No need to invalidate the cached value at runtime.

The trade-off: on every deploy, the cache is fully cold. If your cache hit rate is the dominant performance driver, this can mean a window of degraded latency after each deploy until the cache warms back up. Mitigate with `CachePolicy.JitterMaxDuration` so the warm-up doesn't trigger an expiration spike a TTL-window later — see [how-to/resilience.md](../how-to/resilience.md).

The old keys aren't deleted — they age out via Redis TTL. If you have very long-lived keys (`DistributedExpiration` measured in days), you're paying memory cost for stale keys until they expire. Consider a shorter TTL or run a periodic SCAN-and-DEL operation tied to your release pipeline.

## When not to use

- Caches that must survive deploys — e.g. session caches keyed by external session ID, OAuth refresh tokens, or any cache where the consumer expects continuity across a deploy. Use explicit invalidation instead.
- Caches where the value shape is stable across deploys (e.g. external API response bodies) — the version prefix adds churn for no benefit.
- Services that deploy many times per day with low cache TTL — the cold-start cost dominates.

## See also

- [how-to/telemetry-and-strategies.md](../how-to/telemetry-and-strategies.md)
- [how-to/resilience.md](../how-to/resilience.md)
- [recipes/factory-extension-methods.md](factory-extension-methods.md)
