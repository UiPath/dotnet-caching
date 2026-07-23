# Resilience

Every reliability knob the library ships. **Local single-flight is on by default** for the
`InMemoryRedis` and `InMemory` providers (`LocalLockEnabled = true`, and `AddInMemoryRedis()` /
`AddMemory()` register `ILocalLock` automatically). Everything else on this page —
distributed lock, hydrating cache, jitter, named policies, Polly resilience — is opt-in
via `CachePolicy` or explicit builder extensions. If you're hitting Redis under load,
single-flight and hydrating-cache will move latency more than any other change.

## Stampede protection

Two layers:

- **Local lock** (`ILocalLock`, default impl `AsyncKeyedLocalLock`) — in-process
  single-flight per cache key. **Default on** for `InMemoryRedis` and `InMemory` providers;
  disable via `LocalLockEnabled: false`.
- **Distributed lock** (`IDistributedLock`, default impl `RedisDistributedLock`) —
  cross-node single-flight via Redis `SET NX`. **Default off**; `AddInMemoryRedis()` registers
  the implementation automatically, so the only opt-in is flipping `DistributedLockEnabled: true`
  on the provider options (or in a `CachePolicy.Lock` block). `AddMemory()` does **not**
  register `RedisDistributedLock` (the in-memory provider passes `NullDistributedLock` to
  `MultilayerCache`), so memory-only deployments that need cross-node single-flight must add
  `.AddRedisDistributedLock()` explicitly on the builder. `CachingBuilder.Complete()` falls back
  to `NullDistributedLock` when no real implementation was registered.

When both are enabled, `MultilayerCache.GetOrAddAsync` takes the local lock first (cheap),
then the distributed lock under double-checked reads. A failure to acquire the distributed
lock degrades to thundering-herd (per-node single-flight only) rather than stalling.

**Minimum enablement (appsettings):**

```jsonc
"Caching": {
  "InMemoryRedis": {
    "DistributedLockEnabled": true   // RedisDistributedLock is already registered by AddInMemoryRedis()
  }
}
```

**Full configuration (appsettings):**

```jsonc
"Caching": {
  "InMemoryRedis": {
    "LocalLockEnabled": true,                  // default true
    "LocalLockTimeout": "0:00:00.500",
    "DistributedLockEnabled": true,            // opt in
    "DistributedLockTimeout": "0:00:00.500",   // wait at most 500ms for the Redis lock
    "DistributedLockExpiry": "0:00:05"         // Redis lock TTL — size to at least the P99 generator runtime so the lock never expires mid-factory. RehydrationCoordinator overlays this with factoryTimeout + cooldown for rehydrate-only locks.
  },
  "DistributedLockPollInterval": "0:00:00.050",     // initial retry interval
  "DistributedLockMaxPollInterval": "0:00:00.500"   // max backoff
}
```

### Local lock pool

`LocalLockPoolSize` (default 100) is a semaphore-pool allocation hint, not a hard
concurrency cap. When the pool is exhausted under high cold-miss concurrency, the underlying
`AsyncKeyedLocker` allocates fresh semaphores on demand — increasing `LocalLockPoolSize`
only reduces GC pressure from semaphore churn, it does not throttle callers or prevent new
locks from being granted.

`LocalLockPoolInitialFill` (default 10) controls how many semaphores are pre-allocated at
startup, so the first wave of requests does not pay allocation costs. A sensible value is
your expected P99 concurrent cold-miss concurrency at startup. Leave `LocalLockPoolSize` at
the default unless profiling shows GC pressure from semaphore allocation in heap snapshots
or allocation traces.

### Distributed lock retry semantics

When a distributed lock is contended, `RedisDistributedLock` retries with exponential
backoff starting at `DistributedLockPollInterval` (default 50ms). After each failed acquire
the interval doubles: 50ms → 100ms → 200ms → 400ms → 500ms (capped at
`DistributedLockMaxPollInterval`, default 500ms).

