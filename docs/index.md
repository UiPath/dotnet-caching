# Caching docs

UiPath's internal multilayer caching library. L1 in-memory + L2 Redis, cross-node sync over Redis Streams or Pub/Sub, single-flight stampede protection, hydrating cache, jitter, and resilience policies — all behind a small, opinionated DI surface.

## Packages

| Package | When you need it |
|---|---|
| `UiPath.Platform.Caching.Abstractions` | Always (transitive). |
| `UiPath.Platform.Caching.Runtime` | Always — Redis, InMemory, multilayer providers, Streams + Pub/Sub topics. |
| `UiPath.Platform.Caching.Polly` | Resilience pipelines around cache ops. Recommended. |
| `UiPath.Platform.Caching.CloudEvents` | CNCF CloudEvents wrapper around broadcast events. Recommended. |
| `UiPath.Platform.Caching.Telemetry` | AppInsights wiring for `ICachingTelemetryProvider`. Skip if you use OpenTelemetry. |
| `UiPath.Platform.Caching.AspNetCore` | Dynamic filter + Redis profiler middleware for AppInsights. |

## Pick your path

**I'm new.** → [quickstart.md](quickstart.md). Five minutes from `dotnet add package` to `ICache<MyDto>` injected.

**I'm building the mental model.** → [concepts.md](concepts.md). Architecture overview: providers, layers, topics, locks, policies, telemetry, and the two surfaces (`ICache<T>` vs `ICache`).

**I need to solve X.**

- Single-flight, hydrating cache, jitter, named policies, Polly → [how-to/resilience.md](how-to/resilience.md).
- Cross-node sync, per-topic options, streams notify doorbell, sharded Pub/Sub → [how-to/broadcast.md](how-to/broadcast.md).
- Per-field caching, side-channel metadata, bundled GET+TTL → [how-to/hash-cache.md](how-to/hash-cache.md).
- AppInsights vs OpenTelemetry, custom key/channel strategies → [how-to/telemetry-and-strategies.md](how-to/telemetry-and-strategies.md).
- Adding a new cache provider, topic provider, or serializer → [how-to/extending.md](how-to/extending.md).

**I need a short pattern I can paste.** → [recipes/](recipes/):

- [configure-caching-extension.md](recipes/configure-caching-extension.md) — the universal DI wiring shape.
- [provider-fallback.md](recipes/provider-fallback.md) — InMemoryRedis when Redis is on, InMemory otherwise.
- [factory-extension-methods.md](recipes/factory-extension-methods.md) — typed `ICache<T>` via `ICacheFactory` extension methods.
- [app-version-prefix.md](recipes/app-version-prefix.md) — auto-invalidate on deploy via assembly-version key prefix.
- [mediatr-pipeline-behavior.md](recipes/mediatr-pipeline-behavior.md) — generic per-request cache as a MediatR behavior.
- [hash-cache-with-metadata.md](recipes/hash-cache-with-metadata.md) — payload + freshness metadata side-by-side.
- [custom-telemetry-provider.md](recipes/custom-telemetry-provider.md) — bridge `ICachingTelemetryProvider` to a host telemetry surface.
- [opentelemetry-multiplexer-factory.md](recipes/opentelemetry-multiplexer-factory.md) — register OTel Redis instrumentation.
- [redis-health-check.md](recipes/redis-health-check.md) — wire `RedisHealthCheck` into the ASP.NET health probe.
- [avoid-raw-iredisconnector.md](recipes/avoid-raw-iredisconnector.md) — three anti-patterns and their supported alternatives.

**I need the full settings reference.** → [reference/settings.md](reference/settings.md) (one table per options class), [reference/interfaces.md](reference/interfaces.md) (public API surface), [reference/glossary.md](reference/glossary.md) (alphabetical term lookup).

**I want to run the sample.** → [sample-app.md](sample-app.md).

## What's new

See [CHANGELOG.md](../CHANGELOG.md). Recent headliners:

- **Hydrating cache** — proactive background refresh via `CachePolicy.RehydrateEnabled` + `RehydrateOptions`.
- **Single-flight** — `ILocalLock` (default on) + opt-in `IDistributedLock`.
- **L2 jitter** — `CachePolicy.JitterMaxDuration` to spread cluster-wide expirations after bulk writes.
- **Streams notify doorbell** — `NotifyEnabled` adds a Pub/Sub channel that wakes consumers immediately, dropping publish-to-deliver latency from `PollInterval` to network RTT.
- **Per-topic broadcast options** — `Topics[]` overlay under `Broadcast:RedisStreams` / `Broadcast:RedisPubSub`.
- **Aspire sample** — `Sample.AspNetCore.AppHost` replaces the older `docker-compose` setup.
