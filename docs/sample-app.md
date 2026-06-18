# Running the sample app

The sample is an Aspire-orchestrated host (`Sample.AspNetCore.AppHost`) that boots one or two `Sample.AspNetCore` machine resources plus a Redis backend with a flag-controlled topology.

## Prerequisites

- .NET 10 SDK (matching `global.json`).
- Docker Desktop (Aspire spins up Redis containers).

## Run

From `Caching/`:

```bash
dotnet run --project Sample.AspNetCore.AppHost
```

Aspire opens its dashboard in the browser. Each `Sample.AspNetCore` machine resource exposes:

- Swagger: `https://localhost:<port>/swagger`
- Health: `http://localhost:<port>/healthz` — the sample wires `RedisHealthCheck` here; see [recipes/redis-health-check.md](recipes/redis-health-check.md).

RedisInsight (if enabled): `http://localhost:8001` (and `http://localhost:8002` when sharded Redis is enabled).

> The Aspire dashboard shows per-resource logs, traces, and environment variables. Use it to confirm the Redis connection string injected by the AppHost matches the running container.

## Configuration flags

All flags live under `SampleAspNetCore:*` in `Sample.AspNetCore.AppHost/appsettings.json` (or env vars `SampleAspNetCore__*`).

| Flag | Default | Effect |
|---|---|---|
| `UseRedisInsight` | `true` | Spin up RedisInsight (port 8001 in single-instance mode, 8001+8002 in sharded mode). |
| `UseSingleMachine` | `false` | Boot only `machine1` (skip `machine2`). Useful when you don't want to test cross-node sync. |
| `UseShardedRedis` | `false` | Boot a Redis Cluster (2 masters + 4 replicas, formed via `redis-trib`). Default is a single Redis instance. |
| `UseShardedPubSub` | `false` | When `UseShardedRedis: true`, enable `SPUBLISH`/`SSUBSCRIBE` for the streams notify doorbell. Requires Redis 7+. |
| `SingleRedisConnectionStringExtraParams` | `connectRetry=2,keepAlive=30,syncTimeout=2500,connectTimeout=2500` | Extra StackExchange.Redis params for single-instance mode. |
| `ShardedRedisConnectionStringExtraParams` | `allowAdmin=true,abortConnect=false,connectRetry=2,keepAlive=30,name=test,syncTimeout=2500,connectTimeout=2500` | Extra params for sharded mode. |

## What the sample demonstrates

The sample covers all three cache surfaces the library ships. `Controllers/CacheController.cs` exercises the raw `ICache` surface (dynamic-key, power-user API): Get, MGet, Set, MSet, Refresh, Remove, Contains, TimeToLive, ExpireTime. `Controllers/CacheController.cs` also hosts the typed `IntDefaultCacheController` and `BoolDefaultCacheController`, each derived from `CacheBaseController<TResource>`, which wraps `ICache` in a `Cache<T>` with a key prefix — that is the typical-consumer surface. The hash-cache surface follows the same pattern: `Controllers/HashCacheController.cs` provides the raw `IHashCache` controller and three typed variants (`ResourceDefaultHashCacheController`, `IntDefaultHashCacheController`, `BoolDefaultHashCacheController`) exercising `IHashCache<T>`. Finally, `Controllers/RedisConnectionController.cs` exposes connection-state endpoints (`GET /RedisConnection` for status, `POST /RedisConnection` to force a reconnect).

With `UseSingleMachine: false` and `UseShardedRedis: false` (the defaults), two machine resources (`sample-aspnetcore-machine1` and `sample-aspnetcore-machine2`) boot side-by-side against a shared Redis instance. The walkthrough that actually demonstrates cross-node L1 invalidation has three steps:

1. **Read `foo` on `machine2`** (`GET /Cache/Get?cacheKey=foo`). This populates `machine2`'s L1 with the current value (or `null` if the key doesn't exist yet).
2. **Write `foo = "baz"` on `machine1`** (`POST /Cache/Set?cacheKey=foo`, body `"baz"`). `machine1` updates its own L1 and writes through to L2; an invalidation event is published on the Redis Stream backplane that `machine2` is subscribed to.
3. **Read `foo` again on `machine2`**. Without invalidation, `machine2` would return its now-stale L1 entry. With invalidation, the L1 entry was dropped by the stream consumer, so this read misses L1, fetches the new value from L2, and back-fills L1 with `"baz"`.

