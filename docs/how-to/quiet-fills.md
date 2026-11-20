# Quiet fills

`SetAsync` announces: it writes the distributed tier, then tells every other node, and each node that holds the key refreshes or drops its copy. `TryAddAsync` announces when it wins. When a value is only being kept warm, or rewritten with the same content, that traffic buys nothing. `TryFillAsync` and `SetIfChangedAsync` write without it.

## `TryFillAsync`: put it there if nothing is

```csharp
var result = await cache.TryFillAsync(key, value, TimeSpan.FromMinutes(5), cancellationToken);

switch (result.Outcome)
{
    case FillOutcome.Added: /* the key was absent and holds value now */ break;
    case FillOutcome.Existed: var current = result.Existing; break;
    case FillOutcome.Unreadable: /* the key holds a value no registered serializer reads */ break;
    case FillOutcome.Failed: /* the store could not answer */ break;
}
```

- Writes only if the key is absent (`SET NX`), announces nothing, and answers the value already there when it is not. It is the write the library makes after a `GetOrAddAsync` miss, with the answer.
- It never joins or waits for a `GetOrAddAsync` generator of the same key running in this process. `NX` cannot overwrite, so it writes anyway.
- `Failed` means the distributed tier is unavailable or timed out. It never throws for a store fault; cancellation throws. The value is then kept locally for the disconnected lifetime (`UseLocalOnlyWhenDisconnected`, `LocalMaxExpirationDisconnected`), as a `GetOrAddAsync` result is.
- `Unreadable` means the key holds a value no registered serializer reads: it is left in place and kept nowhere locally. A deadline that passed before the write answers `Failed` and keeps nothing locally. A write that timed out after it landed can answer `Existed` with the value you passed.
- `FillResult<T>.Expiration` and `ChangeResult<T>.Expiration` carry when the stored entry leaves storage (`DateTimeOffset.MaxValue` for no TTL), or `null` when the cache does not know. `RedisCache` sets it on `Added`, `Existed`, `Filled`, `Unchanged` and `Replaced`; a layered cache reports `null`, as a local-only cache does.
- A local-only cache (`InMemory`) adds the value if absent and answers the local value when present; two concurrent fills of one key get one `Added`.

## `SetIfChangedAsync`: write only what differs

```csharp
var result = await cache.SetIfChangedAsync(key, value, comparer, TimeSpan.FromMinutes(5), cancellationToken);
```

| Outcome | What happened | Event |
| --- | --- | --- |
| `Filled` | The key was absent and was written | none |
| `Unchanged` | The stored value is equal under `comparer`; nothing written, lifetime not refreshed | none |
| `Replaced` | The stored value differed and was replaced | as `SetAsync` |
| `Failed` | The store could not answer | none |

The comparer runs on deserialized values, and never on a null: two nulls are equal, one null is not. A stored value no registered serializer reads is replaced like a different one, so `SetIfChangedAsync` has no `Unreadable` outcome. It may run more than once and must not call the cache. A replacement is written against the exact bytes that were read, so a concurrent write is never overwritten silently: a lost write is read and compared again, three tries in all, then the value is written as `SetAsync` writes it and reported `Replaced`.

## What the local tier keeps

A value these calls read or write, a replacement included, is kept locally only if no local write, removal or peer event for the key came while the call ran, the same rule a read hit follows. A replacement whose keep is refused evicts the local copy and is still announced. A kept value lives for the cache's `LocalMaxExpiration` or the policy's `LocalExpiration`, never past the stored deadline.

## Redis

With `ReadScriptsEnabled` off (the default) a fill is one `SET NX`, plus one atomic read (a transaction on the primary) only when it lost. With it on, one script does both in one call. A replacement uses the rewrite script, or a transaction where scripts are refused. Redis 6.0 is enough. An absolute expiration stays absolute: the time left is worked out again right before each write.

## Implementations outside the library

`ICache` and `ICache<T>` carry these members as abstract, so a decorator forwards them and a test double implements them. An implementation that cannot offer them has two fallbacks:

- Fill: `GetOrAddAsync(key, value, static (v, _) => Task.FromResult(v), expiration)`. It joins a generator in flight and is a write that is not announced only by the library's own caches.
- Change: `GetOrAddAsync`, compare, then `SetAsync` for a different value.

A mock that is not set up answers `default(FillResult<T>)` or `default(ChangeResult<T>)`, whose outcome is `Failed`.

## Limits

- A layered cache keeps an `Existed` or `Unchanged` value locally only if the cache below it reports `Expiration`; one that answers `null` there keeps nothing.
- The first call of either primitive turns on change counting for that cache instance for good, which every local write then pays for: a stripe lock and a counter.
- `ReadSnapshotAsync` on a local-only cache reports the deadline the local tier evicts the entry at. With a custom `EntryFactory`, an entry written by `SetAsync` or `GetOrAddAsync` reports the expiration it was written with, which can be later than the local cap.

See [Quiet fills and the local tier](../concepts.md#quiet-fills).
