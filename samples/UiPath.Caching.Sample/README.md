# Sample.AspNetCore Aspire Sample

Run the sample through the Aspire AppHost from the `Caching/` folder. The
AppHost starts this project with the `Machine1` and `Machine2` launch profiles
by default, and it starts the Redis resources required by the sample.

Prerequisites:

- .NET SDK from `global.json`
- Docker Desktop or another Docker engine reachable by Aspire

## Default Run

```powershell
dotnet run --project Sample.AspNetCore.AppHost/Sample.AspNetCore.AppHost.csproj --launch-profile http
```

Default resources:

- `sample-aspnetcore-machine1`: `http://localhost:5017`, `https://localhost:7020`
- `sample-aspnetcore-machine2`: `http://localhost:5018`, `https://localhost:7021`
- Redis: `localhost:6379`
- Redis Insight: `http://localhost:8001`

Open the Aspire dashboard URL printed by the AppHost. Swagger is available from
each sample instance at `/swagger`.

## Redis Commands In Aspire

With `SampleAspNetCore:UseOpenTelemetry=true`, Redis calls are exported to the
Aspire dashboard as trace spans. The sample enables verbose Redis database
statements and adds a `redis.command` span tag, so command names are visible in
the Trace view.

To generate Redis traffic:

1. Start the AppHost.
2. Open Swagger for either sample instance, for example
   `http://localhost:5017/swagger`.
3. Call cache endpoints such as `POST /Cache/Set`, `GET /Cache/Get`,
   `POST /HashCache/Set`, or `GET /HashCache/Get`.
4. Open the Aspire dashboard, go to **Traces**, select the sample request, and
   inspect the Redis child spans. The span display name starts with `Redis`,
   and the details include `redis.command` and `db.statement` attributes.

## Flags

The defaults live under `SampleAspNetCore` in `appsettings.json` and
`Sample.AspNetCore.AppHost/appsettings.json`.

| Setting | Default | Behavior |
| --- | --- | --- |
| `UseOpenTelemetry` | `true` | Enables Aspire/OpenTelemetry wiring. When `false`, the sample uses the existing UiPath telemetry pipeline. |
| `UseRedisInsight` | `true` | Starts Redis Insight with the Redis resources. |
| `UseSingleMachine` | `false` | Starts only `Machine1` when `true`. |
| `UseShardedRedis` | `false` | Starts the sharded Redis topology when `true`. |
| `UseShardedPubSub` | `false` | Enables Redis sharded Pub/Sub for explicit Redis 7 testing. |

Use environment variables to override flags for one run:

```powershell
$env:SampleAspNetCore__UseSingleMachine = 'true'
dotnet run --project Sample.AspNetCore.AppHost/Sample.AspNetCore.AppHost.csproj --launch-profile http
Remove-Item Env:SampleAspNetCore__UseSingleMachine
```

Run without OpenTelemetry:

```powershell
$env:SampleAspNetCore__UseOpenTelemetry = 'false'
dotnet run --project Sample.AspNetCore.AppHost/Sample.AspNetCore.AppHost.csproj --launch-profile http
Remove-Item Env:SampleAspNetCore__UseOpenTelemetry
```

Run without Redis Insight:

```powershell
$env:SampleAspNetCore__UseRedisInsight = 'false'
dotnet run --project Sample.AspNetCore.AppHost/Sample.AspNetCore.AppHost.csproj --launch-profile http
Remove-Item Env:SampleAspNetCore__UseRedisInsight
```

## Sharded Redis

Shard mode models the old `docker-compose.shard.yml` layout with two Redis
masters, four replicas, and the cluster creation container.

```powershell
$env:SampleAspNetCore__UseShardedRedis = 'true'
dotnet run --project Sample.AspNetCore.AppHost/Sample.AspNetCore.AppHost.csproj --launch-profile http
Remove-Item Env:SampleAspNetCore__UseShardedRedis
```

Shard mode sets `Caching:ShardKeyEnabled=true` for both sample instances and
exposes Redis node ports `6379` through `6384`. With Redis Insight enabled, the
AppHost exposes Redis Insight on `http://localhost:8001` and
`http://localhost:8002`.

Enable sharded Pub/Sub only when explicitly testing it:

```powershell
$env:SampleAspNetCore__UseShardedRedis = 'true'
$env:SampleAspNetCore__UseShardedPubSub = 'true'
dotnet run --project Sample.AspNetCore.AppHost/Sample.AspNetCore.AppHost.csproj --launch-profile http
Remove-Item Env:SampleAspNetCore__UseShardedRedis
Remove-Item Env:SampleAspNetCore__UseShardedPubSub
```
