# Claim a key exactly once with `TryAddAsync`

**What:** Use `TryAddAsync` when you need "only one caller may proceed", keyed by something. It writes
the key only if it is absent and returns `true` only to the caller that created it. On a Redis-backed
cache that is StackExchange.Redis `When.NotExists` — `SET key value EX … NX` — one atomic round-trip,
so exactly one caller across every node wins.

**When to use:**
- **At-most-once side effects,** where skipping is the safe failure. "Send this alert / this welcome
  email once per user per day."
- **Electing a worker.** Which of N replicas runs a periodic job this tick.
- **Dedup markers** where the marker's TTL *is* the dedup window.
- **Suppressing duplicate work,** as an optimization ahead of an operation that is idempotent anyway.

**When not to use:** as the sole guard on an operation that must eventually happen. The claim marker
records that someone started, not that anyone finished, so a winner that dies mid-flight leaves the
work undone until the TTL lapses — and `false` cannot tell you which of "already claimed" or "write
failed" you are looking at. Those cases need a recorded outcome, not a claim.

**When not to use:** if you need a lock you can release early, or a fencing token that proves you
still hold it, use [`IDistributedLock`](../reference/interfaces.md#idistributedlock) instead. See
[Notes](#notes).

## Code

```csharp
using UiPath.Caching;

public class DailyDigest(ICache cache, IMailer mailer)
{
    // The key is the dedup unit: one send per user per UTC calendar day. The TTL only
    // cleans the marker up afterwards — it is not a rolling 24-hour window, so two
    // triggers minutes apart either side of midnight use different keys and both send.
    private static readonly TimeSpan MarkerTtl = TimeSpan.FromHours(24);

    public async Task SendOnceAsync(string userId, CancellationToken token)
    {
        var claimed = await cache.TryAddAsync(
            (CacheKey)$"digest:{userId}:{DateTime.UtcNow:yyyyMMdd}",
            DateTimeOffset.UtcNow,
            MarkerTtl,
            token: token);

        if (!claimed)
        {
            // Someone else claimed it, or the write could not be completed. Both mean
            // "do not send" — which is the whole reason this side effect fits the
            // primitive: skipping is the safe failure, so the ambiguity costs nothing.
            return;
        }

        await mailer.SendDigestAsync(userId, token);
    }
}
```

`TryAddAsync` fits here because **not** sending is the safe failure. Note what it is *not* doing:
nothing recovers the missed digest if the winner crashes between the claim and the send, and nothing
tells a lost race apart from a failed write. If your side effect must eventually happen — capturing a
payment, say — a claim marker is the wrong shape and no amount of branching on `false` fixes it: the
marker says "someone is handling this", never "this was handled". Record the *outcome* instead
(`Pending` → `Captured` on a durable store, or an idempotency key the downstream provider itself
honors) and let redelivery retry until the outcome is written.

The typed surface is the same shape without the `policy` parameter:

```csharp
public class JobElection(ICache<string> cache)
{
    public Task<bool> TryClaimTickAsync(string jobName, DateTimeOffset tick, CancellationToken token) =>
        cache.TryAddAsync(
                (CacheKey)$"{jobName}:{tick:yyyyMMddHHmm}",
                Environment.MachineName,
                TimeSpan.FromMinutes(5),
                token)
            .AsTask();
}
```

## Why not check-then-set

The obvious hand-rolled version is not equivalent:

```csharp
// BROKEN: two callers can both observe "absent" before either writes.
if (!await cache.ContainsAsync<DateTimeOffset>(key, token))
{
    await cache.SetAsync(key, DateTimeOffset.UtcNow, MarkerTtl, token: token);
    await payments.CaptureAsync(eventId, token); // runs twice under concurrency
}
```

The gap between the probe and the write is the whole problem, and no amount of narrowing closes it —
that is exactly the gap `NX` removes by making the decision part of the write. This is also why
`TryAddAsync` is a **required** member of `ICache` and `ICache<T>` rather than a default interface
method, precisely so nothing can inherit the code above: a non-atomic emulation would void the only
guarantee the method makes, and a fallback that quietly reported one answer for every store would
hide which stores can actually arbitrate. An implementation says what its own store can do.

## Notes

- **`false` does not mean "the key existed".** It means "you did not create it" — the key already
  existed, *or* the write could not be completed (store disconnected, write threw, or the value was a
  `null`/`default` the cache cannot represent). This is fail-closed on purpose: nobody is ever wrongly
  told they won. Design the `false` branch so that "skip the side effect" is the safe outcome —
  nothing recovers the distinction. `IConnectionState.IsConnected` does not: a serialization or
  command failure returns `false` with the connection snapshot still healthy, and the snapshot can
  change either side of the call anyway. `IDistributedLock.TryAcquireAsync` does not either; it
  documents backend-unavailable and already-held as the same `null`. If the two readings need
  different handling, you need a primitive with a richer result than a `bool`.
- **A non-positive TTL is a bad argument, not a loss.** `expiration` is non-nullable, so a duration
  that is not strictly positive — or a deadline already past — raises `ArgumentOutOfRangeException`
  and nothing is written. Returning `false` would be indistinguishable from "somebody else holds the
  key", which is exactly the confusion the ambiguity above asks you to design around. To inherit the
  policy's TTL, call the overload that has no `expiration` parameter.
- **Caching switched off means everyone wins.** `NullCache.TryAddAsync` returns `true` — it retains
  nothing, so no key pre-exists and nobody loses — and it is what `ICacheFactory.CreateCache` falls
  back to when the requested provider is missing or has `Enabled=false`. A mistyped provider name
  therefore turns at-most-once into at-least-once with no error, so assert the provider you expect at
  startup: `if (cacheFactory.CreateCache(KnownCacheProviderNames.Redis) is NullCache) throw …`.
- **The `NX` write is retryable,** so it stays on the shared `Write` resilience pipeline. Retries
  fire on exceptions only, and the ambiguous case costs nothing: if the write lands but its reply is
  lost, the retry is refused by the key it just wrote and reports `false` — the same answer the
  un-retried exception would have produced — while a first attempt that never reached Redis is
  recovered as the `true` it should have been. (`SPOP` in `UiPath.Caching.Queue` is the opposite: a
  retry there pops a second item and loses the first, which is why it has its own opt-in pipeline.)
- **It never deletes.** `SetAsync` handed a `null` with `CacheNullValues` off *removes* the key;
  `TryAddAsync` returns `false` and leaves it alone. With `CacheNullValues` on, a `null` claims the key
  through the cached-null sentinel — so prefer a meaningful value (a timestamp, the machine name) that
  makes the claim diagnosable in `redis-cli`.
- **The TTL is applied by the same command,** so a won key is never briefly immortal between the write
  and a follow-up `EXPIRE`. Give every claim a TTL you are willing to wait out: there is no release,
  so a crashed winner blocks the key until it expires. That is the main reason to prefer
  `IDistributedLock` for long critical sections.
- **It is not a lock.** No ownership token, no early release, and a later `SetAsync`/`RemoveAsync` on
  the key silently overwrites the claim. If a bug elsewhere writes that key, exclusion is gone with no
  error. On the `InMemory` provider the reverse also holds: the local lock serializes conditional adds
  against each other, but `SetAsync` takes no lock, so a set landing between the probe and the write
  is overwritten by the claim, which still reports `true`. Redis has no such gap — `NX` is atomic
  against a concurrent `SET`.
- **In-memory-only caches exclude in-process only.** With the `InMemory` provider there is no shared
  store to arbitrate, so the local tier does — serialized by the local lock, which a conditional add
  takes whatever `Lock.LocalLockEnabled` says, because here it *is* the guarantee rather than a
  single-flight optimization. A caller that cannot acquire it within `Lock.LocalLockTimeout` is told
  it lost. Two processes still both win. `InMemoryRedis` and `Redis` are cross-node correct.
- **Both tiers contribute, in one sequence:** the local tier is probed under the local lock, then
  the L2 decides, then L1 is populated on a win. The probe bounds exclusion at one winner per
  process — the only thing doing so when the L2 retains nothing — and reports a local hit as a loss
  without asking the L2, which is fail-closed but costs a win the L2 would have granted if the local
  copy outlived the shared one. When the L2 is disconnected the call returns `false` rather than
  granting a local claim every node would also be granted, unlike `SetAsync`, which degrades to a
  local-only write there.
- **There is no multi-key overload.** Redis has no atomic multi-key `NX`, and choosing all-or-nothing
  versus per-key semantics on your behalf would be a guess. Claim keys one at a time, or take a single
  claim on a key that stands for the whole batch.

**See also:** [`ICache`](../reference/interfaces.md#icache),
[`IDistributedLock`](../reference/interfaces.md#idistributedlock),
[Concepts — locking](../concepts.md)
