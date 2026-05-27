# Provider fallback (InMemoryRedis vs InMemory)

**What:** Pick `InMemoryRedis` when Redis is enabled in config, fall back to `InMemory` otherwise — so the same code runs in local-dev without Redis.

**When to use:**
- Local-dev scenarios where running Redis adds friction.
- CLI tools or one-shot jobs that want caching only if Redis is already available.
- Test harnesses that exercise the cache without a Redis dependency.

## Code

```csharp
using UiPath.Platform.Caching;

public static class CacheFactoryExtensions
{
    public static ICache<T> CreateMultilayerCache<T>(
        this ICacheFactory factory,
        bool redisEnabled) =>
        factory.CreateCache<T>(redisEnabled
            ? KnownCacheProviderNames.InMemoryRedis
            : KnownCacheProviderNames.InMemory);
}
```

Then at the call site:

```csharp
using Microsoft.Extensions.Options;
using UiPath.Platform.Caching;
using UiPath.Platform.Caching.Redis;

public class UserCache
{
    private readonly ICache<User> _cache;

    public UserCache(ICacheFactory factory, IOptions<RedisCacheOptions> redis)
    {
        // RedisCacheOptions binds from "Caching:Redis"; Enabled defaults to true and
        // can be flipped to false in appsettings.Development.json to force the InMemory
        // fallback without code changes.
        _cache = factory.CreateMultilayerCache<User>(redis.Value.Enabled);
    }
}
```

## Notes

`KnownCacheProviderNames` carries the canonical strings: `"InMemoryRedis"`, `"Redis"`, `"InMemory"`. Don't hand-type them.

The `redisEnabled` gate shown here is whatever boolean tells you Redis is reachable in this environment. Common patterns: a top-level `Redis:Enabled` flag, the presence of a non-empty `Redis:ConnectionString`, or an environment-variable check. The extension method keeps the branching logic in one place.

Cross-node sync is the obvious thing that changes: `InMemory` has no cross-node invalidation (unless you wire `BroadcastEnable: true` on it, which requires a connected broadcast channel anyway). That means a write on node A is invisible to node B's cache. For local-dev (single process) this is fine; for multi-host scenarios it's a correctness bug — see the "When not to use" section.

The `DefaultCache` setting in `appsettings.json` is the fallback for callers that don't specify a provider. Setting it to `InMemoryRedis` and having code paths force `InMemory` for local-dev is a clean split — production callers get the real cache, dev callers get the simplified one.

## When not to use

- Multi-host deployments — `InMemory` per node means stale reads. Use `InMemoryRedis` always; require Redis as a dependency.
- Caches that must survive process restart — `InMemory` doesn't persist.
- Caches where the L1 `SizeLimit` on `InMemoryRedis` matters differently than the `InMemory` limit — the two providers have separate size semantics.

## See also

- [concepts.md](../concepts.md)
- [reference/settings.md](../reference/settings.md)
