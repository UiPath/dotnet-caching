# Choosing a `GetOrAddAsync` overload

`GetOrAddAsync` comes in three shapes: a plain lambda, a delegate you build once, and a static lambda with its state. They
return the same value, coalesce the same way and take the same expiration arguments. They differ only in what the caller
allocates per call, and that difference is small. **Start with the plain lambda.** Reach for the other shapes on a path a
profiler has shown you, not before.

```csharp
public class UserService(ICache<User> cache, IUserRepository repo)
{
    public ValueTask<User?> GetAsync(int id, CancellationToken ct) =>
        cache.GetOrAddAsync(id, c => repo.LoadAsync(id, c), TimeSpan.FromMinutes(5), ct);
}
```

Everything 3.0 did for the hit path — no key string, no entry, no options and no closure allocated *inside the library*
on a local hit — applies to this call as it is. The only allocation left is the lambda the caller writes, and the table
below is what it costs.

## What each shape costs

`GeneratorShapeBenchmark` in `benchmarks/UiPath.Caching.Benchmarks` (in-memory provider, one key, .NET 10, short run;
times are indicative, the allocations are exact):

| Shape | Local hit | Miss (generator runs, answers `null`, nothing stored) |
|---|---|---|
| `ct => Load(ct)` — lambda that captures `this` | 127 ns, **64 B** (the delegate) | 1.06 µs, 384 B |
| `ct => Load(id, ct)` — lambda that captures a local too | 124 ns, **96 B** (closure + delegate) | — |
| `_load` — delegate built once, in the constructor | 119 ns, **0 B** | 1.13 µs, 320 B |
| `this, static (self, ct) => self.Load(ct)` — state is `this` | 134 ns, **0 B** | 0.67 µs, 416 B |
| `(this, id), static (s, ct) => s.Self.Load(s.Id, ct)` — state is a tuple | 257 ns, **0 B** | — |

So the lambda costs 64–96 bytes per call. The delegate built once and the state overloads make a local hit allocate
nothing; on a miss the lambda still costs its bytes (384 B against 320 B for the prebuilt delegate, since the lambda is
created before the lookup), and a miss that stores its value also pays for the write, next to which those bytes are
small. At a thousand calls a second that is under 100 KB/s of short-lived garbage, a rounding error next to the request that
made the call. The tuple-state form saved those bytes but ran slower in this measurement; if you use it, measure it.

## When a shape other than the plain lambda is worth it

Use another shape when all of these hold:

- the call is on the request path of a service that makes it tens of thousands of times a second per node, or a profiler
  shows the generator delegate among the top allocations;
- the call hits far more often than it misses (a miss allocates hundreds of bytes anyway).

Then pick the simplest shape that fits:

1. **The generator needs only `this`.** Build the delegate once and pass the field. It reads as a plain callback and
   allocates nothing:

   ```csharp
   private readonly Func<CancellationToken, Task<Config?>> _load;

   public ConfigService(ICache<Config> cache, IConfigClient client)
   {
       _load = client.LoadAsync;   // instance method group, bound once
       ...
   }

   public ValueTask<Config?> GetAsync(CancellationToken ct) => _cache.GetOrAddAsync(Key, _load, ct);
   ```

2. **The generator needs per-call arguments.** Pass them as the state of a `static` lambda. The lambda cannot capture
   anything, which is what keeps it allocation-free; it receives the state instead:

   ```csharp
   public ValueTask<Token?> GetAsync(Guid machineKey, CancellationToken ct) =>
       _cache.GetOrAddAsync(machineKey, (Self: this, machineKey), static (s, c) => s.Self.FetchAsync(s.machineKey, c), ct);
   ```

   This is the shape `HybridCache.GetOrCreateAsync<TState, T>`, `string.Create` and `ConcurrentDictionary.GetOrAdd`
   use for the same reason. It is the most verbose of the three; that verbosity is the price of the zero, so pay it
   only where the zero matters.

The state overloads are members of `ICache`, `ICache<T>`, `IHashCache` and `IHashCache<T>`. The `CacheKey` ones are
members every implementation provides, and an interpolated key goes to `ISpanKeyCache` (`ISpanKeyHashCache`) or else to
the `CacheKey` state member, so no library cache binds a closure. `GetOrAddWithExpirationAsync` has a state overload of
the same shape, also a member of `ICache` and `ICache<T>`, and both take an interpolated key (`ISpanKeyCache`): a warm hit
allocates nothing and a miss reads the local tier once.

The batch `GetOrAddAsync` already carries a state per entry, but its generator usually needs the service too. The
overload that takes an `argument` passes one value for the whole call, before the states of the keys that missed, so
the generator can be static:

