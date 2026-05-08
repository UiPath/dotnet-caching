# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/)

## [Unreleased]

### Added

- `ICache.GetCacheEntryAsync<T>` and `ICache.GetCacheEntriesAsync<T>` — bundled GET + TTL reads. Implementations may fetch the value and its remote expiration in a single network round-trip; `RedisCache` uses a `MULTI`/`EXEC` transaction (mirroring the hash-cache pattern).

### Changed

- `MultilayerCache` and `MultilayerHashCache` no longer issue a separate `ExpireTimeAsync` call after a cache-miss read. They now use the bundled `ICacheEntry.Expiration` returned by the inner cache. For Redis-backed multilayer caches this collapses N + 1 sequential commands to one transaction (one round-trip, one `EXEC` dependency in telemetry) when fetching N keys, eliminating per-key `PTTL`/`EXPIRETIME` traffic on the read path. Public APIs are unchanged.

### Fixed

- `RedisCache.GetCacheEntryAsync` / `GetCacheEntriesAsync` and `RedisHashCache.GetCacheEntryAsync` now pass `CommandFlags.PreferReplica` to `transaction.ExecuteAsync(...)`, so the bundled GET + TTL transaction actually routes to a replica. SE.Redis ignores the flag set on inner queued commands when picking a server for the transaction; without an explicit flag on `ExecuteAsync` the read landed on the master. These read paths now also use the `_read` resilience pipeline instead of `_write`.
- Bulk write transactions in `RedisCache.SetAsync(KeyValuePair<>[])` and `RedisHashCache` Set/Refresh paths now pass `CommandFlags.DemandMaster` to `transaction.ExecuteAsync(...)` for symmetry with the read fix and to make the master-only routing intent explicit (inner-command flags do not propagate to transaction routing in SE.Redis).