The sample is OpenTelemetry-only. Its `Program.cs` calls `.AddOpenTelemetry()` on the caching builder and registers `AddSource("UiPath.Caching")` + `AddMeter("UiPath.Caching")` so cache-semantic activities and metrics are collected. The OTel wiring that enables per-connection Redis-command tracing for both machines is shown in `Sample.AspNetCore/OpenTelemetryConnectionMultiplexerFactory.cs`: it wraps the raw `ConnectionMultiplexer.Connect` call and registers the resulting multiplexer with `StackExchangeRedisInstrumentation.AddConnection`.

## Endpoints

The sample exposes one route per cache operation per surface. Base classes are listed once; derived controllers inherit all their actions.

| Controller | Route | Verb | Shape |
|---|---|---|---|
| `CacheController` | `GET /Cache/Get?cacheKey=x` | GET | `ICache.GetAsync<string>` |
| `CacheController` | `POST /Cache/MGet` body `["k1","k2"]` | POST | `ICache.GetAsync<string>(CacheKey[])` |
| `CacheController` | `POST /Cache/Set?cacheKey=x` body `"value"` | POST | `ICache.SetAsync<string>` |
| `CacheController` | `POST /Cache/MSet` body `{"k":"v"}` | POST | `ICache.SetAsync(KeyValuePair<CacheKey,string?>[])` |
| `CacheController` | `POST /Cache/Refresh?cacheKey=x` body `"00:05:00"` | POST | `ICache.RefreshAsync<string>` |
| `CacheController` | `DELETE /Cache/Remove?cacheKey=x` | DELETE | `ICache.RemoveAsync<string>` |
| `CacheController` | `GET /Cache/Contains?cacheKey=x` | GET | `ICache.ContainsAsync<string>` |
| `CacheController` | `GET /Cache/TimeToLive?cacheKey=x` | GET | `ICache.TimeToLiveAsync<string>` |
| `CacheController` | `GET /Cache/ExpireTime?cacheKey=x` | GET | `ICache.ExpireTimeAsync<string>` |
| `IntDefaultCacheController` | `GET /IntDefaultCache/Get?cacheKey=x` | GET | `ICache<int?>.GetAsync` |
| `IntDefaultCacheController` | `POST /IntDefaultCache/Set?cacheKey=x` body `42` | POST | `ICache<int?>.SetAsync` |
| `IntDefaultCacheController` | `POST /IntDefaultCache/Refresh?cacheKey=x` body `"00:05:00"` | POST | `ICache<int?>.RefreshAsync` |
| `IntDefaultCacheController` | `DELETE /IntDefaultCache/Remove?cacheKey=x` | DELETE | `ICache<int?>.RemoveAsync` |
| `IntDefaultCacheController` | `GET /IntDefaultCache/Contains?cacheKey=x` | GET | `ICache<int?>.ContainsAsync` |
| `IntDefaultCacheController` | `GET /IntDefaultCache/TimeToLive?cacheKey=x` | GET | `ICache<int?>.TimeToLiveAsync` |
| `IntDefaultCacheController` | `GET /IntDefaultCache/ExpireTime?cacheKey=x` | GET | `ICache<int?>.ExpireTimeAsync` |
| `BoolDefaultCacheController` | `GET /BoolDefaultCache/Get?cacheKey=x` | GET | `ICache<bool?>.GetAsync` |
| `BoolDefaultCacheController` | `POST /BoolDefaultCache/Set?cacheKey=x` body `true` | POST | `ICache<bool?>.SetAsync` |
| `BoolDefaultCacheController` | `POST /BoolDefaultCache/Refresh?cacheKey=x` body `"00:05:00"` | POST | `ICache<bool?>.RefreshAsync` |
| `BoolDefaultCacheController` | `DELETE /BoolDefaultCache/Remove?cacheKey=x` | DELETE | `ICache<bool?>.RemoveAsync` |
| `BoolDefaultCacheController` | `GET /BoolDefaultCache/Contains?cacheKey=x` | GET | `ICache<bool?>.ContainsAsync` |
| `BoolDefaultCacheController` | `GET /BoolDefaultCache/TimeToLive?cacheKey=x` | GET | `ICache<bool?>.TimeToLiveAsync` |
| `BoolDefaultCacheController` | `GET /BoolDefaultCache/ExpireTime?cacheKey=x` | GET | `ICache<bool?>.ExpireTimeAsync` |
| `HashCacheController` | `GET /HashCache/GetItem?cacheKey=x&field=f` | GET | `IHashCache.GetItemAsync<string>` |
| `HashCacheController` | `POST /HashCache/GetItems?cacheKey=x` body `["f1","f2"]` | POST | `IHashCache.GetAsync<string>(key, fields[])` |
| `HashCacheController` | `GET /HashCache/Get?cacheKey=x` | GET | `IHashCache.GetAsync<string>(key)` |
| `HashCacheController` | `POST /HashCache/Set?cacheKey=x` body `{"f":"v"}` | POST | `IHashCache.SetAsync<string>` |
| `HashCacheController` | `POST /HashCache/SetWithMetadata?cacheKey=x` body `{Values,Metadata}` | POST | `IHashCache.SetAsync` with `HashCacheEntryOptions` |
| `HashCacheController` | `POST /HashCache/Refresh?cacheKey=x` body `"00:05:00"` | POST | `IHashCache.RefreshAsync<string>` |
| `HashCacheController` | `DELETE /HashCache/Remove?cacheKey=x` | DELETE | `IHashCache.RemoveAsync<string>` |
| `HashCacheController` | `GET /HashCache/Contains?cacheKey=x` | GET | `IHashCache.ContainsAsync<string>` |
| `HashCacheController` | `GET /HashCache/TimeToLive?cacheKey=x` | GET | `IHashCache.TimeToLiveAsync<string>` |
| `HashCacheController` | `GET /HashCache/ExpireTime?cacheKey=x` | GET | `IHashCache.ExpireTimeAsync<string>` |
| `HashCacheController` | `GET /HashCache/GetMetadata?cacheKey=x` | GET | `IHashCache.GetMetadataAsync<string>` |
| `HashCacheController` | `POST /HashCache/SetMetadata?cacheKey=x` body `{"k":"v"}` | POST | `IHashCache.SetMetadataAsync<string>` |
| `ResourceDefaultHashCacheController` | `GET /ResourceDefaultHashCache/GetItem?cacheKey=x&field=f` | GET | `IHashCache<SampleResource>.GetItemAsync` |
| `ResourceDefaultHashCacheController` | `POST /ResourceDefaultHashCache/GetItems?cacheKey=x` body `["f1"]` | POST | `IHashCache<SampleResource>.GetAsync(key, fields[])` |
| `ResourceDefaultHashCacheController` | `GET /ResourceDefaultHashCache/Get?cacheKey=x` | GET | `IHashCache<SampleResource>.GetAsync(key)` |
| `ResourceDefaultHashCacheController` | `POST /ResourceDefaultHashCache/Set?cacheKey=x` body `{"f":{...}}` | POST | `IHashCache<SampleResource>.SetAsync` |
| `ResourceDefaultHashCacheController` | `POST /ResourceDefaultHashCache/SetWithMetadata?cacheKey=x` | POST | `IHashCache<SampleResource>.SetAsync` with metadata |
| `ResourceDefaultHashCacheController` | `POST /ResourceDefaultHashCache/Refresh?cacheKey=x` body `"00:05:00"` | POST | `IHashCache<SampleResource>.RefreshAsync` |
| `ResourceDefaultHashCacheController` | `DELETE /ResourceDefaultHashCache/Remove?cacheKey=x` | DELETE | `IHashCache<SampleResource>.RemoveAsync` |
| `ResourceDefaultHashCacheController` | `GET /ResourceDefaultHashCache/Contains?cacheKey=x` | GET | `IHashCache<SampleResource>.ContainsAsync` |
| `ResourceDefaultHashCacheController` | `GET /ResourceDefaultHashCache/TimeToLive?cacheKey=x` | GET | `IHashCache<SampleResource>.TimeToLiveAsync` |
| `ResourceDefaultHashCacheController` | `GET /ResourceDefaultHashCache/ExpireTime?cacheKey=x` | GET | `IHashCache<SampleResource>.ExpireTimeAsync` |
| `ResourceDefaultHashCacheController` | `GET /ResourceDefaultHashCache/GetMetadata?cacheKey=x` | GET | `IHashCache<SampleResource>.GetMetadataAsync` |
| `ResourceDefaultHashCacheController` | `POST /ResourceDefaultHashCache/SetMetadata?cacheKey=x` body `{"k":"v"}` | POST | `IHashCache<SampleResource>.SetMetadataAsync` |
| `IntDefaultHashCacheController` | `GET /IntDefaultHashCache/GetItem?cacheKey=x&field=f` | GET | `IHashCache<int?>.GetItemAsync` |
| `IntDefaultHashCacheController` | `POST /IntDefaultHashCache/GetItems?cacheKey=x` body `["f1"]` | POST | `IHashCache<int?>.GetAsync(key, fields[])` |
| `IntDefaultHashCacheController` | `GET /IntDefaultHashCache/Get?cacheKey=x` | GET | `IHashCache<int?>.GetAsync(key)` |
| `IntDefaultHashCacheController` | `POST /IntDefaultHashCache/Set?cacheKey=x` body `{"f":42}` | POST | `IHashCache<int?>.SetAsync` |
| `IntDefaultHashCacheController` | `POST /IntDefaultHashCache/SetWithMetadata?cacheKey=x` | POST | `IHashCache<int?>.SetAsync` with metadata |
| `IntDefaultHashCacheController` | `POST /IntDefaultHashCache/Refresh?cacheKey=x` body `"00:05:00"` | POST | `IHashCache<int?>.RefreshAsync` |
| `IntDefaultHashCacheController` | `DELETE /IntDefaultHashCache/Remove?cacheKey=x` | DELETE | `IHashCache<int?>.RemoveAsync` |
| `IntDefaultHashCacheController` | `GET /IntDefaultHashCache/Contains?cacheKey=x` | GET | `IHashCache<int?>.ContainsAsync` |
| `IntDefaultHashCacheController` | `GET /IntDefaultHashCache/TimeToLive?cacheKey=x` | GET | `IHashCache<int?>.TimeToLiveAsync` |
| `IntDefaultHashCacheController` | `GET /IntDefaultHashCache/ExpireTime?cacheKey=x` | GET | `IHashCache<int?>.ExpireTimeAsync` |
| `IntDefaultHashCacheController` | `GET /IntDefaultHashCache/GetMetadata?cacheKey=x` | GET | `IHashCache<int?>.GetMetadataAsync` |
| `IntDefaultHashCacheController` | `POST /IntDefaultHashCache/SetMetadata?cacheKey=x` body `{"k":"v"}` | POST | `IHashCache<int?>.SetMetadataAsync` |
| `BoolDefaultHashCacheController` | `GET /BoolDefaultHashCache/GetItem?cacheKey=x&field=f` | GET | `IHashCache<bool?>.GetItemAsync` |
| `BoolDefaultHashCacheController` | `POST /BoolDefaultHashCache/GetItems?cacheKey=x` body `["f1"]` | POST | `IHashCache<bool?>.GetAsync(key, fields[])` |
| `BoolDefaultHashCacheController` | `GET /BoolDefaultHashCache/Get?cacheKey=x` | GET | `IHashCache<bool?>.GetAsync(key)` |
| `BoolDefaultHashCacheController` | `POST /BoolDefaultHashCache/Set?cacheKey=x` body `{"f":true}` | POST | `IHashCache<bool?>.SetAsync` |
| `BoolDefaultHashCacheController` | `POST /BoolDefaultHashCache/SetWithMetadata?cacheKey=x` | POST | `IHashCache<bool?>.SetAsync` with metadata |
| `BoolDefaultHashCacheController` | `POST /BoolDefaultHashCache/Refresh?cacheKey=x` body `"00:05:00"` | POST | `IHashCache<bool?>.RefreshAsync` |
| `BoolDefaultHashCacheController` | `DELETE /BoolDefaultHashCache/Remove?cacheKey=x` | DELETE | `IHashCache<bool?>.RemoveAsync` |
| `BoolDefaultHashCacheController` | `GET /BoolDefaultHashCache/Contains?cacheKey=x` | GET | `IHashCache<bool?>.ContainsAsync` |
| `BoolDefaultHashCacheController` | `GET /BoolDefaultHashCache/TimeToLive?cacheKey=x` | GET | `IHashCache<bool?>.TimeToLiveAsync` |
| `BoolDefaultHashCacheController` | `GET /BoolDefaultHashCache/ExpireTime?cacheKey=x` | GET | `IHashCache<bool?>.ExpireTimeAsync` |
| `BoolDefaultHashCacheController` | `GET /BoolDefaultHashCache/GetMetadata?cacheKey=x` | GET | `IHashCache<bool?>.GetMetadataAsync` |
| `BoolDefaultHashCacheController` | `POST /BoolDefaultHashCache/SetMetadata?cacheKey=x` body `{"k":"v"}` | POST | `IHashCache<bool?>.SetMetadataAsync` |
| `RedisConnectionController` | `GET /RedisConnection` | GET | `IRedisConnector.IsConnected` |
| `RedisConnectionController` | `POST /RedisConnection` | POST | `IRedisConnector.ForceReconnect()` |