```csharp
var users = await _cache.GetOrAddAsync(entries, this, static (self, ids, ct) => self.LoadManyAsync(ids, ct), ct);
```

The argument is not a state: every entry keeps its own, and the generator still sees only the states that missed. A
batch allocates its arrays on every call anyway, so the argument saves the closure and no more; on the library's caches
a call whose keys all hit binds nothing.

## The UIPCACHE001 suggestion

`UiPath.Caching.Abstractions` (and so every package built on it) ships an analyzer that points at the allocation above.
Rule `UIPCACHE001` reports a generator that allocates on every call, hits included, passed to `GetOrAddAsync` or
`GetOrAddWithExpirationAsync` on `ICache`, `ICache<T>`, `IHashCache` or `IHashCache<T>` (or on a class that implements
one), single-key or batch, where a state (or, for a batch, argument) overload exists for the same call:

- a lambda that captures `this`, a local or a parameter, itself or through a local function it calls; the message
  names what it captures — *The generator captures 'this' and 'id' and allocates a closure on every call, hits
  included; pass them as state and make the lambda static*;
- an instance method group (`LoadAsync`, `_client.LoadAsync`), which allocates a delegate each time;
- a capturing lambda passed to a state or argument overload, which should be `static`.

It stays quiet for a `static` lambda, a lambda that captures nothing (the compiler caches it) or names a variable only
in `nameof`, a static method group or local function, a delegate you built once, and a call with no state overload.
Only the library's own extension classes count as state overloads: an extension of your own, even in the `UiPath.Caching`
namespace, is not looked at.

The rule is **Info**: the IDE shows a suggestion and the build prints nothing, because the plain lambda stays the
default. On a hot path where the bytes matter, raise it in `.editorconfig`, for the whole project or for one folder:

```ini
[src/Hot/**.cs]
dotnet_diagnostic.UIPCACHE001.severity = warning
```

or silence it with `dotnet_diagnostic.UIPCACHE001.severity = none`. The rule has a category of its own, `UiPath.Caching`, so
raising a whole category such as `Performance` does not turn it on.

The code fix, *Pass captured values as state*, moves the call to the state overload and keeps the other arguments
(key, expiration, policy, token) where they are:

```csharp
// One capture is the state itself; `this` becomes `self`, and an implicit member access goes through it.
_cache.GetOrAddAsync(key, ct => LoadAsync(ct), ct);
_cache.GetOrAddAsync(key, this, static (self, ct) => self.LoadAsync(ct), ct);

// Several captures become a named tuple.
_cache.GetOrAddAsync(id, c => _repo.LoadAsync(id, c), TimeSpan.FromMinutes(5), ct);
_cache.GetOrAddAsync(id, (Self: this, Id: id), static (s, c) => s.Self._repo.LoadAsync(s.Id, c), TimeSpan.FromMinutes(5), ct);

// An instance method group passes its target.
_cache.GetOrAddAsync(key, _client.LoadAsync, ct);
_cache.GetOrAddAsync(key, _client, static (client, ct) => client.LoadAsync(ct), ct);

// A batch passes the capture as its argument, ahead of the missing states.
_cache.GetOrAddAsync(entries, (ids, ct) => LoadManyAsync(ids, ct), ct);
_cache.GetOrAddAsync(entries, this, static (self, ids, ct) => self.LoadManyAsync(ids, ct), ct);

// Spelled type arguments get the state's type at the end.
_hash.GetOrAddAsync<User>(key, ct => LoadAsync(id, ct), null, ct);
_hash.GetOrAddAsync<User, (UserService Self, int Id)>(key, (Self: this, Id: id), static (s, ct) => s.Self.LoadAsync(s.Id, ct), null, ct);
```

