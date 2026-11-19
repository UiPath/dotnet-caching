# Routing between an existing Redis client and the library

`UiPath.Caching.Migration` lets an application move a cache group between its existing Redis client (the source) and UiPath.Caching, and back, on any call, with both clients reading and writing the same Redis keys and bytes. One local tier per node serves both paths, and every write, remove and clear tells the other nodes.

## Register

```csharp
services.AddCaching(configuration, b => b.AddRedisConnection());   // the application's own stack, with its connector

services.AddCachingRouter(configuration, router => router
    .Add(new RouterCacheRegistration<Folders>
    {
        Name = "folders",
        Group = "navigation",
        Source = new FoldersSource(oldClient),          // your ISourceCache<Folders>
        RedisKey = key => $"n:folders,c:{key}",         // the exact key text the old client uses
        LocalCap = TimeSpan.FromSeconds(30),            // zero: no local copy
        DefaultExpiry = TimeSpan.FromHours(1),
    })
    .UseLibraryWhen(group => flags.IsOn(group))         // true: library path; read once per call
    .UseEnvelope(new OldEnvelopeSerializer()));         // ISerializerProxy<byte[]> of the old value format

var folders = provider.GetRequiredService<IRouter>().Get<Folders>("folders");
var value = await folders.GetOrAddAsync(key, token => LoadAsync(key, token), CacheExpiry.After(TimeSpan.FromMinutes(5)));
```

The router builds a Redis-only named stack on the application's connector (`AddNamedCachingOnPrimaryConnection`) with a pass-through key strategy, your envelope as the only serializer and `RemoveUnreadable = false`. It compiles against the public `UiPath.Caching` API only.

## Behaviour

- **Path.** The predicate is read once per call and an exception means the source. A call stays on one path, including the write after a factory run.
- **Local tier.** One memory cache per node, keyed by cache name and key as two fields, each entry capped per cache; it has no size limit and no bound on the number of keys within the cap. A fill is installed only if no eviction of that key and no clear of that cache happened after the read started. The local deadline is the read start plus the TTL the library reports, bounded by the cap; the source reports no TTL, so the cap alone bounds its fills.
- **Writes.** The Redis command completes, then the node evicts the key, then it publishes. A write of unknown outcome (`WriteOutcome.Unknown`) evicts and publishes at once, keeps the key off the local tier, then evicts and publishes again once the late commit can no longer land: after the longer of `PendingWindow` and the settle deadline the adapter returns with `WriteResult.UnknownWithin`. `PendingWindow` must cover the longest command lifetime of your source client; for the library path the router uses the cache timeout plus the longest command timeout of the connected multiplexer's effective configuration (async, sync and the maintenance relaxed timeout, whatever set them), read once without waiting for a connection and kept across disconnections (10 minutes until it has been read), and a configured timeout above one day fails the router at startup, and `UseLibrarySettleBound` replaces that with a value you give. A failed publish is kept once per key and retried by one worker with backoff, counted against the local cap; past 1024 waiting keys it keeps one topic invalidation for the cache instead.
- **Events.** One topic per cache (`router:{cache}`). A peer's key event evicts the key; a topic invalidation or any missed-events signal clears that cache's local entries.
- **Clear.** Publish, `ISourceCache.ClearAsync`, publish again. The scan stays with your source.
- **Outcomes.** On the library path an unavailable client runs the factory only when the source answers absent (the loaded value is then written to the source); when the source cannot answer either, the call throws `CacheUnavailableException`. An unreadable value is retried on the other client; unreadable on both is a counted miss and the key stays, but under `GetOrAddAsync` the factory result overwrites it. A write is retried on the other client only when the first reports `NotSent`. After `StopAsync` or dispose a call throws `ObjectDisposedException` before sending anything; stop waits a bounded time, then announces a cache-level invalidation; a pending key keeps its own timer and gets its final eviction and announcement at its real deadline, and the router's resources are released only after it, its announcement and any admitted call have ended; `StopAsync` returns after the bounded wait, `DisposeAsync` waits for all of it.
- **Cold start.** Every loaded value publishes a `Set` event to the peers, which evict the key, so a cold node's loads cost one event each.
- **Single flight.** Concurrent misses for one key on a node share one load, whichever path serves them. A caller that starts after a write, remove or clear of the key does not join an older load, and that load does not write its result if the key or cache changed while its factory ran.

The library cache must implement `IMigrationCache`, or constructing the router throws `NotSupportedException`. On the library path reads use `IMigrationCache.ReadSnapshotAsync`: the value and its TTL come from one snapshot on the primary, a failed read is unavailable and never absent, and an unreadable value is left in place. A serializer probe tells unreadable from absent. The library's writes answer only true or false, so one refused before sending (disconnected, token already cancelled, key map or serializer threw) is `NotSent` and any other failure is `Unknown`, which the router never replays. When the library is unavailable and the source cannot answer, or answers only with an unreadable value, the call throws `CacheUnavailableException`.

## Telemetry

Meter `UiPath.Caching.Migration`, counters `local_hit`, `redis_hit`, `miss`, `factory_runs`, `unavailable`, `unreadable`, `publish_failed` and `pending`, each tagged `cache`, `group`, `path` and `path_reason` (`flag`, `predicate_error`, `other_client_read`, `other_client_unreadable`).
