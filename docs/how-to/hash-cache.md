# Hash cache

`IHashCache<T>` models "one entry, many fields, one TTL" — like a Redis hash. Use it when
you want to read or write a subset of fields without round-tripping the whole object.

## When to use

- **Per-field reads on a logical record** — e.g. a user profile where most calls only need
  `Name` and `Email`. `IHashCache<UserFieldValue>` lets `GetItemAsync` fetch one field;
  `ICache<User>` would always materialize the whole `User`.
- **Side-channel metadata** — attach freshness / source / version info alongside the payload
  without polluting the payload type.
- **Bulk writes to one logical record** — `SetAsync(key, IDictionary<string, T?>)` issues a
  single Redis `HSET`, not N `SET`s.

## When not to use

- Single-value caches — use `ICache<T>`.
- Logical records where the whole thing is always read together — the per-field overhead
  isn't free.

## Basic CRUD

```csharp
public class UserDirectoryService(IHashCache<UserFieldValue> cache)
{
    // Read all fields of a logical record — Redis HGETALL
    public ValueTask<IDictionary<string, UserFieldValue?>> GetAllAsync(string userId, CancellationToken token) =>
        cache.GetAsync(userId, token);

    // Read one field — single Redis HGET
    public ValueTask<UserFieldValue?> GetEmailAsync(string userId, CancellationToken token) =>
        cache.GetItemAsync(userId, "email", token);

    // Write a full record — single Redis HSET with all fields
    public ValueTask<bool> SetAsync(string userId, IDictionary<string, UserFieldValue?> fields, CancellationToken token) =>
        cache.SetAsync(userId, fields, TimeSpan.FromMinutes(5), token);

    // Drop the whole record
    public ValueTask<bool> RemoveAsync(string userId, CancellationToken token) =>
        cache.RemoveAsync(userId, token);
}
```

`SetAsync(key, fields, TimeSpan, token)` uses `HashCacheSetOption.KeyReplace` by default:
the existing Redis key is deleted and re-created atomically inside a `MULTI`/`EXEC`
transaction, so stale fields from a previous write never linger. Use
`HashCacheSetOption.HashReplace` when you want to merge fields into an existing hash instead
of replacing the whole key.

## Subset reads

`GetAsync(key, string[] fields, token)` fetches only the listed fields. One round-trip; the
implementation uses Redis `HMGET`.

```csharp
// One Redis call — returns just these two fields.
var subset = await cache.GetAsync(userId, new[] { "name", "email" }, token);
```

This matters at scale: a 50-field user profile that's read on every request can serve the
2 fields the request needs without paying for the other 48. The `_word_` underscore-wrapped
prefix is reserved for system fields — writes to such names are rejected. The `_metadata_`
system field in particular is filtered out of reads so it doesn't surface as a payload field;
other reserved names are write-protected but may still appear in legacy reads.

## Metadata

`HashCacheEntryOptions` is a record struct with four named fields:

```csharp
public record struct HashCacheEntryOptions(
    DateTimeOffset? ExpireTime = default,
    TimeSpan? TimeToLive = default,
    IDictionary<string, string?>? Metadata = null,
    HashCacheSetOption SetOption = HashCacheSetOption.KeyReplace);
```

The `Metadata` dictionary lets you attach side-channel key/value pairs (always
`string → string?`) alongside the payload fields. The metadata is stored in a reserved
`_metadata_` hash field and is never mixed with your payload — it doesn't appear in
`GetAsync` results.

```csharp
private const string LastSyncTimeKey = "_last_sync";
private const string SourceVersionKey = "_source_version";

public async ValueTask StoreAsync(
    string userId,
    IDictionary<string, string?> fields,
    DateTimeOffset lastSync,
    CancellationToken token)
{
    var metadata = new Dictionary<string, string?>
    {
        [LastSyncTimeKey] = lastSync.ToString("O"),
        [SourceVersionKey] = "v3",
    };
    var options = new HashCacheEntryOptions(
        TimeToLive: TimeSpan.FromMinutes(5),
        Metadata: metadata);
    await cache.SetAsync(userId, fields, options, token);
}

public async ValueTask<bool> IsStaleAsync(string userId, TimeSpan threshold, CancellationToken token)
{
    var metadata = await cache.GetMetadataAsync(userId, token);
    if (metadata?[LastSyncTimeKey] is not { } iso)
    {
        return true;
    }
    return DateTimeOffset.UtcNow - DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) > threshold;
}
```

Prefix metadata keys with `_` to signal that they're side-channel data rather than payload
fields. The library doesn't enforce this convention — it comes from consumer code in the
wild — but it makes human review easier and avoids accidental confusion with payload field
names. Metadata keys are stored under a single reserved hash field on the Redis side, so
there's no physical collision with payload fields regardless of naming. The `_word_` pattern
(leading and trailing underscores, e.g. `_metadata_`) is reserved by the library itself as a
system field; don't use that exact shape for your own metadata keys.

