# Upgrading from 1.x to 2.0

2.0 changes source, tests and configuration. It does not change a stored byte: the default
serializer writes what 1.x wrote, so mixed-version pods during a rollout read each other's entries
and a rollback is a package revert. The one exception is where the key is rendered, not the value:
a deployment running `ShardKeyEnabled: true` with braces in its cache keys addresses different Redis
keys on 1.3 and 2.0, see [Configuration keys](#configuration-keys).

Work through the sections in order. Each says how the break shows itself, because several of them
do not show at compile time.

| Section | Shows itself as |
|---|---|
| [Packages and floors](#packages-and-floors) | `NU1605` / `NU1608` on restore |
| [Configuration keys](#configuration-keys) | `InvalidOperationException` at startup (silent in 2.0.0-preview.2 and earlier) |
| [Serializer registration](#serializer-registration) | `InvalidOperationException` at startup |
| [Per-call expiration](#per-call-expiration) | Compile error, then `ArgumentOutOfRangeException` for a zero or past value |
| [Tests that mock ICache](#tests-that-mock-icache) | `NotSupportedException` when the test runs |
| [Hand-written implementations](#hand-written-implementations) | Compile error |
| [Clock](#clock) | Compile error, or a test clock that stops being honored |
| [Renamed types](#renamed-types) | Compile error |
| [Integrations built against 1.x](#integrations-built-against-1x) | No compile or restore signal; `MissingMethodException` / `TypeLoadException` at the first call to a removed member |

## Packages and floors

Move every `UiPath.Caching.*` reference to the same 2.0 version. The floors a consumer's own
references must clear, from `Directory.Packages.props`:

| Package | 1.3.0 | 2.0.0 |
|---|---|---|
| `StackExchange.Redis` | 3.1.13 | 3.1.31 |
| `Microsoft.Extensions.*` on `net10.0` | 10.0.10 | 10.0.12 |
| `Microsoft.Extensions.Logging.Abstractions` on either TFM | 10.0.11 | 10.0.12 |

A project that pins any of these lower than the floor fails restore with `NU1605`. Raise the pin;
the library declares the floor, not a ceiling. `OpenTelemetry.Instrumentation.StackExchangeRedis` is
pinned in this repository for its own sample and tests, and is not a floor on consumers: any version
that does not cap StackExchange.Redis below 3.x works.

## Configuration keys

Four options keys were `[Obsolete]` in 1.x and are gone in 2.0: three multilayer rename aliases
that forwarded to the `Local*` properties, and `ThreadPoolSocketManager`, a no-op since
StackExchange.Redis 3.0 with nothing to forward to. The configuration binder ignores a key it cannot
place, so a leftover key does not fail the build or write a log line; the value falls back to its
default. For `PrimaryMaxExpiration` that means the L1 cap disappears.

| Section | Remove | Use |
|---|---|---|
| `Caching:InMemoryRedis`, `Caching:InMemory` | `PrimaryMaxExpiration` | `LocalMaxExpiration` |
| `Caching:InMemoryRedis`, `Caching:InMemory` | `PrimaryMaxExpirationDisconnected` | `LocalMaxExpirationDisconnected` |
| `Caching:InMemoryRedis`, `Caching:InMemory` | `UsePrimaryOnlyWhenDisconnected` | `UseLocalOnlyWhenDisconnected` |
| `Caching:Connections:Redis` | `ThreadPoolSocketManager` | nothing; it has been a no-op since StackExchange.Redis 3.0 |

From 2.0.0 final, the section-bound registrations refuse a section that still carries one of these
and name the key and its replacement in the exception. That covers `appsettings.json`, environment
variables and any other provider bound through `AddMemory()`, `AddInMemoryRedis()` and
`AddRedisConnection()`. Keys that were never read by any options class are not detected: check the
section against [settings.md](reference/settings.md) while you are in the file.

```diff
 "InMemoryRedis": {
-  "PrimaryMaxExpiration": "01:00:00"
+  "LocalMaxExpiration":   "01:00:00"
 }
```

`CacheOptions.ShardKeyEnabled` is deprecated, not removed. Leave it as it is: flipping it relocates
every brace-free key. If it is `true` *and* your keys contain braces, 2.0 renders them differently:
1.3 wrapped the whole key in a new `{...}`, 2.0 keeps a valid caller-supplied `{tag}` as the tag and
refuses a key whose braces form no valid tag. Those entries live under a different Redis key on each
version, so a mixed rollout misses across versions and a rollback re-misses; a brace-free key, or
`ShardKeyEnabled: false`, renders identically on both.

## Serializer registration

`ISerializerProxy<RedisValue>` is gone. Every cache serializes through `ISerializerProxy<byte[]>`,
and the default `SystemJsonByteSerializerProxy` writes the same bytes `SystemJsonSerializerProxy`
wrote, so no entry moves.

If you register your own serializer, change the interface and the registration:

```diff
-public class SerializerProxy : ISerializerProxy<RedisValue>
+public class SerializerProxy : IMemorySerializerProxy
 {
-    public T? Deserialize<T>(RedisValue value) => ...;
-    public RedisValue Serialize(object? value) => ...;
+    public T? Deserialize<T>(byte[]? value) => ...;
+    public byte[]? Serialize(object? value) => ...;
+    public ReadOnlyMemory<byte> SerializeToMemory<T>(T? value) => ...;
 }

-services.AddSingleton<ISerializerProxy<RedisValue>, SerializerProxy>();
+services.AddSingleton<ISerializerProxy<byte[]>, SerializerProxy>();
```

Implement `IMemorySerializerProxy` rather than the plain `ISerializerProxy<byte[]>` when your
serializer already produces a `Memory<byte>`. `RedisCache` and `RedisHashCache` test for that
interface and call `SerializeToMemory` when it is there; with the plain interface every write first
copies the payload into an array. The memory is borrowed and is only read until the write completes,
so a pooled buffer is safe. Details in
[extending.md](how-to/extending.md#lending-memory-imemoryserializerproxy).

A leftover `ISerializerProxy<RedisValue>` registration that is already in the container when
`AddCaching` runs fails it rather than being ignored, whether or not caching is enabled in that
profile. The scan happens once, inside `AddCaching`, so a legacy registration added after it is not
seen and stays silently unused: replace every one, and register the serializer before `AddCaching`. `RawByteSerializerProxy` is also
available and is a wire-format change; do not switch to it during the upgrade.

## Per-call expiration

The `expiration` parameter is `TimeSpan` / `DateTimeOffset`, not nullable. Passing `null` meant
the same as not passing it, and made `SetAsync(key, value, null)` ambiguous between the two nullable
overloads. A caller that forwards its own optional value now branches:

```diff
-return await cache.SetAsync(key, value, options?.AbsoluteExpirationRelativeToNow, token);
+var expiration = options?.AbsoluteExpirationRelativeToNow;
+return expiration.HasValue
+    ? await cache.SetAsync(key, value, expiration.Value, token)
+    : await cache.SetAsync(key, value, token);
```

The overload without an expiration resolves `CachePolicy.DistributedExpiration`, then the
provider's `DefaultExpiration`, then a one-hour floor. That floor is new: in 1.x a provider whose
`DefaultExpiration` was `null` wrote entries with no TTL. To keep an entry until it is removed, name
`TimeSpan.MaxValue` explicitly.

A value that is passed is enforced by every real provider. A `TimeSpan` that is zero or negative,
or a `DateTimeOffset` already in the past, throws `ArgumentOutOfRangeException` and writes nothing.
In 1.x the same value meant "no expiration". `NullCache`, `NullHashCache` and `NullSetCache` read no
argument and enforce nothing, so a service running with caching disabled, or resolving a provider
that is absent, will not see the throw in a test environment and meet it first in production. Search
your configuration for durations of `0` or `00:00:00` that feed a per-call expiration; a catch-all
around the write turns the throw into a skipped write and a log line, which is easy to miss.

## Tests that mock ICache

The short forms, `GetAsync<T>(key, token)`, `SetAsync(key, value, expiration, token)` and the
rest, moved off the interface onto `CacheExtensions`, `HashCacheExtensions` and
`SetCacheExtensions`. Production code compiles unchanged. A Moq setup does not: Moq reads the
`Setup` or `Verify` expression and refuses a call to a static method, so a short form inside one
throws `NotSupportedException` when the test runs, not when it builds.

Re-target the setup at the member the extension forwards to. The extension passes `null` in the new
`policy` slot, so match it loosely:

```diff
-_cache.Setup(x => x.GetAsync<string>(key, It.IsAny<CancellationToken>()))
+_cache.Setup(x => x.GetAsync<string>((CacheKey)key, It.IsAny<CachePolicy>(), It.IsAny<CancellationToken>()))
     .ReturnsAsync(value);

-_cache.Setup(x => x.SetAsync(key, value, expiration, It.IsAny<CancellationToken>()))
+_cache.Setup(x => x.SetAsync<string>((CacheKey)key, value, expiration, It.IsAny<CachePolicy>(), It.IsAny<CancellationToken>()))
     .ReturnsAsync(true);
```

If a test asserts the expiration-free path, set up the overload with no `expiration` parameter; the
two are distinct members now and a `VerifyAll` on the wrong one fails.

NSubstitute is unaffected. Calling the short form on a substitute runs the extension, which calls
the policy-bearing member on the substitute, and that is the call `Returns` and `Received()` record.
Existing NSubstitute tests keep working as written.

The blocking forwarders (`Get`, `Set`, `GetOrAdd` and the rest on `ICache<T>`) moved the same way,
to `CacheSyncExtensions` and siblings, with the same consequence for mocks.

## Hand-written implementations

Fakes and adapters that implement `ICache`, `ICache<T>`, `IHashCache` or `ISetCache` directly need
three kinds of edit:

- **`TryAddAsync`** is a required member on `ICache` and `ICache<T>`, with three overloads. A fake
  can return `false` (fail-closed) or delegate to a dictionary's `TryAdd`.
- **`policy` is a required parameter** on every member that takes one; the `= null` default is gone.
  Drop the default from the implementation too, so a call through the concrete type behaves like one
  through the interface.
- **The compat and sync forwarders are gone from the interfaces.** An explicit interface
  implementation of one, `ValueTask<T?> ICache.GetAsync<T>(CacheKey, CancellationToken)` say, no
  longer compiles, because the member is not on the interface; delete it. A public method of the same
  shape still compiles, but nothing reaches it through the interface any more, so callers going
  through `ICache` land on the extension and then on your policy-bearing member instead.

The cache and provider constructors take `ISerializerProxy<byte[]>` and a `TimeProvider`; see
[Clock](#clock).

## Clock

`CacheClock`, `ISystemClock` and the per-options `Clock` properties are gone. `System.TimeProvider`
is the single clock: `AddCaching` registers `TimeProvider.System` unless the container already has
one. A test that registered an `ISystemClock` to control time is no longer honored and silently runs
on the wall clock; register a `TimeProvider` before `AddCaching` instead. The conversion from a
lifetime to a deadline is `TimeProvider.ToDateTimeOffset(...)` in the abstractions package, and it
saturates at `DateTimeOffset.MaxValue` instead of overflowing.

`GetCacheEntryAsync` on a key with no TTL reports an `Expiration` of `DateTimeOffset.MaxValue` rather
than `now + DefaultExpiration`; `TimeToLiveAsync` and `ExpireTimeAsync` return `null` for the same
key, as they did in 1.x, so the two surfaces now agree that no deadline exists. The fabricated
deadline also acted as a hidden L1 cap in 1.x: the local copy of a no-TTL key turned
over every `DefaultExpiration`. It now lives until a broadcast invalidation or `LocalMaxExpiration`
evicts it, so set `LocalMaxExpiration` if you were relying on the turnover.

## Renamed types

| 1.x | 2.0 |
|---|---|
| `RedisTypePrefixes` | `RedisKeyspaces` (same constant values; no key moves) |
| `SystemJsonSerializerProxy` | `SystemJsonByteSerializerProxy` (same bytes) |
| `IEventFormatterProxy<T>.Decode(string)` / `EncodeAsString(T)` | `Decode(ReadOnlyMemory<byte>)` / `Encode(T)` |

`CacheKey.Equals` and `GetHashCode` are ordinal. Insensitive keys are lowercased at construction,
so nothing changes for them; keys built with `CacheKeyCasing.Sensitive` now compare
case-sensitively, which is what the mode says.

## Integrations built against 1.x

A package compiled against `UiPath.Caching` 1.3.0 still loads against 2.0, because .NET accepts a
higher assembly version than the one referenced. Loading is not working: every member the **Removed**
and **BREAKING** entries name is gone or has a new signature, and a 1.3 binary that touches one fails
at the first call with `MissingMethodException` or `TypeLoadException`, with no compile error on your
side. Which members a given integration touches is something only its source can answer. The
telemetry and profiler seams, `ICachingTelemetryProvider`, `ITelemetryOperation`, `ICachingBuilder`,
`IRedisProfiler` and the `RedisConnectionOptions` members a profiler reads, have no binary change
since 1.3.0, so an integration confined to those runs. That is a fact about 2.0.0, not a promise.

Prefer a build of the integration against 2.0 when one exists. Where the integration is a telemetry
bridge, `UiPath.Caching.OpenTelemetry` is the supported replacement and removes the skew entirely.
Until then, watch for those two exception types naming `UiPath.Caching` in the first minutes after a
deploy, and confirm cache hit and miss telemetry still arrives.

## Rollback

Reverting the package is enough for stored data: 2.0 writes what 1.3 wrote, under the same key
unless the `ShardKeyEnabled` case above applies. Code that adopted the
non-nullable expiration, the byte-array serializer seam or the re-targeted mocks does not compile
against 1.3, so a rollback of the binary is a rollback of the branch, not only of the version line.
