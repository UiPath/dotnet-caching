# Redis health check

**What:** Wire the shipped `RedisHealthCheck` so ASP.NET Core's health endpoint (typically `/healthz`) reports red when the Redis connection is unhealthy — and stays green during routine Azure Redis planned-maintenance windows that the library transparently handles.

**When to use:**
- Any service that exposes a Kubernetes / load-balancer health probe and uses Redis-backed caching.
- You want the probe to fail when the multiplexer can't reach Redis (so the orchestrator stops sending traffic) but **not** during planned-maintenance failovers (where the library has its own grace period).

## Code

```csharp
using Microsoft.Extensions.Diagnostics.HealthChecks;
using UiPath.Platform.Caching.Redis;

builder.Services.AddHealthChecks()
    .Add(new HealthCheckRegistration(
        name: "redis",
        factory: sp => new RedisHealthCheck(
            sp.GetRequiredService<IRedisConnector>(),
            sp.GetRequiredService<IRedisPlannedMaintenance>()),
        failureStatus: default,        // HealthStatus.Unhealthy
        tags: default,
        timeout: TimeSpan.FromSeconds(3)));

app.MapHealthChecks("/healthz");
```

## Notes

`RedisHealthCheck` resolves two dependencies registered by `AddRedisConnection()`:

- `IRedisConnector` — the connection multiplexer wrapper. The check pings Redis through it.
- `IRedisPlannedMaintenance` — the planned-maintenance state tracker. During a planned failover the check returns healthy even if the multiplexer is briefly reconnecting, so the orchestrator doesn't pull traffic during routine maintenance.

A 3-second timeout is the conventional value — longer than a typical ping (sub-100 ms) but short enough that a hung Redis doesn't keep the probe waiting indefinitely.

## When not to use

- Services that use `AddMemory()` only (no Redis) — `IRedisConnector` isn't registered. Use the standard `Microsoft.Extensions.Diagnostics.HealthChecks` ping or a no-op check instead.
- Services already publishing Redis health through a separate channel (e.g. a sidecar agent that probes Redis directly). Duplicating the signal adds noise without value.

## See also

- [quickstart.md](../quickstart.md)
- [sample-app.md](../sample-app.md) — the Aspire sample wires this exactly as shown above.
