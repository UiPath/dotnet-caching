# Setting up

Packages:

* UiPath.Platform.Caching.Abstractions - contains interfaces and core types shared with other caching packages
* UiPath.Platform.Caching.Runtime - includes actual implementation for Redis and InMemory caching, RedisStream & RedisPubSub event topics
* UiPath.Platform.Caching.Telemetry - Implementation for UiPath.Platform.Telemetry
* UiPath.Platform.Caching.Polly - Resilience policies using Polly
* UiPath.Platform.Caching.CloudEvents - Caching events implementation using CloudEvents
* UiPath.Platform.Caching.AspNetCore - Redis dynamic filters & Redis profiling telemetry

Startup configuration:

```csharp
builder.Host
    .ConfigureCaching(x => 
        x.AddRedisConnection()
        .AddBroadcast()
        .AddRedis()
        .AddInMemoryRedis()
        .AddMemory()
        .AddResiliencePolicies()
        .AddCloudEvents()
        .AddTelemetry());

    //filter processor for dynamic filters
    builder.Services
        .AddDynamicFilter<RedisDependencyFilterOptions, RedisDependencyFilterProcessor>("Redis", "RedisDependency");
    
    //add redis healthcheck
    builder.Services.AddHealthChecks()
    .Add(new HealthCheckRegistration(
            "redis",
            sp => new RedisHealthCheck(sp.GetRequiredService<IRedisConnector>(), sp.GetRequiredService<IRedisPlannedMaintenance>()),
            default,
            default,
            TimeSpan.FromSeconds(3)));
```

The above configuration will load the settings from `Caching` section from appSettings.json.
The same results can be achieve using the configuration options.

```json
{
  "Caching": {
    "Enabled": true,
    "TelemetryEnabled": true,
    "BroadcastEnabled": true,
    "DefaultCache": "InMemoryRedis",
    "DefaultTopic": "RedisStreams",
    "SourceUri": "urn:machine1",
    "AppShortName": "app",
    "ShardKeyEnabled": false,
    "AuditEnabled": false,
    "LargeValueThreshold": 20000,
    "Connections": {
      "Redis": {
        "ConnectionString": "localhost:6379,localhost:6380,localhost:6381,abortConnect=false, connectRetry=3, keepAlive=30,name=test,syncTimeout=250",
        "LogConnectionFailedEvents": true,
        "LogConnectionRestoredEvents": true,
        "EnableHangDetection": true,
        "LastWriteIntervalThresholdMilliseconds": "15000",
        "LastReadIntervalThresholdMilliseconds": "15000",
        "BackOffMilliseconds": "1000",
        "ProfilerEnabled": true
      }
    },
    "Broadcast": {
      "RedisPubSub": {
        "Enabled": true,
        "ConsumerCapacity": "2048", //-1 for unlimited
        "FullMode": "Wait" // default Wait, see https://learn.microsoft.com/en-us/dotnet/api/system.threading.channels.boundedchannelfullmode?view=net-7.0
      },
      "RedisStreams": {
        "Enabled": true,
        "MaxLength": "32768",
        "PollBatchSize": "4096",
        "PollInterval": "0:00:00.250",
        "ProcessingTimeout": "0:00:05",
        "ConsumerCapacity": "2048", //-1 for unlimited
        "FullMode": "Wait" // see https://learn.microsoft.com/en-us/dotnet/api/system.threading.channels.boundedchannelfullmode?view=net-7.0
      }
    },
    "InMemoryRedis": {
      "Enabled": true,
      "DefaultExpiration": "0:05:00",
      "Timeout": "0:00:01",
      // LocalMaxExpiration is the L1 (in-memory tier) cap on entry lifetime.
      // The older "PrimaryMaxExpiration" name still binds for backcompat but is marked [Obsolete].
      "LocalMaxExpiration": "0:03:00",
      "TrackStatistics": true,
      "StatisticsFlushInterval": "0:01:00"
    },
    "Redis": {
      "Enabled": true,
      "DefaultExpiration": "0:07:00",
      "Version": 6
    },
    "InMemory": {
      "Enabled": true,
      "BroadcastEnable": true,
      "DefaultExpiration": "0:05:00",
      "Timeout": "0:00:01",
      "TrackStatistics": true,
      "StatisticsFlushInterval": "0:01:00"
    },
    "ResiliencePolicies": {
      "DurationOfBreak": "0:01:00",
      "RequestTimeout": "00:00:00.500",
      "RetryCount": 3
    }
  }
}
```