Use cases: record `LastSyncTime` / `SourceVersion` for cache-aside flows where the consumer
needs to decide whether to refresh in the background before the TTL expires. Pair with
[hydrating cache](resilience.md#hydrating-cache) for proactive refresh driven by metadata
staleness rather than absolute TTL.

`GetMetadataAsync` reads back only the metadata dictionary — it does not fetch payload
fields. `SetMetadataAsync` updates metadata in-place on an existing key without touching
payload fields or changing the TTL.

## Bundled GET + TTL

`GetCacheEntryAsync` returns the value and its remaining expiration in a single Redis
transaction (`MULTI`/`EXEC`). One round-trip instead of two.

```csharp
// Before — two round-trips:
var value = await cache.GetAsync(key, token);
var ttl   = await cache.TimeToLiveAsync(key, token);

// After — one transaction:
var entry = await cache.GetCacheEntryAsync(key, token);
// entry.Value        — IDictionary<string, T?>
// entry.Expiration   — DateTimeOffset?
// entry.Found        — bool (false when the key doesn't exist)
```

Any code path that reads a cached value and then immediately checks its expiration — typical
for refresh-ahead and staleness-check logic — was paying double the Redis round-trips on
every hit. For services running cache-aside loops at thousands of ops/sec, collapsing that
pair into one transaction halves the Redis command rate for those paths. The migration is
mechanical: replace `GetAsync` + `TimeToLiveAsync` (or `ExpireTimeAsync`) pairs with
`GetCacheEntryAsync`, then use `entry.Value` and `entry.Expiration`.

**Routing note:** the transaction's `ExecuteAsync` call is issued with
`CommandFlags.PreferReplica`, so reads land on a replica even when wrapped in a
transaction. SE.Redis ignores `PreferReplica` set on individual queued commands when
choosing the server for the transaction, so the flag must be on `ExecuteAsync` itself —
the library handles this correctly, and you don't need to opt in.

## `GetOrAddAsync`

`GetOrAddAsync` is the hash-cache equivalent of the standard cache-aside pattern: check the
cache, call the factory on a miss, write the result back.

```csharp
var profile = await cache.GetOrAddAsync(
    userId,
    async ct =>
    {
        // Called only on a cache miss.
        var user = await _db.GetUserAsync(userId, ct);
        return new Dictionary<string, string?>
        {
            ["name"]  = user.DisplayName,
            ["email"] = user.Email,
            ["plan"]  = user.SubscriptionPlan,
        };
    },
    expiration: TimeSpan.FromMinutes(10),
    token: token);
```

An empty dictionary returned by the factory is not written to the cache unless
`CacheNullValues` is enabled on the provider. The default write mode on a miss is
`HashCacheSetOption.KeyReplace` (the cache key is replaced wholesale). If you need
`HashCacheSetOption.HashReplace` (merge fields into an existing hash) on miss-write,
drop to the non-generic `IHashCache` surface — its `GetOrAddAsync` overload accepts a
`HashCacheSetOption setOption` parameter that the typed `IHashCache<T>` does not.

## Registration

`IHashCache<T>` is registered as a transient by `AddCaching(...)`. Inject it directly:

```csharp
// Program.cs — providers must be registered for the typed wrapper to do real work.
// AddCaching alone registers ICache<T>/IHashCache<T> but no provider, so
// ICacheFactory.CreateHashCache() falls back to NullHashCache (silent no-op).
builder.Services.AddCaching(
    builder.Configuration.GetSection("Caching"),
    b => b.AddRedisConnection().AddBroadcast()
          .AddRedis().AddInMemoryRedis().AddMemory()
          .AddResilienceStrategies().AddCloudEvents());

// Service constructor
public class UserDirectoryService(IHashCache<UserFieldValue> cache) { ... }
```

Under the hood the DI container resolves `HashCache<T>`, which wraps the `IHashCache`
singleton produced by `ICacheFactory.CreateHashCache()`. The cache key is namespaced by
type: `IHashCache<UserFieldValue>` and `IHashCache<OtherType>` share the same Redis
connection but use distinct key prefixes derived from the type name, so there are no
cross-type key collisions.

If you need a named cache (useful when two services want separate TTL policies for the same
`T`), construct `HashCache<T>` directly and pass a `policyName:` argument:

```csharp
services.AddSingleton<IHashCache<UserFieldValue>>(sp =>
    new HashCache<UserFieldValue>(
        sp.GetRequiredService<ICacheFactory>(),
        policyFactory: sp.GetRequiredService<ICachePolicyFactory>(),
        policyName: "UserDirectory"));
```

The `policyName` maps to a `CacheOptions.Policies` entry in `appsettings.json`, letting you
configure per-cache TTL, local expiration, and rehydration options independently of other
caches using the same `T`.

## Field-name constraints

Field names must be non-empty, non-whitespace strings. The `_word_` pattern (word
characters surrounded by underscores on both sides, e.g. `_metadata_`) is reserved for
system use. Writing a field with a reserved name throws `ArgumentException`. Reading a
system field directly via `GetItemAsync` also throws — use `GetMetadataAsync` to read
metadata.

## `RefreshAsync`

`RefreshAsync` updates the TTL on an existing key without touching the stored fields.

```csharp
// Extend the key's TTL to 10 more minutes without rewriting any fields.
await cache.RefreshAsync(userId, TimeSpan.FromMinutes(10), token);
```

The `HashCacheEntryOptions` overload also lets you update metadata at the same time as the
TTL extension, in a single transaction. If the new expiration is in the past, the key is
deleted rather than updated.

## Multilayer behaviour

`MultilayerHashCache` wraps `RedisHashCache` with an in-process memory layer. All
`GetAsync`, `GetItemAsync`, and `GetCacheEntryAsync` calls check local memory first; a miss
falls through to Redis and back-fills local memory. `SetAsync` and `RemoveAsync` publish a
cache-set or cache-removed event over the configured topic so that all nodes in the cluster
invalidate their local copies.

Subset reads (`GetAsync(key, fields[])`) fetch the full hash from Redis on a remote miss,
store the complete record in local memory, and then project out the requested fields from
the in-memory copy on subsequent hits — so a second call for a different subset of fields
doesn't generate another Redis round-trip.

Metadata is propagated through the memory layer: a Redis read that returns metadata
populates `ICacheEntry.Metadata` in the local cache entry, and `GetMetadataAsync` returns
it from memory without a Redis call.
