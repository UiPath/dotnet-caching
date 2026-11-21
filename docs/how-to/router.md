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
    .UseBypassWhen(() => settings.BypassLibrary)        // optional live kill switch, read on every call
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

The library cache must implement `IMigrationCache`; a stack whose cache does not counts as a library that failed to start (see below). On the library path reads use `IMigrationCache.ReadSnapshotAsync`: the value and its TTL come from one snapshot on the primary, a failed read is unavailable and never absent, and an unreadable value is left in place. `SnapshotOutcome.Unreadable` tells unreadable from absent. `IMigrationCache.WriteAsync` and `DeleteAsync` report a write refused before sending (disconnected, token already cancelled, key map or serializer threw) as `NotSent`, an acknowledged one as `Committed`, and any other outcome as `Unknown`, which the router never replays. When the library is unavailable and the source cannot answer, or answers only with an unreadable value, the call throws `CacheUnavailableException`.

## Modes: bypass and draining

`IRouter.Mode` is `Library` or `BypassLibrary`, `ModeReason` says why (`Flag`, `Requested`, `LibraryUnavailable`), and `ModeChanged` is raised once a transition has drained. In `BypassLibrary` every call takes the source path: no library client, no shared-tier hit or fill, no retry on the other client. A write still evicts on this node and, if the library is up, hands its announcement to a bounded background worker, so the call never waits on the library and peers on the library stay coherent (a registration with no local cap announces nothing). Every publish is bounded by the source timeout. A router is in bypass while any of three things holds: the live `UseBypassWhen` setting is true, `EnterBypassAsync` was called and not withdrawn, or the library has not started. The setting is read on every call; an exception counts as true for that call and changes no mode.

Entering: new calls take the source at once, the shared tier is cleared and every mark advanced (a fill that started before is rejected), then the transition waits for calls started in library mode, at most `UseSourceTimeout` (default 30 seconds; give it your old client's timeout). `EnterBypassAsync` returns after that. A late completion from the previous mode never fills the tier.

Leaving: the router waits for every cache's `ITopicSubscriptionState.IsSubscribed` (a topic that does not report it counts as subscribed), then for pending writes to settle. Then new calls take the library, but no shared-tier hit or fill is served until calls started in bypass have ended (same bound) and the tier is cleared again. One deadline of the source timeout covers readiness, settlement and the drain of a leave. If readiness or settlement does not complete in it, the router stays in bypass, logs, counts `leave_timed_out` and tries again after the same time; `LeaveBypassAsync` then throws `TimeoutException`. Once the router has switched to library mode the transition finishes whatever the caller does, so cancelling `LeaveBypassAsync` only keeps the request when it is still waiting. `LeaveBypassAsync` returns when the router is in library mode, or at once if the setting or a missing library still asks for bypass. `ModeChanged` handlers run after the transition's lock is released, in order, so a handler may call back into the router. The old client's own local tier is the application's: turn it on after `ModeChanged` reports bypass and off before calling `LeaveBypassAsync`.

## Library that fails to start

The router owns the whole lifecycle of its library stack: the host does not build or start it (the stack's own hosted service is not registered), so a stack that cannot be built or started never fails the host. Router creation starts the stack in the background and waits at most five seconds, in real time, for it; if it fails or is late, the router starts in `BypassLibrary` with reason `LibraryUnavailable`, logs the exception at error and counts `library_start_failed`. The attempt carries on, retrying after 1 s and doubling to at most 1 min, and when one succeeds the router leaves bypass in the order above. A result that arrives after the router stopped is released, never used, and a factory that hangs delays neither creation nor `DisposeAsync`. Caches are attached all or none: a failure part way gives back the subscriptions and hosted services already taken. With the bypass setting on at start the library is not started until the setting goes off. A setting no start could satisfy (a connection timeout beyond the settle horizon) still fails the host at startup.

## Node identity

The router's source identity, which its events, subscriptions and advertisements carry, is the configured source with a process part added to the host part: `urn:host-p{processId}?stack=UiPath.Caching.Migration`. The default process id is the operating system process id and eight random hex digits, so two processes on one host (an overlapped recycle) are peers and hear each other's events, and so are two routers in one process; `UseNodeIdentity` names it instead. Only the router's named stack changes; the application's own stack and `CacheOptions.MachineUri` keep the host name alone. A Streams topic keeps one consumer group per identity and topic, so each process start adds a group per router topic. A router that stops gracefully (`StopAsync` or `DisposeAsync`) removes its own groups with `XGROUP DESTROY`, best effort and bounded, after its topics are removed. A process that crashes or is killed leaves its groups behind: the stream health maintainer keeps a group that is caught up with the stream head, so on a quiet topic those groups stay until something reaps them. N pods rolled M times with crashes leave up to N times M groups per topic; plan for that, or reap them from outside.

The router keeps its own entry in its stack's node hash (`caching:nodes` under the router stack's key strategy), field `{source identity}#router`, text `kind=router;inst=...;build=...;mig=t1+t2`. `byp` is the one token today; the pin and the Phase B tokens join later, and a reader skips tokens it does not know. The entry is written every `FormatNegotiation.AdvertiseInterval` and at once on a mode change, stamped with the server's clock, and counts for `LivenessWindow` plus two intervals. A stopping router writes it with `left=1`. Pass `UseSourceConnection(() => sourceDatabase)` so the entry is written over the source client's connection and is kept while the library is down; the database must not add a key prefix of its own. Without it the application's connection carries the entry. A write that fails is logged and retried with backoff (500 ms doubling, at most one interval), never thrown.

## Precedence

A call is decided by the first of these that applies, and the router logs this order with its settings at startup:

1. Bypass (the setting, an explicit request, or a library that has not started): source path, no shared tier.
2. The group predicate (`UseLibraryWhen`): library or source; an exception means source.
3. Pin: not available in this version.

## Telemetry

Meter `UiPath.Caching.Migration`, counters `local_hit`, `redis_hit`, `miss`, `factory_runs`, `unavailable`, `unreadable`, `publish_failed` and `pending`, each tagged `cache`, `group`, `path` and `path_reason` (`flag`, `predicate_error`, `bypass`, `bypass_flag_error`, `library_unavailable`, `other_client_read`, `other_client_unreadable`). `mode_change` (tags `mode`, `reason`) counts completed transitions and `library_start_failed` counts failed starts.