## Distributed lock

When you register a real `IDistributedLock` (e.g. `RedisDistributedLock` via `AddRedisDistributedLock`), the hydrating cache's `RehydrationCoordinator` uses it for cross-node dedup and post-failure cooldown: `TryAcquireAsync` returns `null` when the lock was not acquired (either contended — another node is rehydrating the same key or its cooldown is still active — or the lock backend is unavailable). The lock TTL (`factoryTimeout + cooldown`) enforces the cooldown window when a rehydrate fails. Backend-failure visibility is preserved separately via `cache.distributedlock.unavailable` telemetry.

When **no `IDistributedLock` is registered**, the fallback is `NullDistributedLock` — a pure no-op shim. In this mode:

* `TryAcquireAsync` always returns a no-op handle (no contention check).
* The blocking `AcquireAsync` returns a no-op handle (no mutual exclusion).
* `RehydrationCoordinator` cross-node dedup and cooldown enforcement do **not** apply — rehydrate can trigger per node on every hit past threshold (subject only to the per-node `_inFlight` single-flight).
* Use this only for single-node setups where cross-node coordination is not needed; for multi-node deployments, wire `RedisDistributedLock` (or another real distributed lock).

There are 2 important settings:

* AppShortName - the caching prefix for all keys. This will avoid cache key conflicts when multiple apps are using the same Redis cache server.
* SourceUri - represents the machine/pod where the app is running, used in sending & processing cache events

:warning: For production readiness twick & perf test you're app with different caching configuration (Resilience policies, Broadcast consumer capacity, batch interval, topic full mode)
If you are using a cluster with multiple shards enable key sharding `"ShardKeyEnabled": true`
To proctect agains large values in redis you can enable the key audit size
` "AuditEnabled": false, "LargeValueThreshold": <<bytes>>`

By default the library comes with 3 default caching providers, you can one or more in the same time

* InMemory - memory only cache (it can be configured to broadcast synchronization cache events)
* Redis - redis only cache
* InMemoryRedis - multi layer cache implementation, InMemory first layer, Redis as inner cache

Using the library. You can refer 4 interfaces

* [`ICache`](/Caching.Abstractions/ICache.cs) \- retrive/set objects from cache
* [`ICache<T>`](/Caching.Abstractions/IHashCacheOfT.cs) \- generic implementation of `ICache`, it will use the provider set in `Caching:DefaultCache`
* [`IHashCache`](/Caching.Abstractions/IHashCache.cs) \- retrive/set dictionary of objects from cache\. You can retrive a subset of keys\. The expiration/extension will take place for the entire dictionary
* [`IHashCache<T>`](/Caching.Abstractions/IHashCache.cs) \- generic implementation of `IHashCache<T>`, it will use the provider set in `Caching:DefaultCache`

```csharp
public class MyService
{    
    private readonly ICache<DtoObj> _cache;
    
    public MyService(ICache<DtoObj> cache) =>
        _cache = cache;

  public Task<DtoObj> GetObjAsync(string cacheKey, CancellationToken token) =>
      _cache.GetAsync(cacheKey, token);
}

public class MyService2
{    
    private readonly IHashCache<DtoObj> _cache;
    
    public MyService2(IHashCache<DtoObj> cache) =>
        _cache = cache;
    
    public Task<IDictionary<string, DtoObj?>> GetAsync(string cacheKey, string[] fields, CancellationToken token) =>
        _cache.GetAsync(cacheKey, fields, token);
}

public class DtoObj
{
    public int Prop{get;set;}
}
```