Each sleep is jittered by multiplying the interval by a random factor in [0.8, 1.2], i.e.
±20%, so concurrent waiters on different nodes desynchronize naturally instead of retrying
in lockstep. For example a 100ms interval becomes anywhere from 80ms to 120ms.

A remaining-wait clamp on each sleep prevents any individual delay from exceeding the
overall `DistributedLockTimeout` budget: once the remaining wait drops below the next
computed interval, the clamp substitutes the remaining budget directly, which limits the
retry loop to roughly one more attempt before timing out. This means setting
`DistributedLockPollInterval` larger than `DistributedLockTimeout` effectively reduces the
entire acquire loop to a single attempt.

## Hydrating cache

Refresh an entry in the background before it physically expires, so foreground callers never
block on a miss.

Enable via `CachePolicy.RehydrateEnabled: true` plus `Rehydrate: { ... }`:

```jsonc
"Policies": {
  "MyApp.Models.Order": {
    "DistributedExpiration": "0:10:00",   // 10-minute hard TTL
    "RehydrateEnabled": true,
    "Rehydrate": {
      "Threshold": 0.75,                  // refresh after 7.5 minutes
      "BaseCooldown": "0:00:05",          // wait 5s between failed refreshes
      "MaxCooldown": "0:05:00",
      "TimeoutFraction": 0.5,             // background factory gets 5 minutes
      "Name": "orders"                    // telemetry profile dimension
    }
  }
}
```

**Worked example — caller:**

```csharp
public class OrderService(ICache<Order> cache, IOrderRepository repo)
{
    // The named policy resolves by typeof(T).FullName = "MyApp.Models.Order".
    // RehydrateEnabled + Rehydrate are pulled from appsettings; the caller doesn't see them.
    public ValueTask<Order?> GetAsync(int orderId, CancellationToken token) =>
        cache.GetOrAddAsync(
            orderId,
            ct => repo.LoadAsync(orderId, ct),
            token: token);
}
```

**Lock-take fallback:** when the distributed lock cannot be acquired (contended — another
node is already rehydrating this key — or its cooldown is still active, or the lock backend
is unavailable), the foreground caller serves the still-live entry from the cache and
rehydration is skipped this cycle. The next call past the soft-TTL threshold will retry.
Backend-failure visibility is preserved separately via the `cache.distributedlock.unavailable`
telemetry event.

**Cooldown semantics:** `Rehydrate.BaseCooldown` doubles after each failure up to
`MaxCooldown`. The `RehydrationCoordinator` enforces the cooldown via the distributed lock's
TTL — `factoryTimeout + cooldown` — so a failed rehydrate on node A does not immediately
retry from node B. When no `IDistributedLock` is registered (the default), `NullDistributedLock`
is used and cooldown enforcement does not apply — rehydrate can fire per-node on every hit
past the threshold (subject only to per-node `_inFlight` single-flight). Single-node setups
are fine with the null lock; multi-node deployments should wire `RedisDistributedLock`.

### Threshold math

`Threshold` is a fraction in (0, 1] of the entry's full `DistributedExpiration` lifetime.
The `RehydrationCoordinator` computes the elapsed fraction as
`(duration - remaining) / duration`: once that fraction reaches or exceeds `Threshold`,
rehydration is eligible to fire.

With `DistributedExpiration: 10m` and `Threshold: 0.75`, the soft-TTL trigger fires once
7.5 minutes have elapsed and 2.5 minutes remain. The background factory runs under a timeout
equal to `TimeoutFraction * duration`, floored at 1 second — for the example above,
`0.5 * 600s = 300s`, so the background factory gets 5 minutes. The 1-second floor means
very short-TTL entries (under 2s) still get a full second for their background factory,
preventing an instantaneous timeout on entries whose `TimeoutFraction * duration` rounds to
zero.

### The Name field

The `Name` field on `RehydrateOptions` surfaces on telemetry as the `profile` dimension
across all rehydrate events:

