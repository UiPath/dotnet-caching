# Server support

Which Redis-compatible servers the library supports and where each one falls short. Results are from the integration suite (6500 tests, StackExchange.Redis 3.4.31).

## Support policy

- Self-hosted servers follow upstream releases. No release in 2 years means unsupported.
- Managed services follow their own lifecycle.
- Self-hosted floor: Redis 6.2.

## Self-hosted (tested)

| Server | Version | Status | Notes |
|---|---|---|---|
| Redis | 6.2, 7.4, 8 | Supported | Standalone and cluster. |
| Valkey | 8.1, 9 | Supported | |
| Dragonfly | 2.1.0 | Supported | See known limits. |
| Garnet | 2.2.13 | Supported with fallback | Broadcast over Pub/Sub (no Streams). |
| Kvrocks | 2.17.0 | Supported with fallback | Needs `protocol=resp2`. Broadcast over Pub/Sub. |
| Redis 6.0, 7.0, twemproxy, KeyDB, predixy | | Unsupported | |

## Managed services

| Service | Status | Notes |
|---|---|---|
| Azure Managed Redis | Supported, tested | Redis 7.4.3, OSS cluster. See known limits and [Active-Active](#active-active). |
| Azure Cache for Redis (classic) | Supported until 2028-09-30 | Vendor docs only. |
| Amazon ElastiCache | Supported | Vendor docs only. |
| Amazon MemoryDB | Supported (single Region) | Multi-Region lacks the TTL commands the library needs. |
| Google Memorystore (Redis, Redis Cluster, Valkey) | Supported | Vendor docs only. |
| Redis Cloud | Supported | `SELECT` does nothing; keep database 0. |
| Upstash | Not verified | |

For AWS and Google Cloud IAM tokens, see [cloud IAM authentication](../recipes/cloud-iam-authentication.md). For Entra ID, see [Azure Entra authentication](../recipes/azure-entra-authentication.md).

## What the library adapts to

- No Streams: broadcast falls back to Pub/Sub. See [When the server has no Streams](../how-to/broadcast.md#when-the-server-has-no-streams).
- Streams that keep the groups of a deleted stream (Kvrocks): broadcast uses Pub/Sub.
- `SPUBLISH` refused: the doorbell uses `PUBLISH`.
- `EXPIRETIME` missing: the library uses `PTTL`.
- `SELECT` ignored: the library logs one error.
- A cluster entry endpoint (Azure Managed Redis): counted once, not as an extra shard.
- `PUBSUB NUMSUB` that is not a client count (Azure Managed Redis): Pub/Sub writes stay JSON unless `AssumeAllNodesCanRead` is set.

## Known limits per server

- **Dragonfly.** A consumer group made by another node after the stream was fully trimmed can report one false missed event.
- **Kvrocks.** Without `protocol=resp2`, StackExchange.Redis drops the subscription on every Pub/Sub message, so nodes miss events. It also ignores `SELECT`.
- **Azure Managed Redis.** One database only. Pub/Sub subscriber counts are cluster-wide, so binary formats stay off on Pub/Sub deployments unless `AssumeAllNodesCanRead` is set.
- **Any cluster.** After a dropped connection, StackExchange.Redis 3.4.31 can restore a subscription on two nodes, so a message may arrive twice until the next reconnect ([seredis/StackExchange.Redis#3337](https://github.com/seredis/StackExchange.Redis/pull/3337)).

## Active-Active

Tested on Azure Managed Redis active geo-replication, West Europe and France Central, one node per region.

- Updates reach the other region in about 50 to 100 ms, and invalidations in about 90 to 110 ms.
- The invalidation can arrive before the value. The other region then reloads the old value and keeps it in its local tier until that entry expires. This happened in up to 14% of updates, over Streams and over Pub/Sub.
- Streams report false missed events after a trim. Each one clears the local tier, which costs reads but serves nothing stale.

Set `RepeatInvalidationAfter` on the InMemoryRedis options to about 1 s, above the replication lag. A node then drops a key's local copy when the invalidation arrives and again after the delay. In 300 rounds with 1 s, no update stayed stale and no concurrent write diverged; without it, 14 of 100 stayed stale. It costs one extra Redis read per remote invalidation. A region that lags longer than the delay can still serve stale values.

## How to check your server

```bash
eng/compat-matrix.sh [results-dir] [name ...]
```

It needs Docker. Names: `redis-6.2`, `redis-7.4`, `redis-8`, `valkey-8.1`, `valkey-9`, `dragonfly`, `kvrocks`, `garnet`, `redis-cluster`. For a managed service, set `RUN_REDIS_INTEGRATION_TESTS=1` and `REDIS_CONNECTION_STRING`, then run the test project.
