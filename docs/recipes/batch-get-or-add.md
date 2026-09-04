# Fetch many entries with one database round trip

**What:** Use the multi-key `GetOrAddAsync(entries, generator, …)` overload instead of looping over
single-key `GetOrAddAsync`. Each key is paired with an opaque state — a database id, a request object,
whatever names the entry in your own vocabulary — and the generator receives only the states of the
entries that missed the cache, never the keys. N misses cost one source round trip instead of N, and no
call site parses a cache key to recover an id.

**When to use:**
- You already have a batch of ids/keys up front (a page of results, a list from a request body) and
  would otherwise call single-key `GetOrAddAsync` in a loop.
- The generator talks to something that supports a multi-get shape — a SQL `WHERE id IN (...)`, a
  bulk HTTP endpoint, a batched gRPC call.
- You want stampede protection across an exactly-repeated batch — one lock for the whole miss set
  rather than one per key (see Notes for the limits).

## Code

```csharp
using UiPath.Caching;

public class UserCache(ICache cache, IUserRepository db)
{
    public async Task<IReadOnlyDictionary<long, User?>> GetUsersAsync(
        IReadOnlyCollection<long> ids, CancellationToken token)
    {
        var entries = ids
            .Select(id => new KeyValuePair<CacheKey, long>((CacheKey)$"user:{id}", id))
            .ToArray();

        var results = await cache.GetOrAddAsync<User, long>(
            entries,
            async (missingIds, ct) =>
            {
                // Only the ids that missed every cache layer reach this point — as ids, not keys.
                var rows = await db.GetUsersAsync(missingIds, ct);
                return rows.Select(r => new KeyValuePair<long, User?>(r.Id, r)).ToArray();
            },
            policy: null,
            token: token);

        // results holds every distinct requested id exactly once, in request order.
        return results.ToDictionary(r => r.Key, r => r.Value);
    }
}
```

Before this overload existed, the same call site looped:

```csharp
var users = new List<User?>();
foreach (var id in ids)
{
    users.Add(await cache.GetOrAddAsync<User>((CacheKey)$"user:{id}", ct => db.GetUserAsync(id, ct), token: token));
}
```

Ten uncached ids there means ten `SELECT`s; the batch version issues one, however many ids miss.

## Keys you already own

If your cache keys already are your identity — there's no separate id to carry — pair each key with
itself, so `TState` is `CacheKey`:

```csharp
var entries = ids
    .Select(id => (CacheKey)$"user:{id}")
    .Select(k => new KeyValuePair<CacheKey, CacheKey>(k, k))
    .ToArray();

var results = await cache.GetOrAddAsync<User, CacheKey>(
    entries,
    async (missingKeys, ct) =>
    {
        var rows = await db.GetUsersByKeyAsync(missingKeys, ct);
        return rows.Select(r => new KeyValuePair<CacheKey, User?>(r.Key, r.Value)).ToArray();
    },
    token: token);
```

The generator then receives `CacheKey[]` and returns pairs keyed by `CacheKey`, which is the shape you
would write by hand anyway. The synchronous `GetOrAdd` facade on `ICache<T>` is the one place that
takes `CacheKey[]` directly and does this pairing for you — a caller reaching for a blocking call
*and* per-key state is rare enough to use the async API instead.

## Notes

The generator signature is `Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>>` — it
receives the states of the entries that missed and returns a pair per state it could resolve.
Parameter order on the call is `entries, generator, [expiration], policy, token`, and only `token` is
optional — the same shape as the single-key `GetOrAddAsync`. Callers that do not want to pass a
`policy` use the `CacheExtensions` overloads instead. On `ICache<T>` there is no `policy` parameter at
all, since `ICache<T>` resolves one at construction.

- **Return a pair for every state you can resolve.** A state you omit comes back as `default(T)` (i.e.
  `null` for a reference type) and is **not** cached, so the next call retries the source. That is
  correct for "no such row," but means a permanently-missing state is re-queried every time. To
  remember a genuine absence, return an explicit `null` value for that state and enable
  `CacheNullValues` — the write still happens, it just stores `null`.
- **Do not return states you were not asked for.** They are silently ignored.
- **Two states sharing one key is supported.** Cache operations de-duplicate by `CacheKey` (one probe,
  one write per distinct key); results de-duplicate by state (one entry per distinct requested state,
  in first-occurrence order). When two entries carry different states but the same key, the generator
  is asked once — about the first state seen for that key — and the value it returns is reported under
  both states.
- **One state under two different keys is caller error.** The first occurrence wins; the second entry
  is dropped entirely — never probed, never written, never passed to the generator. Keep states unique
  per call, or deliberately collapse them onto one key as described above.
- **`TState` must have the equality you actually intend.** It's used as a dictionary key internally.
  `where TState : notnull` is enforced by the compiler; whether `Equals`/`GetHashCode` mean what you
  intend is not — a reference-equality type used where value equality was meant silently produces
  duplicate result entries.
- **Stampede protection is per exact miss set, not per key.** On `MultilayerCache`, the generator
  call is guarded by one composite lock derived from the set of keys that missed. Two concurrent
  batch calls with the *same* miss set share one generator invocation; if the miss sets differ by
  even one key, the two calls take different locks and both invoke the generator. A batch whose
  only miss is a single key `k` locks on `k` itself, so it serializes correctly with a concurrent
  single-key `GetOrAddAsync(k, …)` call. This is a real limitation, not just a fine print — don't
  assume batch calls with overlapping-but-not-identical key sets deduplicate against each other.
  Note this applies to the *fill* path only: **rehydration locks per key**, so two nodes whose aging
  sets merely overlap still refresh each shared key exactly once.
- **Your generator must be safe to call concurrently with itself.** On `MultilayerCache` with
  rehydration enabled, one `GetOrAddAsync` call can invoke it twice at the same time — once on the
  calling path for the states of the missing keys, and once on a background task refreshing the hit
  keys that are past their rehydrate threshold — over disjoint key sets.

## When not to use

- You have one key at a time, not a batch — the single-key `GetOrAddAsync` overload is simpler and
  there is nothing to gain.
- Your data source has no efficient multi-get — if the "batch" generator would just loop over
  single-item fetches internally, you've moved the loop without removing the round trips.
- You need per-key stampede protection when miss sets vary across concurrent callers — the composite
  lock only coalesces identical miss sets (see Notes).

## See also

- [concepts.md#batch-get-or-add](../concepts.md#batch-get-or-add)
- [reference/interfaces.md](../reference/interfaces.md)
- [how-to/resilience.md#stampede-protection](../how-to/resilience.md#stampede-protection)