- `cache.rehydrate.triggered` — rehydration has started for a key.
- `cache.rehydrate.succeeded` — factory completed and the new value was stored.
- `cache.rehydrate.failed` — factory threw an exception.
- `cache.rehydrate.timed_out` — factory exceeded `TimeoutFraction * duration`.
- `cache.rehydrate.deduped` — a concurrent rehydration was already in flight; skipped.

If multiple typed caches share the same `RehydrateOptions` block — for example via
`DefaultCachePolicy` — giving them a common `Name` lets you group their rehydrate activity
in dashboards and alerts as a single operational profile. Leave `Name` null for caches where
per-type telemetry granularity is more useful than cross-type grouping.

## Jitter

`CachePolicy.JitterMaxDuration` adds a uniform random `[0, max)` delay to the L2 expiration
at write time. Spreads cluster-wide expirations after bulk writes (deploy warm-up, batch
import).

**Enablement (one line per policy):**

```jsonc
"Policies": {
  "MyApp.Models.Catalog": {
    "DistributedExpiration": "0:10:00",
    "JitterMaxDuration": "0:00:30"   // up to 30s added to each write's L2 TTL
  }
}
```

**Bulk-write semantics:** `SetAsync(KeyValuePair[])` does one jitter draw per call — the
jittered TTL is shared across the batch. This still spreads expirations across nodes
(different nodes pick different draws), but does not vary within one batch. If you need
per-key variance within a batch, call `SetAsync(key, value, expiration)` one key at a time.

**L1 vs L2:** jitter applies to L2 only in the typical case where
`LocalMaxExpiration ≤ DistributedExpiration`. If the L1 cap is configured longer than L2,
the L2 entry's jittered absolute expiration flows through to the in-memory entry's absolute
expiration — per-node only, no cluster-sync concern. A `JitterMaxDuration` larger than
`DistributedExpiration` is accepted but produces wildly skewed TTLs — pick a value smaller
than the typical lifetime.

**Caller-supplied expirations bypass jitter.** Passing an explicit `expiration:` argument to
`SetAsync` / `GetOrAddAsync` / `RefreshAsync` uses that value exactly; jitter only applies
when the resolved expiration comes from `CachePolicy.DistributedExpiration` or the provider
default.

## Named policies

`CacheOptions.Policies` is a `Dictionary<string, CachePolicy>` keyed by name. Each
`ICache<T>` / `IHashCache<T>` binds a policy at construction; the default name is
`typeof(T).FullName`.

**Resolution chain:** a named policy in `Policies["MyApp.Models.Order"]` is consulted first;
fields it leaves `null` inherit from `DefaultCachePolicy`; fields still `null` inherit from
the cache instance's *effective default* — provider-specific options
(`IMultilayerCacheOptions.LocalMaxExpiration`, `DefaultExpiration`, the lock fields) merged
field-by-field with `CacheOptions.DefaultCachePolicy`. Provider settings win per field; the
user's `DefaultCachePolicy` fills gaps. For lock fields MultilayerCache also applies
hardcoded fallbacks (`500ms` lock timeouts, `5s` distributed-lock expiry) so every cache has
a fully resolved lock at startup.

```jsonc
"Policies": {
  "MyApp.Models.Order": {
    "DistributedExpiration": "0:10:00",
    "RehydrateEnabled": true
    // LocalExpiration omitted → inherits from DefaultCachePolicy,
    // then from InMemoryRedis.LocalMaxExpiration
  }
},
"DefaultCachePolicy": {
  "LocalExpiration": "0:01:00",
  "FactoryTimeout": "0:00:02"
}
```

**Per-call override:** the `expiration:` argument on `GetOrAddAsync` / `SetAsync` /
`RefreshAsync` always wins over `DistributedExpiration`. Use this when one call site needs a
different lifetime than the cache's policy default — e.g. a long-lived precompute vs. a
short-lived live read on the same cache.

```csharp
// Uses policy DistributedExpiration (10 min) — the typical case.
await cache.SetAsync(key, value, token);

// Overrides the policy for this one write.
await cache.SetAsync(key, value, expiration: TimeSpan.FromHours(1), token);
```