Source files:

- `Controllers/CacheBaseController.cs` — non-generic `ICache` base (Get / MGet / Set / MSet / Refresh / Remove / Contains / TimeToLive / ExpireTime).
- `Controllers/CacheBaseOfTResourceController.cs` — typed `ICache<T>` base (Get / Set / Refresh / Remove / Contains / TimeToLive / ExpireTime).
- `Controllers/CacheController.cs` — `CacheController` (`ICache`), `IntDefaultCacheController` (`ICache<int?>`), `BoolDefaultCacheController` (`ICache<bool?>`).
- `Controllers/HashCacheBaseController.cs` — non-generic `IHashCache` base (GetItem / GetItems / Get / Set / SetWithMetadata / Refresh / Remove / Contains / TimeToLive / ExpireTime / GetMetadata / SetMetadata).
- `Controllers/HashCacheBaseOfTResourceController.cs` — typed `IHashCache<T>` base (same surface).
- `Controllers/HashCacheController.cs` — `HashCacheController` (`IHashCache`), `ResourceDefaultHashCacheController` (`IHashCache<SampleResource>`), `IntDefaultHashCacheController` (`IHashCache<int?>`), `BoolDefaultHashCacheController` (`IHashCache<bool?>`).
- `Controllers/RedisConnectionController.cs` — connection-state endpoints.

## Common adjustments

- **Want to see cache telemetry?** Open the Aspire dashboard's traces and metrics views — the `UiPath.Caching` source/meter and `Redis.*` command spans flow there with no extra configuration.
- **Want to inspect L1 invalidation events on the wire?** Open RedisInsight, navigate to the stream key (`<AppShortName>:st:<topic>`), and watch entries appear as you write keys via Swagger.
- **Want to run only one machine?** Set `UseSingleMachine: true`. The second machine resource is skipped.
- **Want sharded Redis?** Set `UseShardedRedis: true`. Aspire boots a 6-node cluster (2 masters, 4 replicas) and runs `redis-trib create --replicas 1 ...` to form the cluster. Initial cluster formation takes ~10-20 seconds — the AppHost includes a `sleep 10` hedge for the race between Aspire's `WaitFor` and the Redis process actually listening.

## See also

- [quickstart.md](quickstart.md) — for the equivalent wiring in your own service.
- [how-to/telemetry-and-strategies.md](how-to/telemetry-and-strategies.md) — for the OpenTelemetry adapter and Redis instrumentation.
- [how-to/broadcast.md](how-to/broadcast.md) — for the cross-node sync mechanics the sample exercises.
