# MediatR caching pipeline behavior

**What:** A generic `IPipelineBehavior<TRequest, TResponse>` that caches request handlers marked with an `IHybridCacheable` interface. One implementation; every cacheable request gets it for free.

**When to use:**
- Codebases already on MediatR where requests are pure functions of their input.
- When caching policy (key, TTL) is a property of the request, not the cache infrastructure.
- When you want a single audit point for caching behavior across many handlers.

## Code

```csharp
using MediatR;
using UiPath.Platform.Caching;

public interface IHybridCacheable
{
    string GetKey();
    TimeSpan GetExpirationTime();
}

public class CachingBehavior<TRequest, TResponse>(ICacheFactory cacheFactory)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IHybridCacheable
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken token)
    {
        var cache = cacheFactory.CreateCache(KnownCacheProviderNames.InMemoryRedis);
        return await cache.GetOrAddAsync<TResponse>(
            new CacheKey(request.GetKey()),
            _ => next(),
            request.GetExpirationTime(),
            policy: null,
            token);
    }
}
```

Register the behavior in DI:

```csharp
services.AddTransient(typeof(IPipelineBehavior<,>), typeof(CachingBehavior<,>));
```

A cacheable request:

```csharp
public record GetOrderQuery(int OrderId) : IRequest<Order>, IHybridCacheable
{
    public string GetKey() => $"order:{OrderId}";
    public TimeSpan GetExpirationTime() => TimeSpan.FromMinutes(5);
}
```

## Notes

The behavior uses the non-generic `ICache` because the value type varies per request. Inject `ICacheFactory` and call `CreateCache(KnownCacheProviderNames.InMemoryRedis)` per request — the factory caches provider instances internally, so this isn't allocating a new cache per call.

`new CacheKey(request.GetKey())` wraps the request's key string. `CacheKey` implicit-converts from `string`, but making the construction explicit is clearer at a glance.

The behavior only fires for `TRequest : IHybridCacheable`. MediatR resolves the open generic `IPipelineBehavior<,>` for each request, picks the closed type, and the generic constraint filters out non-cacheable requests at compile time. Non-cacheable requests pay nothing (the behavior isn't instantiated for them).

Key collisions across handlers: since this behavior uses the request's `GetKey()` directly, two different requests that produce the same key string will share a cache entry. Include the request type name in `GetKey()` if collisions are a real risk: `$"{nameof(GetOrderQuery)}:{OrderId}"`.

## When not to use

- Requests with side effects — caching a command means the side effect runs only on cache miss. Use a `ICommand`-vs-`IQuery` split and only cache queries.
- Handlers whose response shape changes per call (rare; usually a sign of mixing two responsibilities).
- Codebases not already on MediatR — adding MediatR for caching alone is overkill.

## See also

- [recipes/factory-extension-methods.md](factory-extension-methods.md)
- [reference/interfaces.md](../reference/interfaces.md)