**Field-level vs whole-object merge:** `Lock` merges field-by-field against
`DefaultCachePolicy.Lock` (a named policy can override `LocalLockEnabled` while inheriting
the rest). `Rehydrate` is whole-object — a named policy that sets `Rehydrate` replaces the
default's `Rehydrate` entirely. To override a single rehydrate field, redeclare the whole
`RehydrateOptions` block.

### Practical example: per-entity TTL with shared lock settings

```jsonc
"Caching": {
  "DefaultCachePolicy": {
    "LocalExpiration": "0:01:00",
    "FactoryTimeout": "0:00:02",
    "Lock": {
      "DistributedLockEnabled": true,
      "DistributedLockTimeout": "0:00:00.500"
    }
  },
  "Policies": {
    "MyApp.Models.Order": {
      "DistributedExpiration": "0:10:00",
      "RehydrateEnabled": true,
      "Rehydrate": {
        "Threshold": 0.75,
        "BaseCooldown": "0:00:05",
        "MaxCooldown": "0:05:00",
        "TimeoutFraction": 0.5,
        "Name": "orders"
      }
      // Lock omitted → inherits DistributedLockEnabled: true from DefaultCachePolicy.Lock
    },
    "MyApp.Models.Catalog": {
      "DistributedExpiration": "0:30:00",
      "JitterMaxDuration": "0:01:00"
      // Lock omitted → same inheritance
    }
  }
}
```

In this example both `Order` and `Catalog` inherit the same distributed-lock settings from
`DefaultCachePolicy.Lock`. `Order` adds rehydration; `Catalog` adds jitter. Neither
redeclares `LocalExpiration` or `FactoryTimeout`, so those also inherit.

## Polly resilience

`.AddResilienceStrategies()` adds Polly pipelines around every cache operation. Configure
via `ResiliencePoliciesOptions`:

| Property | Default | Notes |
|---|---|---|
| `Enabled` | `true` | Master switch for the Polly pipeline. |
| `DurationOfBreak` | `1m` | Circuit-breaker open duration. |
| `ExceptionsAllowedBeforeBreaking` | `500` | Failures inside `DurationOfBreak` before tripping. |
| `RequestTimeout` | `1s` | Per-op timeout (wraps the SE.Redis call). |
| `RetryCount` | `1` | Retries before failing the op. |
| `TelemetryEnabled` | `true` | Emit Polly events on `ICachingTelemetryProvider`. |
| `RethrowCircuitBreakerExceptions` | `false` | When `true`, callers see `BrokenCircuitException` instead of getting `null`. |

See [reference/settings.md](../reference/settings.md) for the full reference.

### Retries and non-idempotent operations

`RetryCount` defaults to `1` and the retry strategy handles **every** exception, including the
per-attempt `RequestTimeout` (`TimeoutRejectedException`). For idempotent commands this is safe —
replaying `SADD`/`SREM`/`SET`/`DEL`/`EXPIRE` converges to the same end state (only the returned
count may differ). It is **not** safe for destructive-read commands like `SPOP` (and the planned
`LPOP`/`RPOP`): if the server executed the pop but the response was lost to a timeout or a
connection drop, a retry pops a *different* set of members and the first batch is gone forever —
silent data loss.

Such operations therefore do **not** go through the retrying `Write` pipeline. The set cache
resolves a configurable pipeline via `IResiliencePipelineProvider.Get(RedisSetCacheOptions.ResilienceKeyName)`.
Register your own pipeline for that name and point the set cache at it:

```csharp
builder
    .AddResilienceStrategies()
    .AddResiliencePipeline("set-pop", o => o.RetryCount = 0)   // timeout + breaker + fallback, no retry
    .AddQueueRedis(o => o.ResilienceKeyName = "set-pop");
```