The state is read once, when the call is made, where the lambda read the variable when it ran, so the fix is offered
only when that cannot differ. It is not offered when a captured variable is assigned anywhere in the method (or a
captured primary constructor parameter anywhere in the type), when the lambda uses `base`, calls a local function that
captures, or names a captured local in `nameof` or an inferred tuple or anonymous-type member, for a lambda already
on a state overload, and for a call on a class whose state overload is only on the cache interface (a cache class of
your own; the library's public caches declare theirs); the suggestion still shows, and the rewrite is yours. Fix All applies it to a document, project or solution (`dotnet format
analyzers --diagnostics UIPCACHE001 --severity info` from the command line); a generator inside another generator is
fixed on the next pass, its outer one first.

## A generator that knows its value's lifetime

When the lifetime is in the value (a token with an expiry), return it from the generator instead of computing it before
the call:

```csharp
var token = await _cache.GetOrAddWithExpirationAsync(key, async c =>
{
    var fresh = await _client.FetchAsync(c);
    return new GeneratedValue<Token?>(fresh, fresh.ExpiresAt - Skew);
}, ct);
```

The entry is stored until that instant; an instant already passed returns the value without storing it. The method has
its own name because a `GetOrAddAsync` overload taking `Func<CancellationToken, Task<GeneratedValue<T>>>` would make
every generator that only throws or returns `default` — `_ => throw new InvalidOperationException()`, common in tests —
ambiguous against the closure overload.

## Keys

- **A constant key** is built once: `private static readonly CacheKey Key = "default";`. A constant string passed on
  every call is normalized (trimmed, lowercased) on every call.
- **A formatted key** is an interpolated string: `$"{tenantId}:{machineKey}"`. On the hot paths above it is written to a
  256-character stack buffer and a local hit allocates nothing (values format in the current culture, so a `DateTime` or `decimal` hole differs between nodes with different cultures):

  ```csharp
  var token = await _cache.GetOrAddAsync($"{tenantId}:{machineKey}", (Self: this, machineKey), static (s, c) => s.Self.FetchAsync(s.machineKey, c), ct);
  ```

  A longer key rents its buffer from `ArrayPool`. `$"..."` on a variable typed as a concrete class (`Cache<T>`, `HashCache<T>`, `MultilayerCache`) goes through `string` and allocates, so type the variable as the interface. Under C# 12 or older a constant interpolated key (`$"fixed"`) is ambiguous (CS0121); pass a plain string. `SetAsync` takes interpolated keys too; a write has to build the
  string the entry is stored under, so it saves your own key string, not the allocation.

## What runs, and what a test sees

Every shape ends in the same place: `ICache<T>` is `Cache<T>`, which composes the key with its `ICacheKeyStrategy` and
calls the provider (`InMemory`, `InMemoryRedis` or `Redis`). The `CacheKey` state `GetOrAddAsync` and
`GetOrAddWithExpirationAsync`, and the batch `GetOrAddAsync` with an argument, are members every cache implements. The
interpolated-key members are `sealed` interface members with a body: each looks for the matching capability interface
(`ISpanKeyCache`, and for the hash caches `ISpanKeyHashCache`) and otherwise calls the `CacheKey` member. `Cache<T>`,
`HashCache<T>` and the library's in-memory and multilayer caches implement them; the Redis-only `ICache` implements none
and takes the fallback, where the Redis round trip allocates far more than the key string does.

A Moq or NSubstitute proxy implements no capability interface, so every shape arrives at the proxy's own members. Set
up these, and a strict mock answers the production shape:

| Call on the proxy | Members to set up |
|---|---|
| Interpolated key on any of the four interfaces | The same member with a `CacheKey` (the formatted text) |
| State `GetOrAddAsync` on any of the four interfaces, or the `CacheSyncExtensions` and `HashCacheSyncExtensions` `GetOrAdd` | The same state member with a `CacheKey` (and a `CachePolicy?` on `ICache` and `IHashCache`, which the policy-less call passes as `null`) |
| `GetOrAddWithExpirationAsync`, with or without state, on `ICache` or `ICache<T>` | The same member with a `CacheKey` (and a `CachePolicy?` on `ICache`, which the policy-less call passes as `null`) |
| Batch `GetOrAddAsync` with an argument, or the multi-key `CacheSyncExtensions` `GetOrAdd` | The same argument member (and a `CachePolicy?` on `ICache`); the sync `GetOrAdd` passes its generator as the argument |

On a loose proxy that was not set up, `GetOrAddAsync` and `GetOrAddWithExpirationAsync` in every shape, batch included,
return the default without running the generator. To test what the library does — coalescing, the generator's
expiration — use the in-memory provider rather than a proxy.

A sealed member is not virtual, so no proxy can intercept it: its body runs on the proxy and reaches the setups above.
That is why they are sealed. Castle DynamicProxy, which Moq, NSubstitute and FakeItEasy build on, emits invalid IL for an
overridable method that takes a `ref struct`, and an overridable default interface member is intercepted (Moq,
NSubstitute) or throws (NSubstitute on Castle.Core 5.2.1) rather than running its body. The capability interfaces stay
separate for the same reason.

## Wrapping the cache

If your service wraps `ICache<T>` in a helper of its own (to map an application entry type, say), wrap the plain-lambda
shape and let the library's member handle the capability check:

```csharp
public static ValueTask<T?> GetOrAddAsync<T>(this ICache<T> cache, CacheKey key, Func<CancellationToken, ValueTask<AppEntry<T>>> fetch, CancellationToken ct = default) =>
    cache.GetOrAddWithExpirationAsync(key, async c => ToGeneratedValue(await fetch(c)), ct);
```

A wrapper that takes state or reads before it calls `GetOrAdd` is re-implementing the member it calls.
