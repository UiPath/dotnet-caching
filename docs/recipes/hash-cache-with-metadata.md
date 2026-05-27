# Hash cache with side-channel metadata

**What:** Store payload fields and freshness/version metadata side-by-side in one `IHashCache<T>` entry via `HashCacheEntryOptions`. The metadata never collides with payload fields and isn't returned by default reads.

**When to use:**
- Cache-aside flows that need to know how fresh the cached value is before deciding whether to refresh.
- When you want to record the source version, last-sync timestamp, or origin of a cached value.
- When the payload type can't carry the metadata (e.g. it's a DTO with a fixed schema you don't control).

## Code

```csharp
using UiPath.Platform.Caching;

public class UserDirectoryCache(IHashCache<UserField> cache)
{
    private const string LastSyncTimeKey = "_last_sync";
    private const string SourceVersionKey = "_source_version";

    public async ValueTask StoreAsync(
        string userId,
        IDictionary<string, UserField?> fields,
        DateTimeOffset lastSync,
        CancellationToken token)
    {
        var metadata = new Dictionary<string, string?>
        {
            [LastSyncTimeKey] = lastSync.ToString("O"),
            [SourceVersionKey] = "v3",
        };
        await cache.SetAsync(
            userId,
            fields,
            new HashCacheEntryOptions(Metadata: metadata),
            token);
    }

    public async ValueTask<bool> IsStaleAsync(
        string userId,
        TimeSpan threshold,
        CancellationToken token)
    {
        var metadata = await cache.GetMetadataAsync(userId, token);
        if (metadata?[LastSyncTimeKey] is not { } iso) return true;
        return DateTimeOffset.UtcNow - DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) > threshold;
    }
}
```

## Notes

`HashCacheEntryOptions` is a `record struct` with named parameters — `Metadata` is the only field you need to set when you don't want to override expiration. The other fields (`ExpireTime`, `TimeToLive`, `SetOption`) default to provider-policy defaults.

The convention of prefixing metadata keys with `_` (e.g. `_last_sync`) makes them recognizable as side-channel data in logs and debuggers. The library doesn't enforce this — it's a convention from production code.

`cache.GetAsync(userId, token)` returns just the payload fields, not the metadata. You explicitly call `cache.GetMetadataAsync(userId, token)` to fetch metadata. These are separate Redis round-trips. If you need both atomically, call them in parallel and `await` both — there's no transactional `GetAsync + GetMetadataAsync`, but the cache entry's TTL means there's a single window during which both reads see the same generation.

Note that `IHashCache<T>.GetMetadataAsync` and `SetMetadataAsync` are typed-wrapper methods — they delegate to the underlying `IHashCache` (non-generic), which applies the key strategy before the Redis call.

## When not to use

- Payloads where the freshness info is a real payload field — model it as one and skip metadata.
- Cases where you only ever read metadata together with the full payload — the round-trip-per-read cost adds up; consider embedding the metadata into one of the payload fields instead.
- Caches with very tight memory budgets — every metadata field is a separate Redis hash field with its own overhead.

## See also

- [how-to/hash-cache.md](../how-to/hash-cache.md)
- [how-to/resilience.md](../how-to/resilience.md)
- [reference/interfaces.md](../reference/interfaces.md)
