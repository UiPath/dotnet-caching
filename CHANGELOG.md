# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/)

## [Unreleased]

### Added

- `ICache.GetCacheEntryAsync<T>` and `ICache.GetCacheEntriesAsync<T>` — bundled GET + TTL reads. Implementations may fetch the value and its remote expiration in a single network round-trip; `RedisCache` uses a `MULTI`/`EXEC` transaction (mirroring the hash-cache pattern).
- `RedisStreamsTopic` supports an optional Pub/Sub notify doorbell (`NotifyEnabled`) that drops publish-to-deliver latency from the poll interval to network RTT, while preserving stream durability and consumer-group semantics. Channel name defaults to the stream's Redis key joined with `NotifyChannelName` (default `"notify"`) using the same `CacheOptions.Separator` as the rest of the key scheme. Set `NotifyShardedPubSub = true` to use sharded Pub/Sub (`SPUBLISH`/`SSUBSCRIBE`, requires Redis 7.0+) so the doorbell does not fan out across cluster nodes; the sharded strategy wraps the stream key as a Redis Cluster hash tag (or inherits an existing one) so the channel and stream share the same slot — `XADD` and `SPUBLISH` go to the same node. Otherwise regular `PUBLISH`/`SUBSCRIBE` is used. Override the channel entirely via `NotifyChannelStrategy`. Pub/Sub is best-effort — the existing poll continues to run as a safety net.

### Changed

- `MultilayerCache` and `MultilayerHashCache` no longer issue a separate `ExpireTimeAsync` call after a cache-miss read. They now use the bundled `ICacheEntry.Expiration` returned by the inner cache. For Redis-backed multilayer caches this collapses N + 1 sequential commands to one transaction (one round-trip, one `EXEC` dependency in telemetry) when fetching N keys, eliminating per-key `PTTL`/`EXPIRETIME` traffic on the read path. Public APIs are unchanged.
- **BREAKING:** `ICachingBuilder.RegisterOnCompleteCallback(Action<ICachingBuilder>)` is replaced by `RegisterOnCompleteCallback(object key, Action<ICachingBuilder>)`. `CachingBuilder` keeps a per-builder set of seen keys and ignores re-registrations against an already-seen key on the same instance. Callers should pass a deterministic key (typically `typeof(YourExtensionsClass)`).
- **BREAKING:** `Caching.Polly.CachingBuilderExtensions.ConfigureTelemetry(this ICachingBuilder, bool, Action<TelemetryOptions>?)` is removed. Telemetry is now configured exclusively through `AddResilienceStrategies(..., configureTelemetryOptions: ...)` and `ResiliencePoliciesOptions.TelemetryEnabled`, which flow through `IOptions<TelemetryOptions>` / `IOptions<ResiliencePoliciesOptions>` per-container instead of process-static fields.

### Fixed

- `RedisCache.GetCacheEntryAsync` / `GetCacheEntriesAsync` and `RedisHashCache.GetCacheEntryAsync` now pass `CommandFlags.PreferReplica` to `transaction.ExecuteAsync(...)`, so the bundled GET + TTL transaction actually routes to a replica. SE.Redis ignores the flag set on inner queued commands when picking a server for the transaction; without an explicit flag on `ExecuteAsync` the read landed on the master. These read paths now also use the `_read` resilience pipeline instead of `_write`.
- Bulk write transactions in `RedisCache.SetAsync(KeyValuePair<>[])` and `RedisHashCache` Set/Refresh paths now pass `CommandFlags.DemandMaster` to `transaction.ExecuteAsync(...)` for symmetry with the read fix and to make the master-only routing intent explicit (inner-command flags do not propagate to transaction routing in SE.Redis).
- `AddInMemoryRedis` and `AddResilienceStrategies` no longer use a process-static `_callbackRegistered` flag to gate their on-complete callback. The static guard silently broke L1 invalidation / resilience pipelines for any second host wired up in the same process: only the first builder's callback ever ran, leaving subsequent builders with `NullChangeTokenFactory` and `ResiliencePipelineHolder.Empty`. Both extensions now register their callbacks against `typeof(self)` via the keyed `RegisterOnCompleteCallback` overload.
- `AddResilienceStrategies` no longer stores telemetry enable/config in process-static fields. `IResiliencePipelineFactory` now resolves `IOptions<TelemetryOptions>` and `IOptions<ResiliencePoliciesOptions>` from its own container, so two hosts in the same process keep independent telemetry settings. Previously the second `AddResilienceStrategies` call would silently overwrite the first host's telemetry configuration (the factory singleton read the statics at resolution time, not at registration time).

