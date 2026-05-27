# Per-cache `ICacheFactory` extension methods

**What:** Define a typed-cache extension on `ICacheFactory` per cache, with a pre-built `ICacheKeyStrategy` cached in a static field. This is the most common pattern in consumer code — a single service often defines dozens of these extensions, one per typed cache.

**When to use:**
- Always, for typed caches — this is the recommended ergonomic pattern.
- When you want each cache to have its own key prefix without re-allocating the strategy on every call.
- When you want call sites to be one line: `factory.Orders()` instead of `new Cache<Order>(factory.CreateCache(...), new PrefixCacheKeyStrategy(...))`.

## Code

```csharp
using UiPath.Platform.Caching;

public static class MyCacheFactoryExtensions
{
    private static readonly ICacheKeyStrategy OrdersStrategy =
        new PrefixCacheKeyStrategy("orders");
    private static readonly ICacheKeyStrategy UsersStrategy =
        new PrefixCacheKeyStrategy("users");
    private static readonly ICacheKeyStrategy TenantsStrategy =
        new PrefixCacheKeyStrategy("tenants");

    // Pre-resolve the named policy via the factory; the base Cache<T> / HashCache<T> ctor takes
    // a resolved CachePolicy?, not a policy factory.
    public static ICache<Order> Orders(this ICacheFactory f, ICachePolicyFactory policies) =>
        new Cache<Order>(f.CreateCache(KnownCacheProviderNames.InMemoryRedis), OrdersStrategy,
            policies?.Resolve(typeof(Order).FullName!));

    public static ICache<User> Users(this ICacheFactory f, ICachePolicyFactory policies) =>
        new Cache<User>(f.CreateCache(KnownCacheProviderNames.InMemoryRedis), UsersStrategy,
            policies?.Resolve(typeof(User).FullName!));

    public static IHashCache<TenantField> Tenants(this ICacheFactory f, ICachePolicyFactory policies) =>
        new HashCache<TenantField>(f.CreateHashCache(KnownCacheProviderNames.InMemoryRedis), TenantsStrategy,
            policies?.Resolve(typeof(TenantField).FullName!));
}
```

Inject and use:

```csharp
public class OrderService(ICacheFactory factory, ICachePolicyFactory policies)
{
    private readonly ICache<Order> _cache = factory.Orders(policies);

    public ValueTask<Order?> GetAsync(int id, CancellationToken token) =>
        _cache.GetOrAddAsync(id, ct => LoadAsync(id, ct), token: token);
}
```

## Notes

The strategies are cached in `static readonly` fields so they're allocated once at type-init, not on every `ICacheFactory.Orders()` call. With dozens of caches, this matters: a heavy DI scope that creates services per-request would otherwise allocate one strategy instance per call.

`Cache<T>` (base constructor `Cache<T>(ICache cache, ICacheKeyStrategy?, CachePolicy?)`) is a typed wrapper that calls the underlying non-generic `ICache` with the strategy applied and the resolved policy snapshotted at construction. `HashCache<T>` is the same idea for hash caches. If you don't need named policies, omit the third argument — the cache falls back to its factory's default policy on every call. A convenience overload `Cache<T>(ICacheFactory, ICacheKeyStrategy?, ICachePolicyFactory?, string? policyName)` exists for callers who want the typed wrapper to do the `Resolve` itself, but it always calls `cacheFactory.CreateCache()` (the default provider) — when you need to select `InMemoryRedis` explicitly, use the base ctor as shown above.

`KnownCacheProviderNames.InMemoryRedis` is the de-facto default in production — most services pick the multilayer cache. If you need provider-fallback (`InMemoryRedis` for prod, `InMemory` for local-dev), wrap the factory call — see [recipes/provider-fallback.md](provider-fallback.md).

If you want a strategy that bakes in the assembly version (so deploys auto-invalidate), see [recipes/app-version-prefix.md](app-version-prefix.md). The shape is the same — only the strategy changes.

## When not to use

- Dynamic-key scenarios where the prefix or value type changes per call (e.g. a generic per-request cache behavior). Use the non-generic `ICache` directly — see [recipes/mediatr-pipeline-behavior.md](mediatr-pipeline-behavior.md).
- Caches that only ever read or write one key (an extension method is over-engineering for a one-call cache).

## See also

- [quickstart.md](../quickstart.md)
- [concepts.md](../concepts.md)
- [recipes/app-version-prefix.md](app-version-prefix.md)
- [recipes/provider-fallback.md](provider-fallback.md)
- [reference/interfaces.md](../reference/interfaces.md)
