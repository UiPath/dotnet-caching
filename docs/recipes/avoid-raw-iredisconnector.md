# Don't reach for `IRedisConnector.Database`

Three recurring shapes routinely show up in consumer code that goes around `ICache` straight to `IRedisConnector.Database`. Each has a supported library primitive that does the same thing better.

## Anti-pattern 1: hand-rolled `CacheStrategy<T>`

**Before** (manual JSON, manual TTL, manual sentinels):

```csharp
public class CacheStrategy<T>(IRedisConnector connector) : ICacheStrategy<T>
{
    private const int RetentionPeriod = 300;
    private const string NoValue = "NoValue";

    public async Task CacheValueAsync(string key, T? content)
    {
        var value = content is null ? NoValue : JsonSerializer.Serialize(content);
        await connector.Database.StringSetAsync(key, value, TimeSpan.FromSeconds(RetentionPeriod));
    }
}
```

**After** (typed cache, automatic TTL, lib-side null sentinel via `CacheNullValues`):

```csharp
public class CachedThings(ICacheFactory factory)
{
    private readonly ICache<MyDto> _cache = factory.CreateCache<MyDto>(
        KnownCacheProviderNames.InMemoryRedis);

    public ValueTask<bool> SetAsync(string key, MyDto? value, CancellationToken token) =>
        _cache.SetAsync(key, value, TimeSpan.FromMinutes(5), token);
}
```

Set `CacheNullValues: true` on the provider in `appsettings.json` if you want null persistence (otherwise the cache treats `null` as "miss / drop entry").

## Anti-pattern 2: `StringIncrement`-based distributed latch

**Before** (manual once-per-key flag, manual TTL):

```csharp
var count = await connector.Database.StringIncrementAsync(latchKey);
if (count == 1) await connector.Database.KeyExpireAsync(latchKey, TimeSpan.FromMinutes(5));
if (count > 1) return;  // someone else got there first
```

**After** (`IDistributedLock` with built-in TTL and cooldown):

```csharp
await using var handle = await distributedLock.TryAcquireAsync(
    latchKey,
    expiry: TimeSpan.FromMinutes(5),
    token);
if (handle is null) return;  // contended — someone else holds the lock
// Do the work; lock is released on DisposeAsync.
```

`AddInMemoryRedis()` registers `RedisDistributedLock` automatically, so the typical opt-in is just `DistributedLockEnabled: true` in appsettings (or in a `CachePolicy.Lock` block). `AddMemory()`-only deployments and any custom wiring that skips `AddInMemoryRedis()` need an explicit `.AddRedisDistributedLock()` on the builder — see [how-to/resilience.md](../how-to/resilience.md).

## Anti-pattern 3: counter-based throttle

**Before** (manual increment/decrement, manual race correction):

```csharp
var current = await connector.Database.StringIncrementAsync(throttleKey);
if (current > MaxConcurrent)
{
    await connector.Database.StringDecrementAsync(throttleKey);
    throw new ThrottleException();
}
try { /* do work */ }
finally { await connector.Database.StringDecrementAsync(throttleKey); }
```

**After:** there are two correct shapes depending on what you actually need.

- **Single-flight per key** (one generator runs at a time per cache key): use `CachePolicy.LockProfile` — see [how-to/resilience.md](../how-to/resilience.md). This is what most "throttle" patterns actually want.
- **True rate-limiting** (N ops per second): use a dedicated rate-limiter (`System.Threading.RateLimiting`) — the cache library is not a rate-limiter and won't grow into one.

## Why this matters

Hand-rolled patterns on `IRedisConnector.Database` skip every safety net the lib provides:

- **No telemetry** — `cache.miss`, `cache.write`, `cache.distributedlock.unavailable` events don't fire for hand-rolled code. You lose visibility into how the cache is actually used.
- **No resilience** — the Polly pipeline (`AddResilienceStrategies`) wraps every `ICache` call with retry and circuit-breaker. Hand-rolled `StringSetAsync` calls bypass it.
- **No L1** — `InMemoryRedis` consumers get free in-process caching on top of Redis. Hand-rolled `StringGet` hits Redis on every call.
- **No test coverage** — the lib has years of coverage on edge cases (cluster reconfigurations, planned maintenance, hung connections). Hand-rolled code reinvents them.

The library exists so you don't have to think about these. If you're reaching for `IRedisConnector.Database`, the right move is almost always "find the lib primitive that does this and use it."

## See also

- [how-to/resilience.md](../how-to/resilience.md)
- [reference/interfaces.md](../reference/interfaces.md)
- [recipes/factory-extension-methods.md](factory-extension-methods.md)