If `ResilienceKeyName` is left null/empty (the default), or names a pipeline that was never
registered, `Get` returns the no-op `EmptyResiliencePipeline` — `SPOP` runs raw, with no retry,
circuit-breaker or timeout. This is the safe default for non-idempotent commands: on a transient
failure the op fails (returning the empty/default result) rather than risk popping members that
are never returned to any caller, so `PopAsync` is **at-most-once**. The idempotent read/write
operations continue to use the predefined `Read` / `Write` pipelines.

### Registering your own pipelines

`AddResiliencePipeline(name, configure)` registers a named pipeline built from its own
`ResiliencePoliciesOptions`. The `name` is the scope you pass to `IResiliencePipelineProvider.Get(name)`.
`AddResilienceStrategies` predefines `ResiliencePipelineNames.Read` and `ResiliencePipelineNames.Write`
from the base configuration; you can add new names or retune the built-ins (named options compose, so
the last `configure` for a name wins per field):

```csharp
builder
    .AddResilienceStrategies(o => o.RetryCount = 1)            // base config for read + write
    .AddResiliencePipeline(ResiliencePipelineNames.Write, o => o.RetryCount = 3)  // retune write
    .AddResiliencePipeline("bulk-import", o =>                 // a brand-new pipeline
    {
        o.RequestTimeout = TimeSpan.FromSeconds(10);
        o.RetryCount = 0;
    });
```

Only registered names resolve to a real pipeline; `Get` on any other name is a no-op. To take full
control of the Polly strategy chain (beyond what `ResiliencePoliciesOptions` exposes), subclass
`ResiliencePipelineFactory`, override `Create`/`GetBuilder`, and register your factory after
`AddResilienceStrategies`.

### Why exception count, not error rate

The circuit-breaker counts absolute failures inside a sliding `DurationOfBreak` window
rather than tracking an error rate percentage. This is intentional for caching workloads: at
high RPS a small error percentage still translates to a very large absolute count, and
percentage-based breakers become hard to reason about when baseline traffic fluctuates.

Absolute counting is predictable — 500 failures in 60 seconds means the breaker trips
regardless of how many successes happened alongside them. Tune `ExceptionsAllowedBeforeBreaking`
based on your traffic:

- A service doing 10,000 cache ops/sec hits the default threshold of 500 in 3 seconds at a
  0.5% failure rate — likely too sensitive for a healthy Redis cluster experiencing brief
  timeouts.
- A value of 2,000–5,000 is more appropriate for high-throughput services where you want the
  breaker to trip only on a sustained fault, not a momentary spike.
- A value of 50–100 is appropriate for lower-volume services where even a handful of
  failures signals a genuine Redis problem worth backing off from.

### What happens when the circuit is open

When the circuit is open and `RethrowCircuitBreakerExceptions` is `false` (the default),
every cache operation returns `null` — or the equivalent empty result for the operation type
— as if it were a cache miss. The caller's `GetOrAddAsync` factory runs against the source of
truth, bypassing both L1 and L2 for the duration of the open period.

This means your service keeps functioning at the cost of elevated source-of-truth load; the
cache simply stops absorbing traffic until the breaker resets after `DurationOfBreak`. Once
the breaker enters half-open and a probe succeeds, normal caching resumes.

Set `RethrowCircuitBreakerExceptions: true` when you want callers to observe
`BrokenCircuitException` explicitly — for example to degrade gracefully to a simplified
response, surface a 503, or short-circuit a batch operation that would be pointless without
cache support.

### Enablement example

```jsonc
"Caching": {
  "ResiliencePolicies": {
    "Enabled": true,
    "DurationOfBreak": "0:01:00",
    "ExceptionsAllowedBeforeBreaking": 2000,
    "RequestTimeout": "0:00:01",
    "RetryCount": 1,
    "TelemetryEnabled": true,
    "RethrowCircuitBreakerExceptions": false
  }
}
```

### Don't roll your own

If you're reaching for `IRedisConnector.Database` directly — manual `StringSet` + JSON,
manual `StringIncrement`-based latch, manual counter-based throttle — see
[recipes/avoid-raw-iredisconnector.md](../recipes/avoid-raw-iredisconnector.md). The
supported alternatives are `ICache<T>`, `IDistributedLock`, and `CachePolicy`.

## Feature decision guide

Use this table to pick the right knob for a given problem.

| Symptom | Recommended feature | Key setting |
|---|---|---|
| Cache miss storm after deploy or after a key expires under load | Stampede protection — local lock | `LocalLockEnabled: true` (default) |
| Cross-node miss storms when multiple pods hit the same cold key simultaneously | Stampede protection — distributed lock | `DistributedLockEnabled: true` (the lock impl is already registered by `AddInMemoryRedis()`) |
| Foreground callers occasionally block while waiting for a slow factory | Hydrating cache | `RehydrateEnabled: true` + `Rehydrate.Threshold` |
| All keys written in a batch expire at the same time, causing periodic spikes | Jitter | `JitterMaxDuration` |
| Redis goes slow or unavailable and callers queue up waiting | Polly circuit-breaker | `AddResilienceStrategies()` + `ExceptionsAllowedBeforeBreaking` |
| Some caches need different TTLs or rehydrate settings than others | Named policies | `Policies["MyApp.Models.Order"]` |
| Need per-call TTL overrides without a separate cache instance | Per-call expiration | `expiration:` argument on `GetOrAddAsync` / `SetAsync` |

## Common pitfalls

**Rehydrate fires on every node with `NullDistributedLock`.**
Without a real distributed lock, `NullDistributedLock.TryAcquireAsync` always returns a
non-null handle — every node treats itself as having acquired the lock. Per-node
`_inFlight` deduplication prevents concurrent rehydrations on the same node, but across N
nodes you get N simultaneous background factory calls after the threshold is crossed. For
single-node or dev setups this is acceptable. For multi-node production, register
`RedisDistributedLock`.

**`DistributedLockExpiry` set too short.**
If `DistributedLockExpiry` is shorter than the actual factory execution time, the Redis lock
expires before the factory completes. A second node acquires the lock and runs the factory
concurrently, defeating the point of the distributed lock. Set `DistributedLockExpiry` to
your factory's P99 latency plus a generous cooldown buffer. The `RehydrationCoordinator`
computes lock expiry as `factoryTimeout + cooldown`, so for rehydration the lock expiry is
managed automatically; it's the foreground `GetOrAddAsync` lock that needs manual tuning via
`InMemoryRedis.DistributedLockExpiry`.

**Jitter larger than `DistributedExpiration`.**
A `JitterMaxDuration` of, say, `1:00:00` on a policy whose `DistributedExpiration` is
`0:10:00` can write entries with TTLs anywhere from 10 to 70 minutes. If the service expects
a maximum staleness bound, the effective bound is now `DistributedExpiration + JitterMaxDuration`,
not `DistributedExpiration` alone. Keep `JitterMaxDuration` to 10–30% of `DistributedExpiration`.

**Circuit-breaker threshold too low at high RPS.**
With `ExceptionsAllowedBeforeBreaking: 500` and a service doing 50,000 ops/sec, a 1%
transient blip (500 errors/sec) trips the breaker in under one second. While the breaker is
open the service runs without the cache, hammering the source of truth. Tune the threshold
to match your expected failure characteristics — start with `DurationOfBreak / (acceptable
downtime fraction * RPS)` as a rough calculation.

**`RehydrateEnabled: true` without a `Rehydrate` block.**
`CachePolicy.RehydrateEnabled` is a separate master switch from `CachePolicy.Rehydrate`.
Setting only `RehydrateEnabled: true` without a `Rehydrate` block leaves `Rehydrate` null;
`RehydrationCoordinator.TryTrigger` checks both and returns false immediately. Both fields
must be set.

## See also

- [reference/settings.md](../reference/settings.md) — full property reference for all options classes.
- [reference/interfaces.md](../reference/interfaces.md) — `IDistributedLock`, `ILocalLock`, `ICachePolicyFactory`.
- [concepts.md](../concepts.md) — cache tiers, lifetime model, and the broadcast layer.
