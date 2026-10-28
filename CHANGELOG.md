# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/)

## [Unreleased]

### Added

- **`RedisStreamsTopicOptions.AutoAck`** (off by default). Reads with `NOACK` and sends no `XACK`, so the group keeps no pending list and
  the pending-entry recovery is skipped after the entries an earlier run left pending are read once at start. A read that fails may
  have been delivered, so it is reported as missed events and the local tier is cleared rather than left stale, when a failure streak
  starts and again when a read ends it.

- **`InMemoryRedisCacheOptions.PeerFieldUpdates`** (off by default; `PeerFieldUpdateMaxFields`, 16, and `PeerFieldUpdateMaxBytes`, 1024). A small
  `HashReplace` write tells the other nodes which fields it wrote, and a node whose local copy of the hash is exactly one write behind (its own writes that the copy already holds do not count, and the copy waits for the events of the other nodes' writes that landed before them) adds
  them to a copy of its own instead of dropping it and reading the whole hash on its next call. A copy that is further behind, has no version,
  or a write that is a replacement of the key, a removal, over the limits or by a node that does not count, is dropped as before, except an event that is no newer
  than the copy and of the same epoch of the hash, which the copy already has and which is ignored (a late event used to drop the copy, and the read it caused was then hit by the next late event). The count's high bits name the
  epoch, drawn when a write makes the hash, so a hash made again after a removal, an expiry or an eviction is never taken for an earlier state of the old one. A failover that loses
  acknowledged writes can move the count backwards inside one epoch; the events of the writes it repeats are then ignored rather than dropped, until the reconnect clears the local tier or the TTL ends. Every write of a
  hash then also counts the hash's writes in a reserved `_version_` field (hidden from reads, rejected on writes), in the write's own
  transaction, so the TTL and the fields stay atomic. A `RemoveFieldsAsync` that empties a hash leaves the count behind until the key expires.
  Turn it on once every node that writes the hashes runs this version.

- **`RedisStreamsTopicOptions.NotifyMinInterval`.** The least time between two doorbells, 25 ms by default and at most 1 s.
  Rings during the interval fold into the next doorbell, so a burst of writes wakes each consumer at most once per interval instead of
  once per write; the first ring after a quiet period still goes out at once.

- **`RemoveFieldsAsync` on `IHashCache` and `IHashCache<T>`.** Removes the named fields with `HDEL` (in commands of
  `RedisCacheOptions.HashFieldBatchSize` fields, 1000 by default, all in flight together), drops the hash from the local tier of the
  writing node, and announces the key as removed, so every node, an older one too, drops its whole local copy of the hash: an entry is replaced
  whole, never patched, which keeps a read that races the removal from bringing a removed field back. A hash left without fields is gone. A
  reserved `_word_` field name is rejected, as on a write. `sealed` member served by `ISpanKeyHashCache` and its `<T>` sibling, on the
  library's hash caches and `HashCache<T>`; any other hash cache throws `NotSupportedException`, as does an `ISpanKeyHashCache`
  that does not override it.

- **`RemoveManyAsync` and `SetManyAsync`** on `ICache`, `ICache<T>`, `IHashCache` and `IHashCache<T>` (`sealed` members served by
  `ISpanKeyCache`, `ISpanKeyCache<T>`, `ISpanKeyHashCache` and `ISpanKeyHashCache<T>` on the library's caches, `Cache<T>` and `HashCache<T>`,
  which gain them with default bodies so an existing implementation still loads; any other cache gets the same calls through its
  single-key members). They take an `IEnumerable` and return the keys that
  failed, empty when all succeeded. One command per key, so no `CROSSSLOT` on a cluster, with the commands of a chunk all in flight before
  any is awaited and then the chunk's events likewise: about two round trips of wall time per chunk, whatever the key count or slots.
  The input is read in chunks (`InMemoryRedisCacheOptions.BatchChunkSize`, 500; `BatchConcurrentChunks`, 2), so memory stays with the chunk
  and a million keys is fine; only the failures are kept. A failure never stops the rest. The call is awaited under
  `BackgroundDistributedWrite` too, and waits for the pending background writes of its keys, and only those. A removal drops L1 first and is
  announced even when its delete failed, as `RemoveAsync` does; a set whose write failed gets no event. The caller's cancellation throws,
  between chunks and within one; what ran stays done. A key named twice in a chunk ends as its writes in order would leave it; across
  chunks, which run two at a time, their order is not set.
  `InMemoryRedisCacheOptions.BatchTopicInvalidationThreshold` (off by default) replaces the per-key events of an input of known size above
  it by one `TopicInvalidated` event after the writes, which makes every node drop its local tier of the topic, for every cache sharing it, and is counted as the
  `cache.batch.topic_invalidation` event. A node that does not know the event ignores it: turn the threshold on once all nodes run this
  version. If that event fails, every key of the call is returned.
  The hash `SetManyAsync` takes one `HashCacheEntryOptions` for all its hashes; the overload without one replaces each key. `RemoveAsync(CacheKey[])` and the batch `SetAsync` are
  separate calls, and on a cluster they too run one command per slot (see the slot entry under Fixed).

- **`CachePolicy.BackgroundDistributedWrite`.** Opt-in per policy (`Caching:Policies`, or the default policy for the
  whole cache): single-key `SetAsync` and the write after a `GetOrAddAsync` miss, on the cache and on the hash cache, return once the
  value is in L1, and the L2 write, then a set's event, run detached. The value is local for `LocalMaxExpirationDisconnected` until the L2 write
  succeeds, then for its normal lifetime; writes to one key keep their order; `InMemoryRedisCacheOptions.MaxBackgroundWrites`
  (default 1024) bounds them, with the foreground as the overflow; `BackgroundWriteDrainTimeout` bounds the wait for them
  when the cache is disposed (`Timeout.InfiniteTimeSpan` waits for them all; those it cancels stay local only and are counted
  under the `drain_cancelled` stage). Batch writes, `RemoveAsync`, `TryAddAsync`, refresh, metadata writes and a fill under the distributed lock are
  unchanged, and wait for the background writes of their key; a background write in turn waits for every write of its key
  issued before it, and an awaited write that lands after a newer background one leaves that one's value local, as does an L2 read
  that began before a write or removal of the key; such a read is not kept locally, nor is one of a key sharing the
  written key's stripe (one in 1024), and the next read keeps it. The caller's token ends the wait. The `IDistributedCache` adapter always writes in the
  foreground, as its payload may be memory the caller reclaims on return. A failed background step is logged and counted as the `cache.background_write.failed` event.

- **`IPolicyDefaults`.** `ICacheFactory.PolicyFactory` moves to this interface, which `CacheFactory` implements, and
  `Cache<T>` and `HashCache<T>` read it from a factory that implements it. `ICacheFactory` is left with no default
  member, so `new Cache<T>(factory)` over an NSubstitute substitute no longer throws `ArgumentNullException` on
  Castle.Core 5.2.1, which fails on a call to a default interface member (castleproject/Core#684).

- **State overloads on the hash caches.** `IHashCache` and `IHashCache<T>` gain `GetOrAddAsync` members that take a
  `TState state` before a `Func<TState, CancellationToken, Task<IDictionary<string, T?>>>` generator, for every
  expiration, set-option and policy variant of the closure ones (the policy-less `IHashCache` ones in
  `HashCacheExtensions`). The `CacheKey` ones are plain interface members; the `Span<char>` ones are `sealed` and call
  `ISpanKeyHashCache` (`ISpanKeyHashCache<T>`), which gains the same members, or else the `CacheKey` state member. Every
  library hash cache, Redis included, hands the state to the generator without a closure.
- **A state overload of `GetOrAddWithExpirationAsync`** on `ICache` and `ICache<T>` (the policy-less `ICache` one in
  `GeneratedExpirationExtensions`), a plain member every cache implements; the library's caches, Redis included, hand
  the state to the generator without a closure.
- **A batch `GetOrAddAsync` with one argument for the whole call.** `GetOrAddAsync<T, TState, TArgument>(entries,
  argument, generator, ...)` on `ICache` (and `<TState, TArgument>` on `ICache<T>`), for each expiration variant of the
  batch overload, hands the generator `argument` before the states of the keys that missed, so it can be static. The
  argument is not a per-entry state. It is a member every cache implements: the library's multilayer and in-memory
  caches and `Cache<T>` bind a closure only for a miss or a rehydrate, and Redis and `NullCache` hand the argument to the
  generator without one.

  None of the new overloads takes a call written for an existing one: a generator that throws or returns `default` has
  no natural type and cannot be a state or an argument, and where both apply the closure member is the more specific
  (`OverloadShapeTests`). The span state members carry `OverloadResolutionPriority(-1)`, as the other span members do.
  `Cache<T>`, `HashCache<T>`, `NullCache` and `NullHashCache` declare the new overloads as public members too, so a call
  on a public cache class takes them (`ConcreteCacheCallShapeTests`).
- **Analyzer `UIPCACHE001`: a generator that allocates on every call.** `UiPath.Caching.Abstractions` now ships an
  analyzer and code fix (under `analyzers/dotnet/cs`; a project that references `UiPath.Caching` gets them too). It
  reports, at Info severity, a generator passed to `GetOrAddAsync` or `GetOrAddWithExpirationAsync` on `ICache`, `IHashCache`,
  `ISpanKeyCache`, `ISpanKeyHashCache` and their `<T>` forms, or a class that implements one, that captures `this`, a local or a parameter (itself or through a local
  function), or is an instance method group, when a state (or batch argument) overload exists for the call; and a
  capturing lambda passed to a state overload. The fix, *Pass captured values as state*, moves the call to that
  overload with a `static` lambda: one capture is the state (`this` becomes `self`), several become a named tuple
  (`(Self: this, Id: id)`), and spelled type arguments get the state's type. It is not offered when the move could
  change what the generator reads (a captured variable assigned elsewhere, `base`, a capturing local function), and
  only when the rewritten call compiles in place without an error. The blocking `GetOrAdd` wrappers in
  `CacheSyncExtensions` and `HashCacheSyncExtensions` now pass their generator as state themselves. The plain lambda
  stays the default; raise the rule with `dotnet_diagnostic.UIPCACHE001.severity = warning` where the allocation
  matters. It has a category of its own, `UiPath.Caching`, so raising a category such as `Performance` leaves it alone.
  See `docs/how-to/choosing-a-get-or-add-overload.md`.

### Changed

- **Reads go to the master by default (was `PreferReplica`).** This is a default behavior change: unless you opt in, every Redis read
  (values, hashes, sets, TTLs, read scripts) is sent to the master. On an Azure Redis 6 cluster a replica round trip measured about 3 times the
  master's (1.6 ms against 0.55 ms), and a replica that still holds a key the master has removed could refill an entry with stale data right
  after an invalidation. Set **`RedisCacheOptions.ReadFrom`** to `Replica` to read from replicas as before; it is `Master` by default. With `Replica`, a read of a key this node
  wrote or removed within **`RedisCacheOptions.MasterReadWindow`** (1 second by default; set it above the expected replica lag), or heard an
  invalidation for through a live local entry, still goes to the master. A fire-and-forget TTL write (`AwaitRefresh` off) starts the window when
  it is queued. A key with no local entry is not guarded against another node's write, and neither is a lost-events clear on a node holding no local entries.

- **Breaking for implementers of `IHashCache`, `IHashCache<T>`, `ISpanKeyHashCache` and `ISpanKeyHashCache<T>`.** The
  state `GetOrAddAsync` overloads are now members to implement, not `sealed` defaults over the closure ones. A mock of
  `IHashCache` or `IHashCache<T>` must set up the state member itself: a state call (and the `HashCacheSyncExtensions`
  `GetOrAdd` on `IHashCache<T>`, which calls it) no longer falls back to the closure overload's setup. `RedisHashCache`
  now throws `ArgumentNullException` for a `null` generator from the `GetOrAddAsync` call itself, as the other caches do,
  instead of from the task it returns.

- **Breaking for implementers and mocks of `ICache` and `ICache<T>`: `IStatefulCache` and `IGeneratedExpirationCache`
  are folded into them.** `IStatefulCache`, `IStatefulCache<T>`, `IGeneratedExpirationCache` and
  `IGeneratedExpirationCache<T>`, which shipped in 3.0, are removed. Their members, the `CacheKey` state `GetOrAddAsync`
  overloads and `GetOrAddWithExpirationAsync` with and without state, are now members of `ICache` and `ICache<T>` to
  implement, not `sealed` defaults; the `Span<char>` forms stay `sealed` and call `ISpanKeyCache` (`ISpanKeyCache<T>`),
  whose implementers already provide them, or else the `CacheKey` member. A cache outside the library implements the new
  members; one that implemented the removed interfaces drops them from its base list and keeps its methods. A mock of
  `ICache` or `ICache<T>` must set up the member it is called through: a state call (and the `CacheSyncExtensions`
  `GetOrAdd`, which calls the state `GetOrAddAsync`) no longer falls back to the closure overload's setup, and
  `GetOrAddWithExpirationAsync` no longer reads through `GetCacheEntryAsync` (`GetAsync` and `ContainsAsync` on
  `ICache<T>`) and writes through `SetAsync`. `GeneratedExpirationExtensions` and `StatefulCacheExtensions` keep their
  policy-less overloads, which call the members. Code that tested `cache is IStatefulCache` or `cache is
  IGeneratedExpirationCache` drops the test: every `ICache` has the members. The batch `GetOrAddAsync` with an argument
  is folded the same way: its three members on `ICache` and `ICache<T>` are to implement, and a mock must set them up,
  since a call (and the multi-key `CacheSyncExtensions.GetOrAdd`, which calls it) no longer falls back to the batch
  closure overload's setup.

- **StackExchange.Redis 3.4.20 (was 3.3.1).** It fixes transactions on an idle connection being torn down as a dead socket when the
  replies are slower than one heartbeat (the hash writes are `MULTI`/`EXEC`), and `IBatch` commands that never completed after a
  connection drop. Commands that a dropped connection fails now throw `RedisConnectionException` instead of a raw transport or
  cancellation exception. Under RESP3 (the Azure Entra default) pub/sub now uses its own connection, so a server sees one more client
  per node. `Microsoft.Extensions.Logging.Abstractions` rises to 10.0.13, the floor StackExchange.Redis 3.4.20 declares.

- **`RedisStreamsTopicOptions.PollBatchSize` defaults to 1024 instead of 4096.** It is the `COUNT` of each
  `XREADGROUP`, and the reply shares the cache's connection: the commands behind it wait for it, and with a short
  `syncTimeout` on a small server a 4096-entry reply of a node catching up could take most of the timeout by itself.
  A full batch is read again at once, so catching up costs a round trip per 1024 entries. A `PollBatchSize` set in
  configuration is kept. The documentation no longer says `Limit` caps a read: neither the reader nor the publisher uses it.

- **The Redis Streams consumer polls every 5 s while its notify doorbell is live, unless `PollInterval` is set.**
  `RedisStreamsTopicOptions.NotifiedPollInterval` (`TimeSpan?`, bound from the same section and per topic) is the
  cadence while the doorbell subscription is confirmed live. Unset, it is 5 s when `PollInterval` was not set, and
  `PollInterval` when it was, so an app that configured `PollInterval` keeps its cadence. A `PollInterval` key in
  configuration counts as set even at its 250 ms default, and a global one counts as set for every topic. Only
  `AddRedisStreams(sectionName)` and the `Topics[]` entries keep an absent key unset: an app that binds the options
  itself (`AddRedisStreams(o => section.Bind(o))`, `services.Configure<RedisStreamsTopicOptions>(section)`,
  `ConfigureRedisStreamsTopic(name, o => section.Bind(o))`) counts as having set it, so set `NotifiedPollInterval`
  explicitly there for the long cadence. A reader takes the long interval from its own subscription, so entries from a
  writer that does not ring the doorbell (`NotifyEnabled: false`, another app sharing the stream, a version from before
  the doorbell) reach it up to `NotifiedPollInterval` late; where some writers do not ring, set
  `NotifiedPollInterval` to `PollInterval`, or keep `PollInterval` set. A restore of the subscription connection
  reads once, since a ring sent before the subscription is taken again is lost.
  `PollInterval` stays the cadence before the first subscribe, after a forced reconnect until the topic subscribes again, while a subscription
  connection is down, when the server refuses the subscription, and with `NotifyEnabled: false`. A set value must be positive
  and at least `PollInterval` (`OptionsValidationException` from `AddRedisStreams`, and when a topic with an override
  is made). A subscription connection failing ends a wait in progress at the long interval; a failed command
  connection does not. Failure retries and the `XINFO GROUPS` position check stay on `PollInterval`. Idle cost per
  process with 3 topics against a local Redis: 44.4 server-executed commands a second at 250 ms, 12.0 at 1 s, 6.0 at
  2 s, 2.4 at 5 s, 1.2 at 10 s; invalidation visibility with the doorbell is the same at each (median 14 ms, 90th
  percentile under 19 ms). What the long interval costs is the window left by a doorbell lost while its subscription
  still looks live (a dropped `PUBLISH`): up to one `NotifiedPollInterval` instead of one `PollInterval`. 5 s is where
  the saving levels off. Set `NotifiedPollInterval` to `PollInterval` for the old cadence.

- **A hash of many fields no longer goes to Redis in one command.** On `RedisHashCache`, with `RedisCacheOptions.HashFieldBatchSize` (1000) fields per command:
  a write of more fields is split into `HSET` commands in flight together. A `KeyReplace` write fills a staging key on the slot of the key and renames
  it over the key, so it still takes the old hash's place at once and expires as before (the `MULTI`/`EXEC` it replaces held every field in one
  command); a `HashReplace` merge goes straight into the hash and is not atomic across its chunks. A staging key left by a crash expires on its own, and one whose
  slot cannot be matched to the key's (a prefix with a key that has no hash tag, on a cluster) falls back to the single transaction.
  `GetAsync(key, fields)` splits a long field list into `HMGET`s likewise. A read of the whole hash (`GetAsync(key)`,
  `GetCacheEntryAsync`, the read of `GetOrAddAsync`) where read scripts run takes a hash of more than ten batches of fields by `HSCAN` pages
  instead of one `HGETALL`; its pages are not one snapshot. Smaller hashes cost what they did.

- **A single-key `SetAsync` writes the value before it publishes the event**, as the batch one does; so do the hash cache's `SetAsync` and the rehydrate writes. It published first, so
  a node the event woke could re-read the old value from L2 and keep it for its whole local lifetime. A write the inner
  tier refuses sends no event; one it takes whose event fails returns `false` with the value in place.

- **`Span<char>` overloads of `GetOrAddWithExpirationAsync`.** `ICache` and `ICache<T>` gain `sealed` members that take
  the key as a `Span<char>`, with and without a `TState state`, so a caller that formats its key on the stack and reads
  first no longer has to fall back to a string key for a generated expiration. `ISpanKeyCache` and `ISpanKeyCache<T>`
  gain the matching members, which the library's multilayer and in-memory caches, `Cache<T>` and `NullCache` implement:
  a warm hit allocates nothing, and a miss reads the local tier once, where a span read followed by a string-key call
  read it twice. **Breaking** for an `ISpanKeyCache` or `ISpanKeyCache<T>` implementation outside the library, which
  must add the two members (a cache that only implements `ICache` is unaffected: any other cache, a Moq or NSubstitute
  proxy included, is called through the `CacheKey` member, as every span member is). The interface members carry
  `OverloadResolutionPriority(-1)` and the `CacheKey` members of `Cache<T>` `OverloadResolutionPriority(1)`, so a bare
  `default` key still binds to the `CacheKey` overload on the interfaces and on `Cache<T>`.

- **`Span<char>` overloads of `SetAsync`.** `ICache`, `ICache<T>`, `IHashCache` and `IHashCache<T>` gain `sealed`
  members that take the key as a `Span<char>`, for each expiration variant of the single-key write (the
  `HashCacheEntryOptions` one included), so a consumer that formats its key on the stack no longer builds a string or a
  `CacheKey` to write. `ISpanKeyCache`, `ISpanKeyCache<T>`, `ISpanKeyHashCache` and `ISpanKeyHashCache<T>` gain the
  matching members, which the library's multilayer and in-memory caches, `Cache<T>`, `HashCache<T>`, `NullCache` and
  `NullHashCache` implement; any other cache, a Moq or NSubstitute proxy included, is called through its `CacheKey`
  members. A stored entry is keyed by a string, so a write builds that string once on every cache: the saving is the
  caller's own key string and, through a typed cache, the one its key strategy would compose. **Breaking** for an
  implementation of any of the four `ISpanKey*` interfaces outside the library, which must add the new members. The
  span members carry `OverloadResolutionPriority(-1)`, as the other span members do.

- **`Span<char>` overloads of the field, metadata, TTL and expiry reads.** `ICache`, `ICache<T>`, `IHashCache` and
  `IHashCache<T>` gain `sealed` `TimeToLiveAsync` and `ExpireTimeAsync` members that take the key as a `Span<char>`, and
  `IHashCache` and `IHashCache<T>` gain the same for the field-subset `GetAsync(key, fields)` and for
  `GetMetadataAsync`. `ISpanKeyCache`, `ISpanKeyCache<T>`, `ISpanKeyHashCache` and `ISpanKeyHashCache<T>` gain the
  matching members, which the library's multilayer and in-memory caches, the Redis hash cache, `Cache<T>`,
  `HashCache<T>`, `NullCache` and `NullHashCache` implement; any other cache, a Moq or NSubstitute proxy included, is
  read through its `CacheKey` member. On the multilayer and in-memory caches a local hit is found by span: a warm TTL,
  expiry or metadata read allocates nothing on .NET 9 and later, and a field read selects its fields from the local
  entry without building the key. **Breaking** for an implementation of any of the four `ISpanKey*` interfaces outside
  the library, which must add the new members. The span members carry `OverloadResolutionPriority(-1)`, as the other
  span members do.

- **`Span<char>` overloads of the remaining single-key writes.** `ICache` and `ICache<T>` gain `sealed` `Span<char>`
  forms of every `TryAddAsync` and `RefreshAsync` overload, and `IHashCache` and `IHashCache<T>` of every
  `RefreshAsync` overload, `RemoveFieldsAsync` and `SetMetadataAsync`. Each builds the `CacheKey` from the text and
  calls the `CacheKey` member, so every implementation, a Moq or NSubstitute proxy included, serves it unchanged; the
  capability interfaces gain nothing. Every single-key member of the four cache interfaces now has a `Span<char>` form.
  The members carry `OverloadResolutionPriority(-1)`; under C# 12 a bare `default` key on these members is now
  ambiguous (CS0121), as on the other span members.

- **Read scripts are on by default.** `RedisCacheOptions.ReadScriptsEnabled` now defaults to `true`: on Redis 7 and
  later, `RedisCache` and `RedisHashCache` read a value (or a hash) and its expiration with one `EVAL_RO` script
  instead of a MULTI/EXEC transaction, which allocates about 2.5 KB less per miss through the in-memory and Redis
  tiers. The fallback is unchanged and automatic: before Redis 7 no script is sent; a connection user without
  `@scripting`, or a proxy that cannot run scripts, gets one warning per cache instance (`RedisCache` and
  `RedisHashCache` each log their own) and then the transaction path for good, so no read is lost. Set
  `ReadScriptsEnabled: false` to avoid the attempt, and the warning, where that is the known state of the server.

- **The Redis Streams notify doorbell is on by default, with a cheap wake.** `RedisStreamsTopicOptions.NotifyEnabled`
  now defaults to `true`: the publisher rings a Pub/Sub channel after every `XADD`, so the other nodes read the
  stream at once instead of at their next `PollInterval` tick. Measured between two hosts on one machine against a
  local Redis (`benchmarks` `doorbell` mode), a `SetAsync` became visible to the other host's local tier in 2.7 ms at
  the median and 3.3 ms at the 99th percentile, against 112 ms at the median and 218 ms at the worst with the poll
  alone. The poll stays as the safety net.
  What a broadcast now costs on the server: the `XADD`, one fire-and-forget `PUBLISH`, and on every other node one
  script (`XINFO STREAM` and `XREADGROUP` in one round trip) and one `XACK`. The doorbell carries the publisher's
  consumer name, so a node does not wake for its own ring. Every read is still checked for entries trimmed past the
  group's position, but a woken node checks against the position it knows, and the check that also asks the server
  for it (`XINFO GROUPS`, which grows with the number of nodes) keeps the poll's cadence (once a poll interval, after
  a reconnect and after a full batch); doorbells that arrive during a read collapse into one more read; and a read
  that drained the stream waits for the next ring or poll instead of reading again. A node's own entries, which
  nothing rings for, are read every `MaxLength / 2` of its own publishes as well as at the poll, so a burst of them
  cannot be trimmed unread and reported as a loss. The per-node subscription is one Pub/Sub channel per topic; where
  the server refuses it (`NOPERM`, or `SUBSCRIBE` disabled) the topic logs one warning and polls only, and a
  connection error is retried at `NotifySubscriberTimeout` with one warning per streak. Set `NotifyEnabled: false`
  to poll only.
  On a Redis Cluster a plain `PUBLISH` is forwarded over the cluster bus to every node; `NotifyShardedPubSub` keeps
  it on the stream's shard where Redis 7.0 and a hash-tagged stream key allow it, and stays off by default because
  neither can be assumed.

- **The span reads, the state `GetOrAddAsync` and `GetOrAddWithExpirationAsync` are interface members.** The span
  members are `sealed` members with a body on `ICache`, `ICache<T>`, `IHashCache` and `IHashCache<T>`, so IntelliSense and
  go-to-definition find them on the interface. Each body does what the extension did: a cache that implements the
  capability interface (`ISpanKeyCache`, `ISpanKeyHashCache` and their siblings) takes its allocation-free path; any
  other cache, including a Moq or NSubstitute proxy, is called through its `CacheKey` members. A sealed member is not
  virtual, so a proxy never intercepts it and a test's `CacheKey` setups answer it as before. The `CacheKey` state
  `GetOrAddAsync` and `GetOrAddWithExpirationAsync` are plain members (see the breaking entry under Changed). The policy-less state and `GetOrAddWithExpirationAsync` overloads of `ICache` stay extensions, as the other
  policy-less overloads do. A class does not inherit an interface's members, so the public caches declare every shape
  the removed extensions served: `NullCache` and `NullHashCache` make their span members public and
  `NullCache` adds the policy-bearing state `GetOrAddAsync` overloads and `GetOrAddWithExpirationAsync`; `Cache<T>` and
  `HashCache<T>` add `RemoveAsync(Span<char>)`. A call on `Cache<string>`, `NullCache` or another public cache compiles
  as it did on 3.0. A bare `default` key binds to the `CacheKey` member under C# 13 (`OverloadResolutionPriority(-1)` on
  the span members); under C# 12, the default for a `net8.0` project, it is now ambiguous (CS0121) for every member that
  gained a span overload, on the interfaces and on `NullCache`, `NullHashCache` and `HashCache<T>`, as `GetAsync` already
  was; cast it to `CacheKey`.

### Removed

- **`ICacheFactory.PolicyFactory`.** It is on `IPolicyDefaults` now. A factory that returned its own policy factory
  implements `IPolicyDefaults` as well; one that relied on the `null` default drops nothing. A `PolicyFactory` property
  on a class that does not implement `IPolicyDefaults` still compiles and is no longer read. Code that read
  `factory.PolicyFactory` reads `(factory as IPolicyDefaults)?.PolicyFactory`, or takes `ICachePolicyFactory` from
  DI. A test that set it up on a substitute creates it with `Substitute.For<ICacheFactory, IPolicyDefaults>()`
  (Moq: `.As<IPolicyDefaults>()`).

- **The 3.0 extensions the interface members replace.** `SpanKeyExtensions` is gone, with all 36 of its methods (the
  `Span<char>` `GetAsync`, `ContainsAsync`, `GetOrAddAsync`, `GetCacheEntryAsync`, `GetItemAsync` and `RemoveAsync`
  over `ICache`, `ICache<T>`, `IHashCache` and `IHashCache<T>`). `StatefulCacheExtensions` loses its `GetOrAddAsync<T,
  TState>` overloads that take a `CachePolicy?` on `ICache` and all three over `ICache<T>`; `GeneratedExpirationExtensions`
  loses the `GetOrAddWithExpirationAsync<T>` that takes a `CachePolicy?` on `ICache` and the one over `ICache<T>`. The
  policy-less `ICache` overloads of both classes stay. Recompiled, an extension-syntax call binds to the interface member
  (or the class member, on a public cache class) with no change, except a call on `ICache<T>`, `IHashCache<T>`,
  `Cache<T>` or `HashCache<T>` that spells the value type as a type argument. The extensions were generic in it; the
  members take it from the cache, so drop it: `typed.GetAsync<string>(span)` becomes `typed.GetAsync(span)`,
  `typed.GetOrAddWithExpirationAsync<string>(key, generator)` becomes `typed.GetOrAddWithExpirationAsync(key,
  generator)`, and `typed.GetOrAddAsync<string, int>(key, state, generator)` becomes `typed.GetOrAddAsync(key, state,
  generator)` or `typed.GetOrAddAsync<int>(...)`. A call written as a static method
  (`SpanKeyExtensions.GetAsync<T>(cache, ...)`) becomes an instance call on the cache. An assembly compiled against 3.0
  that calls a removed method and is not recompiled fails when the JIT first compiles the method holding the call, with
  `TypeLoadException` for a `SpanKeyExtensions` call and `MissingMethodException` for the others; rebuild it against
  3.1.

### Fixed

- **A group this node made on an empty stream no longer reports its first entry as lost on Redis 6.** Without `max-deleted-entry-id`,
  an entry added after a group made at `0-0` looked trimmed, so the node logged "entries were removed before they were read" and cleared its
  whole local tier. The node now remembers it made the group on a stream that had never held an entry, and reports no gap for it unless the stream
  has reached `MaxLength`, where a trim may really have happened. A group that was found, or made on a stream that had held entries, is checked as before.

- **A topic name that selects a disabled or unregistered provider now fails host start.** `CacheOptions.DefaultTopic`, or the `Topic` of an
  `InMemoryCacheOptions` or `InMemoryRedisCacheOptions` with broadcast on, naming a provider that is off (`Broadcast:RedisPubSub:Enabled` is
  false by default) or not registered used to resolve to the null topic provider, so broadcast was off with no signal. Start now throws an
  `OptionsValidationException` that names the provider and the setting to enable it; an empty `DefaultTopic` is rejected too. Nothing is checked
  while `BroadcastEnabled` is false. **Upgrade check:** an app that sets `Broadcast:RedisStreams:Enabled` to false and has a cache with no `Topic`
  resolves to the default `RedisStreams` and now fails start. Set `Caching:BroadcastEnabled` to false if it wants no broadcast, or point
  `DefaultTopic` at an enabled provider.

- **A Redis Streams read that times out no longer loses its invalidations.** Redis hands the entries of a read to the
  consumer and moves the group past them even when the answer never reaches the client, and the reader only ever asked
  for new entries, so those stayed pending and their invalidations were never applied: the node kept the old values
  until they expired. The reader now reads its own pending entries when it starts and after every failed read, before
  going back to new ones. A pending entry deleted from the stream before it was read is acknowledged and reported as
  missed, which clears the local tier for the topic, since its invalidation is lost. A read timeout is logged as a
  Warning once per 100, and at Debug otherwise, rather than as an Error every time. An entry that cannot be decoded is
  acknowledged and dropped, not reported as a missed message: a bad payload is not a lost invalidation, and reporting it
  would let one bad writer clear the local tier of every node. Each one is tracked as the
  `Caching.RedisStreamSubjectWriter.DispatchEventsAsync.UndecodableEvent` telemetry event and logged as a Warning once
  per 100.

- **The stream maintainer no longer deletes the consumer group of a live subscriber on a quiet stream.** A group with
  consumers counted as stale once its last delivery was older than `MaintainerTrimInterval`, even when nothing newer had
  been written, so on a stream with no traffic every live group was quarantined and then deleted after
  `MaintainerQuarantineInterval`, and its reader recreated it on the next `NOGROUP`. A group whose last delivery is the
  stream's last entry is now healthy, and a quarantine it was under is cleared.

- **Multi-key calls over keys of several Redis Cluster slots run, one command per slot, instead of throwing
  `CrossSlotKeysException`.** `GetAsync(CacheKey[])`, `GetCacheEntriesAsync`, the batch `SetAsync` and `RemoveAsync(CacheKey[])`
  refused such a batch, and with them the batch `GetOrAddAsync`, which reads its keys with `GetCacheEntriesAsync` and stores
  its misses with the batch `SetAsync`; on a cluster endpoint, keys without a shared hash tag made every such call throw. The
  keys are now grouped by slot (with the connector's key prefix, as before) and each group is sent as its own `MGET`, read
  transaction, write transaction or `DEL`, all in flight together, up to 1000 at a time; keys of one slot, and every batch on a server that is not a
  cluster, are still one command. The batch `SetAsync` is atomic per slot, not across slots: when one slot's transaction does not
  commit it returns `false` and the other slots may be written. A read whose slot fails reads that slot's keys as misses and
  keeps the others; a remove reports `false` when any slot fails. The multi-layer cache announces every key of a batch set it
  could not confirm, since some may be written, and drops its own copies of them. `CrossSlotKeysException` stays in the API and is no longer thrown.

- **A profiled Redis command's dependency names the configured server, not the cluster node.** The target was the
  endpoint that served the command, so on a cluster each shard's IP and port showed as its own dependency target. It is
  now the endpoints of `RedisConnectionOptions.ConnectionString` (`host:port`, with the default port filled in), and the
  node moves to the new `RedisNode` property (`ProfiledCommandProcessor.NodeField`). Without a connection string, as with
  a `ConnectionFactory`, or with a Sentinel `serviceName`, whose endpoints are not the servers that answer, the node stays the target. Queries and alerts on the dependency target need the new value.

- **A named caching stack's telemetry carries its name.** Everything a stack from `AddNamedCaching` reports through the
  application's `ICachingTelemetryProvider` (Redis dependencies, `Caching.Stats.*` metrics, events, exceptions) has a
  `CachingStack` property set to the stack's name, so stream and topic metrics of two connections no longer mix. The
  application's own stack adds none. A provider that `configureServices` registers is the stack's own and is left as it is.

- **A named caching stack's profiled commands keep the request's profiling session.** The stack has its own
  `IRedisProfiler`, and a session opened on the application's one did not reach it, so the stack's commands went to its
  default session without the request's session id. Each session the application's profiler opens is now opened on the
  stack's as well, and processed by it when closed. An application that forwards its profiler to the stack through
  `configureServices` gets the stack's commands with its own connection as their target and without `CachingStack`; it
  can drop that forwarding now.

- **`PollInterval` must be positive.** Zero or negative failed later, when the poll loop waited on it; it is now
  refused by the options validator with the other Redis Streams options.

- **`IHashCache.GetCacheEntryAsync` on Redis is one round trip, as documented.** It asked the server whether the key exists before
  reading it; the read of an absent key already answers a miss, so the first trip is gone.

- **A `HashReplace` write left the hash cache's local tier with only the fields it wrote.** The store merges the fields into the hash, but L1
  kept the partial dictionary, so a read on the writing node lost every other field until the entry expired. The write now merges
  into the hash L1 holds, keeping its metadata, and leaves nothing in L1 when it holds none, so the next read fetches the whole hash. A
  `KeyReplace` write is unchanged, and so is an empty `HashReplace` write with `CacheNullValues` on: it replaces the hash with an empty one
  in both tiers, as the store always wrote it.

- **The notify doorbell of a Redis Streams topic is one `PUBLISH` at a time.** It was one per event, so a burst of many thousand events
  overflowed the subscriber connection's output buffer (`client-output-buffer-limit pubsub`) and Redis closed it, failing the
  commands queued on it. The rings that arrive while one is in flight are sent as one more, and a ring stalled on
  Redis holds them for at most one second.

- **A subscribe to a Redis Streams topic no longer blocks the caller.** A subscribe that found no consumer group created
  it synchronously while holding a lock the reader also takes. Every cache write subscribes a change token, so under a
  burst on a fresh process the request threads queued on that lock while the holder waited on a Redis reply that needed
  a thread-pool thread, and the pool starved: a load test of a fresh process served 0 of 1,500 requests. A subscribe now
  neither waits nor sends a command; with no group known it marks the topic in use and wakes the reader, which makes
  the group. Before making it at the head, the reader reads the server's clock and the stream's newest entry in one
  script; the subscribers that joined first are told of a loss only if an entry another node would act on was added
  since the first of them joined, which it checks by reading back what the group skipped (up to 256 entries) and
  ignoring its own and invalid ones. A fresh process on a quiet stream, or one whose own writes raced its group,
  reports nothing. The same check runs for a group found after a create that seemed to fail, such as one that timed
  out after the server ran it. Below Redis 7.0 the group is made at the newest entry the script read rather than at
  `$`, so what is added between the two is delivered. With scripts or `XINFO` refused, or the skipped entries
  unreadable, the subscribers are told; a cold start on such a server now expires the entries they cached before the
  group existed. `Subscribe` no longer throws when Redis cannot create the group; the reader retries, as it already
  did for a group lost while in use.

- **The connector key prefix, three edge cases.** An `ECHO` answered with no prefix no longer overrides a non-empty
  `RedisCacheOptions.KeyPrefix`: the configured value is used, with a warning, since a decorator may send `ECHO` past
  its own prefixing. Running out of probe attempts is now warned about, naming the `KeyPrefix` fallback used until the
  process restarts; it was silent, and with an empty `KeyPrefix` the maintainer again found nothing. A prefix skipped
  for its SCAN glob characters is skipped for stream discovery only: the cluster slot check hashes with it, since it is
  what the server receives.

- **Stream discovery reaches every shard through a connector of the application's own.** The maintainer asked
  `IRedisConnector.GetPrimaries()` for the servers to `SCAN`, and an implementation outside this library inherits
  its empty default, so each pass warned `No primary could be enumerated for stream discovery` and scanned only the
  routed server. On a cluster that is one shard: streams on the others went untrimmed and their abandoned consumer
  groups were never reaped. An empty answer now falls back to the connected primaries of `Database.Multiplexer`.
  A non-clustered Redis was unaffected beyond the warning.

- **Each primary is scanned in the connector's database.** The per-primary `SCAN` ran in the configured default
  database rather than `Database.Database`, so a connector on `GetDatabase(n)` with another `n` discovered no streams
  through it.

- **The first subscribe of a Redis Streams topic no longer risks a spurious loss.** A read issued before the subscribe
  created the consumer group, and answered `NOGROUP` after it, was reported as entries lost, so the local tier could be
  expired for nothing right after a process started. Such an answer now reports a loss only when the group was known to
  exist, and a loss already reported is not reported again when the recreate had to be retried.

- **A failed multi-key `RemoveAsync` still clears this node's local entries and sends every invalidation.** The
  `CacheKey[]` overload on the multilayer cache returned as soon as the inner delete failed, before it cleared the
  local entries, so after a transient Redis failure the removed values stayed in this node's memory until they
  expired; and it stopped at the first invalidation that failed, so the other nodes kept the rest of the keys. It now
  does what one single-key remove per key does: the local entries go first, every key's invalidation is sent even
  when another fails, and the call still returns `false` when the delete or any invalidation failed.

- **A Redis restart no longer leaves the Streams reader waiting out its back-off when the connection monitor is
  off.** The fetch loop's error back-off (three retries at `PollInterval`, then doubling up to 30 s) was ended only by
  the connection monitor's recovery events, so with `ConnectionMonitorEnabled: false` the reads that failed while
  Redis was down went on pacing the ones after it came back, and the doorbell could not wake the loop: the first
  invalidation after a restart waited out the remaining back-off. The reader now listens to the connection's own
  restored and reconnected events, which end the back-off and its failure streak with or without the monitor, so the
  group lost with the server is recreated and read at the poll's pace, and the next ring is answered at once. A
  recovery that lands between a read's failure and its back-off ends that back-off too, and that failure is not
  counted against the reads after it.

### Documentation

- `broadcast.md` says an explicit `MaintainerSearchPattern` must carry the connector's `WithKeyPrefix` prefix, and its
  broken em dashes are restored. `second-redis-connection.md` no longer suggests `KeyPrefix` for separate keyspaces: it
  prepends nothing itself.

- `docs/reference/interfaces.md` says how the capability calls behave over a mock (the span and stateful calls go to
  the proxy's `CacheKey` member, `GetOrAddWithExpirationAsync` reads, generates and sets; none coalesces) and how to
  test what the library does instead: over the in-memory cache, named explicitly, since an
  unnamed `CreateCache()` resolves `CacheOptions.DefaultCache` (`InMemoryRedis`) and falls back to `NullCache` when only
  `AddMemory()` is registered.

- **Which `GetOrAddAsync` shape to call.** `docs/how-to/choosing-a-get-or-add-overload.md` makes the plain lambda the
  documented default and measures the alternatives: a lambda costs 64 to 96 bytes per call; a delegate built once and
  the state overloads make a local hit allocate nothing. On a miss the lambda still costs its bytes, small next to the
  generator and the write. It says when the state and `Span<char>`
  overloads pay (a hot path a profiler has shown), how to write each shape, which one a wrapper should wrap, what runs
  behind the extension classes, and what a Moq or NSubstitute proxy sees. The README and quickstart point to it, and
  the XML docs of the capability interfaces name the classes that implement them. `GeneratorShapeBenchmark` in
  `benchmarks/UiPath.Caching.Benchmarks` is the measurement.

## [3.0.0] - 2026-10-13

### Added

- **Reads by `Span<char>`.** `SpanKeyExtensions` adds `GetAsync`, `ContainsAsync` and `GetOrAddAsync` over `ICache`,
  `ICache<T>`, `IHashCache` and `IHashCache<T>`, `GetCacheEntryAsync` over `ICache`, `IHashCache` and
  `IHashCache<T>`, and `GetItemAsync` over the hash surfaces, reading by the key's text without building a `CacheKey`.
  `GetOrAddAsync` builds the key only on a miss, or when the policy rehydrates, since rehydration keeps the key; the
  generator then runs exactly as it would on the `CacheKey` path.
  `NullCache` and `NullHashCache` implement the capability interfaces explicitly, so their public surface keeps only
  the `CacheKey` overloads; a span read reaches them through the extensions.
  The text is normalized as `new CacheKey(text)` normalizes it. A cache that implements `ISpanKeyCache`,
  `ISpanKeyCache<T>`, `ISpanKeyHashCache` or `ISpanKeyHashCache<T>` serves the read itself, as the library's caches
  do: over the in-memory tier on .NET 9 and later, under a key strategy that composes by span, as both built-ins do,
  a local hit allocates nothing. Any other cache, an outside implementation or a Moq or NSubstitute proxy, is read
  through its `CacheKey` overload, so a test that sets that overload up answers the span read too. The reads are
  extensions rather than interface members because a proxy generator emits invalid IL for a method that takes a
  `Span<char>`. The parameter is `Span<char>` rather than `ReadOnlySpan<char>`: a read-only span overload on the
  capability interfaces would make a call with a string ambiguous against the `CacheKey` overload. Through a
  library class, a bare `default` key, as in `cache.GetAsync<T>(default, policy)`, still binds to the `CacheKey`
  overload under a C# 13 compiler (.NET 9 SDK or later). Under an older compiler it is ambiguous (CS0121); cast it
  to `CacheKey`.
  On a cache of a nullable value type, such as `ICache<Guid?>`, a generator that returns the non-nullable type fails to
  infer (CS1503); pass the type argument, `GetOrAddAsync<Guid?>(...)`, or return `(Guid?)` from the generator.

- **`GetOrAddAsync` with generator state.** `GetOrAddAsync<T, TState>(key, state, (state, ct) => ..., ...)` over `ICache`
  and `ICache<T>`, by `CacheKey` or `Span<char>`, with the same expiration variants as the closure overloads. A static
  lambda and its state replace a capturing lambda, so a call that hits the local tier allocates no closure and no
  delegate. The built-in caches implement the new `IStatefulCache` and `IStatefulCache<T>`, and `ISpanKeyCache` and
  `ISpanKeyCache<T>` gain the span forms; `StatefulCacheExtensions` and `SpanKeyExtensions` prefer them and otherwise
  close over the state and call the existing overload, so a mock or an outside `ICache` keeps working. The Redis
  provider builds the factory-timeout wrapper only on a miss.

- **`RemoveAsync` by `Span<char>`.** `SpanKeyExtensions.RemoveAsync` over `ICache`, `ICache<T>`, `IHashCache` and
  `IHashCache<T>` removes by the key's text, normalized as `new CacheKey(text)` normalizes it, so a caller that
  formats its key on the stack need not build the `CacheKey` itself. Every tier needs the key as a string, so the
  extension builds it and calls the `CacheKey` overload; there is no capability interface to implement.

- **A generator that returns its value's expiration.** `GetOrAddWithExpirationAsync` takes a
  `Func<CancellationToken, Task<GeneratedValue<T>>>`; `GeneratedValue<T>(Value, Expiration)` carries the instant the
  value stops being valid, for a token whose lifetime is in the token. The entry is stored until that instant; a null
  `Expiration` takes the policy's lifetime, and one that has already passed returns the value without storing it.
  Single-flight, the locks and `FactoryTimeout` apply as they do to `GetOrAddAsync`; rehydration does not, since there
  is no configured lifetime to measure a threshold against. It is a separate method name because `GetOrAddAsync<T>`
  on `ICache` would bind first and cache the `GeneratedValue<T>` itself. The built-in caches implement
  `IGeneratedExpirationCache` and `IGeneratedExpirationCache<T>`. On a cache that does not (a mock, an outside
  implementation) the extension reads, and on a miss runs the generator and sets its value with the returned
  expiration, or with the policy's lifetime when there is none; it does not coalesce. A cached default value (`0`,
  `false`, a cached `null`) is a hit on `ICache`; the typed `ICache<T>` has no entry read, so it asks
  `ContainsAsync` when the value reads as the default. It has no clock to tell that an
  expiration has passed, so a set that rejects it with `ArgumentOutOfRangeException` leaves the value returned and not
  stored, as the capable caches do. Moq and NSubstitute proxies take this path.

- **`CacheKey(ReadOnlySpan<char>)`** and `CacheKey(ReadOnlySpan<char>, CacheKeyCasing)` build a key from text that
  is not yet a string, normalizing while copying, so a key formatted on the stack costs one string rather than two.

- **`IResiliencePipeline.ExecuteAsync<TResult, TState>`.** Hands a state value to a static callback, so a Redis
  command no longer allocates a closure and a delegate. The Polly pipeline boxes the state once instead, since Polly
  copies its state into each strategy's state machine. Its default implementation forwards to the existing overload,
  so a custom pipeline keeps compiling; implement it to drop the closure there too.

- **`RedisCacheBase.RunAsync`, `KeyTimeToLiveAsync`, `KeyExpireTimeAsync` and `SupportsExpireTime`.** The
  one-command shape the Redis caches repeat, in one place: a pipeline call timed by a telemetry operation, tracked
  as a hit by a predicate, and on failure logged and answered with a fallback. The thirteen single-command members
  of `RedisCache`, `RedisHashCache` and `RedisSetCache` forward to it; the TTL and expiration reads, with the
  pre-Redis-7 fallback from TTL to expiration, live in the base once instead of in each cache; and `RedisHashCache`
  reads a hash's fields in one method instead of two copies of the loop. Protected, for a subclass that adds a
  command of its own. `SupportsExpireTime` resolves the server version on first use, so constructing a cache
  that never reads an expiration, such as `RedisSetCache`, does not connect.

### Changed

- **Dependency floors.** What a consumer's restore has to satisfy, taken from `Directory.Packages.props`:

  | Package | 2.1.0 | 3.0.0 |
  |---|---|---|
  | `StackExchange.Redis` (`UiPath.Caching`, `UiPath.Caching.Queue`) | 3.3.0 | 3.3.1 |
  | `AsyncKeyedLock` (`UiPath.Caching`) | 8.1.2 | no longer referenced |

  The `Microsoft.Extensions.*`, Polly and `Microsoft.Azure.StackExchangeRedis` floors are unchanged.

- **`RedisCacheOptions.ReadScriptsEnabled` reads with one read-only script.** Opt-in, off by default. On Redis 7
  or later, `RedisCache` and `RedisHashCache` then read a value (or all hash fields) and its expiration with one
  `EVAL_RO` script instead of a MULTI/EXEC transaction, still atomically and still from a replica when one is
  preferred; a batch read runs one script for all its keys. A miss through the in-memory and Redis tiers allocates about
  2.5 KB less (8.7 KB to 6.2 KB on net10), a miss under the distributed lock about 5 KB less and a hash hit about
  1.9 KB less; round trips are unchanged. The connection's user needs `@scripting`. Before Redis 7, if the first script
  fails for any reason, or once the server refuses one as unknown or not permitted, reads use the transaction; the
  switch is logged as a warning. After a script has worked, timeouts and connection errors do not switch it.

- **The first Redis connect no longer blocks a thread.** `RedisConnector.Database` waits for the first connect
  synchronously, so on a new instance every concurrent first call held a thread-pool thread until it finished. The Redis
  caches, the distributed lock and the health check now wait for the connector with `ConnectAsync` before they read
  `Database`; the cache commands do it inside the resilience pipeline, so the wait is cancellable and bounded by the
  request timeout, like the command. `RedisConnector.ConnectAsync` costs nothing once connected. The synchronous
  `Database` and `Subscriber` accessors are unchanged for other callers. `WarmUpOnStart` stays `false`: it opens a
  connection, and runs any authentication configurator, at every host start, which a consumer that never reaches Redis
  should not pay; with the wait asynchronous, the first call no longer needs it to stay off the thread pool.

- **The Redis provider's `GetOrAddAsync` reads Redis once and runs one generator per key at a time.** Concurrent
  misses on one key in a process each ran the generator, since the Redis provider has no lock, and every caller read
  the key first. Every caller now goes straight to one run per key: the run reads Redis once, returns what it finds
  (concurrent hits share that one read), and on a miss generates and writes once. Callers that arrive meanwhile join
  the run and take its result or its failure. The run is on its own token, cancelled once every caller has cancelled,
  and `FactoryTimeout` applies to it. The first caller's generator and expiration are the ones used. N concurrent
  callers on a key send one `GET` (and one `SET` on a miss) instead of N, and a read that failed counts as a miss for all of
  them, as before. The hit and miss counts record the reads sent, one per run, not one per caller. This also holds while
  Redis is unreachable, when every call is a miss. The Redis provider takes no distributed lock, so none is involved;
  the multilayer provider's `Lock` settings are unchanged.

- **A caller whose wait for the local lock times out joins the generator in flight.** `GetOrAddAsync` on the multilayer
  and hash caches used to run the caller's own generator once `LocalLockTimeout` (500 ms by default) expired, so a
  loader slower than that was run by every caller that arrived while it did. The caller now re-reads the cache as before
  and then joins the load running for the key, sharing its result or its failure, and starts its own only when none is
  running. The shared load runs on its own token: a caller that cancels stops waiting, and the load is cancelled once
  every caller has. **Breaking** in what it observes: callers that waited out the lock no longer run separate
  generators, so a generator that failed for one of them now fails them all, and the generator that runs is the first
  caller's, with its expiration and policy. With `LocalLockEnabled` off nothing is shared, as before. Batch
  `GetOrAddAsync` does the same per key: a key whose generator run is in flight, from a single-key call or another
  batch, is joined, including by a batch that only partly overlaps it, and the generator receives only the keys no run
  covers. A joined key shares the run's failure; if the batch that started the run cancels, its joiners re-read the
  cache and take the key over together, one of them generating it for the rest. A single-key call joins a batch's run
  for its key the same way, and a batch and a single-key call never generate one key twice. A caller that takes a
  key looks it up once more in the local tier, and only there, so a key another run of this process finished since the
  caller's read is returned, to its joiners too, and not generated; a batch generates only the keys still missing. A run
  writes the local tier before it leaves the table, which is what makes that lookup enough. With the local tier
  disabled or the entry evicted the lookup finds nothing and the key is generated once more, after the earlier run, never
  beside it. The Redis-only provider has no local tier; it reads Redis once per run instead. The starting caller's locks
  stay held until the shared load ends, even when that caller cancels and stops waiting, in the hash cache as in the
  multilayer cache. A load every caller has left does not store its result, so a generator that ignores its
  cancellation cannot overwrite what a newer load wrote. A batch that stops early, whether it failed or was
  cancelled, stops counting as a waiter on the loads it joined, so they are cancelled once nobody else waits. The hash
  cache has no batch overload.

- **A Redis outage is served from the local tier by default.** `CacheOptions.ConnectionMonitorEnabled` and
  `UseLocalOnlyWhenDisconnected` now default to `true`: while the inner tier is disconnected, reads are served from
  the local tier and writes kept there, capped at `LocalMaxExpirationDisconnected` (30 seconds by default). The new
  `ClearLocalOnReconnect`, also `true` by default, expires the local entries of a Redis Pub/Sub topic when its
  connection is restored, since the invalidations sent while it was down never arrived. The topic expires them through
  their change tokens, so it works on any local tier, a custom `IMemoryCacheFactory` one included. A forced reconnect counts too: swapping the connection
  can drop publications although it never read as down. A Pub/Sub topic also subscribes only after a delay, at first
  and after a reconnect, so once its subscription is in place it expires the entries cached before then; a cache with
  `ClearLocalOnReconnect` off keeps them. Redis Streams replay what was sent meanwhile, so their local tier is kept.
  The monitor setting is app-wide, so the broadcast providers' connection monitor turns on too. **Breaking** for an app that relied on a disconnected tier answering
  misses: set `UseLocalOnlyWhenDisconnected` to `false`, or `ConnectionMonitorEnabled` to `false` for the previous
  behavior. **Breaking** too for a custom `IMultilayerCacheOptions` implementation, which must add
  `ClearLocalOnReconnect`.

- **`RedisConnector.IsConnected` is `false` only when a connection exists and reports itself down.** Before the
  first connect, while it is pending and after it faulted, it is now `true`, so a command goes through and opens or
  retries the connection; it used to be `false`, and the operations that check it first, L2 reads and every
  `ISetCache` call, did nothing until a write had connected. Observable to code that reads `IRedisConnector.IsConnected`
  directly, such as a status endpoint: use `RedisHealthCheck` to learn whether the server is reachable.

- **Concurrent single-key reads share one inner read.** On a local miss, concurrent single-key `GetAsync`,
  `GetCacheEntryAsync` and hash reads of one key, and the read `GetOrAddAsync` makes before its lock, wait for a
  single inner-tier read and share its result or failure. Reads share only when they read the same value type with
  the same `LocalExpiration` and `LocalExpirationDisconnected`, since the shared read keeps its hit for those
  lifetimes; reads that differ in any of them read separately. Multi-key reads still read every missing key
  themselves. A caller that cancels stops waiting; the shared read is cancelled only once every caller waiting on it
  has cancelled.

- **`GetOrAddAsync` keeps a value the inner tier refused.** When the inner write fails or throws, the generated value
  is kept in the local tier for `LocalMaxExpirationDisconnected`, by the batch overload too, so the callers waiting on
  the local lock reuse it instead of each running the generator again.

- **A string key reads the local tier by its text.** On .NET 9 and later, `GetAsync`, `GetItemAsync`,
  `ContainsAsync`, `GetCacheEntryAsync` and `GetOrAddAsync` with a `CacheKey` look the in-memory tier up as the span
  reads do, and `Cache<T>` and `HashCache<T>` compose their strategy's key on the stack instead of through
  `GetCacheKey`. A local hit through a typed cache with a prefix strategy, the common shape, no longer allocates the
  prefixed key, its entry options or, for `GetOrAddAsync`, the rehydration closure, with no change at the call site.
  A key built with a casing other than `CacheKey.DefaultCasing`, a strategy that declines `TryGetCacheKey`, and
  .NET 8 under a custom strategy take the path they took before. Under `DefaultCacheKeyStrategy` the lookup is by the
  key's text on every runtime and over any `IMemoryCache`, so a miss costs one lookup instead of the span composition.

- **A `GetOrAddAsync` miss does less work before it generates.** A call that has just missed the local tier goes
  straight to the inner tier instead of reading the local tier again, and a span key's miss no longer reads it a
  second time on the way to the `CacheKey` path; the hash cache likewise. After a caller takes its key it looks the
  local tier up once more only when something in the process wrote that tier since the caller's first read, counted
  per cache, instead of on every miss.

- **The caller that starts a shared run waits on the work, not on the run's published task.** A miss that starts the
  generator used to resume through a thread-pool hop, since the shared task completes its continuations asynchronously
  so that a joined caller never runs another caller's code inline. The starter now resumes where the work ends, as an
  awaited call does, which saves the hop and the shared task: a run nobody joins builds none. Callers that join the run
  resume asynchronously as before. A starter that cancels still gets a `TaskCanceledException`, and a failure of the
  work still reaches it, and every joiner, as the exception the work threw.

- **The write, remove and broadcast paths allocate less.** The local setter and the event publisher take the entry
  options as they are, unboxed; the null change token is not wired to the local entry, so a replace or remove no
  longer queues a thread-pool work item for its eviction callback; a factory with no timeout hands back its own task;
  no event is built for the null topic; a key's observers live in an array instead of a dictionary per key; and the
  local tier keeps the entry built for the caller. On net10, a remove then re-add on the in-memory cache drops from
  1.6-2.2 us and 1416 B to 0.84 us and 616 B, a fresh in-memory miss from 1.27 KB to 688 B, and a miss on the
  in-memory tier over Redis from 10.45 KB to 8.5-9.2 KB. With `TrackStatistics` on, the memory cache now counts one
  extra hit per local write, from the lookup that tells whether it kept the entry (see Fixed).

- **The Redis provider shares a run by Redis key and value type, and builds less per call.** The run table is keyed by
  the `RedisKey` itself, per value type, instead of the key's rendered text, so a prefixed key is no longer turned
  into a string on every `GetOrAddAsync`, and the run's result is neither boxed nor cast. As before, two calls share a run only
  when the key strategy maps them to the same Redis key, so a strategy that reads ambient state, such as a tenant, and
  maps one cache key to different Redis keys keeps them apart. The two async frames wrapped around the run are gone.

- **A miss takes its key before any lock when no distributed lock is held.** With the local lock on, the default, and
  `DistributedLockEnabled` off, the default, `GetOrAddAsync` on the multilayer and hash caches takes the key's run in
  the process first and runs the generator in the caller that took it; a caller that arrives while it runs joins it at
  once. The keyed local lock, the read under it, its lock key and its async frames are not used on this path, and a
  miss that never waits builds no async state machine, so a miss costs one read of the local tier, and of the inner
  tier, instead of two. Batch `GetOrAddAsync` takes its keys the same way, per key, with no composite lock key and no
  second probe, and copies nothing when every key is its own. With a distributed lock enabled the caller that takes the run takes the lock; see the next entry.
  **Breaking** in what it observes: `LocalLockTimeout` no longer bounds a `GetOrAddAsync` wait on this path, since a
  caller that arrives while a run is in flight joins it however long it takes, and stops waiting only through its token,
  where it used to wait for the lock up to `LocalLockTimeout` and only then join; a generator that fails now fails
  every caller that arrived while it ran, where the callers queued on the lock each ran their own generator in turn
  and only those that timed out shared the failure; and `GetOrAddAsync` no longer excludes `TryAddAsync` through the
  lock. `TryAddAsync` keeps the exclusion in the process by taking the key's run for the length of its add: it waits up
  to `LocalLockTimeout` for a run that holds the key, as it waited for the lock, and loses if one still does, and a
  `GetOrAddAsync` that arrives during an add joins it and takes its value, or takes the key over if the add lost. A
  generator therefore does not store over a value `TryAddAsync` added in the same process while the local tier still
  holds it; a miss whose inner read began before the add and whose local copy of the added value is gone by the time
  its run starts, after an eviction or a short `LocalMaxExpiration`, can still generate and store over it, as the lock
  path's second inner read used to prevent. A failed add leaves nothing held. Across processes nothing changes: only the inner tier arbitrates there. A load every caller has left still
  does not store its result.

- **The in-process lock is gone; `ILocalLock`, `AsyncKeyedLocalLock` and `NullLocalLock` are removed.** One run per key
  in the process, the run table, does what the local lock did, without making a caller wait. `GetOrAddAsync` on the
  multilayer and hash caches, single key and batch, takes the key's run first and, when a distributed lock is enabled,
  only the caller that runs the generator takes it: after the run is reserved, it reads both tiers again under the
  lock, runs the generator on a miss and releases the lock when the run ends, even if that caller has stopped waiting.
  Callers that join the run never touch the Redis lock, so N concurrent callers take it once instead of N times, and a
  batch takes one lock over the keys it runs. `TryAddAsync` no longer takes any lock; it keeps the run reservation
  described above. `Lock.LocalLockEnabled` and `Lock.LocalLockTimeout` are renamed to `Lock.LocalSingleFlightEnabled`
  ("callers of one process share a run per key") and `Lock.LocalSingleFlightTimeout` ("how long `TryAddAsync` waits for
  a run that holds its key"), with the same defaults, on the provider options, `IMultilayerCacheOptions` and
  `LockProfile`. The old names stay as `[Obsolete]` aliases, in code and in configuration, that read and write the new
  ones; when both keys are configured the new one wins. A per-call profile still overrides them.
  `LocalSingleFlightEnabled=false` with `DistributedLockEnabled=true` is still rejected: with no shared run every caller
  of the process competes for the Redis lock. With a distributed lock, a timeout or failure taking it now fails every
  caller that joined the run, where each caller used to take the lock itself and fail alone. The set and queue
  caches' in-memory tier serialises writers of one key with a striped monitor (128 stripes chosen by the key's hash)
  instead of an async lock; their critical sections never await. **Breaking**: remove `ILocalLock` registrations and
  `AddLocalLock()` calls (they are unused), the `MultilayerCacheBase`, `InMemoryCacheProvider`,
  `InMemoryRedisCacheProvider`, `InMemoryQueueCacheProvider` and `InMemoryRedisQueueCacheProvider` constructors lose
  their `ILocalLock` parameter, `CacheOptions.LocalLockPoolSize` and `LocalLockPoolInitialFill` are removed (delete them
  from configuration: `AddCaching` now refuses to start while the `Caching` section still sets either), and the `AsyncKeyedLock` package is no longer a
  dependency. A subclass of `MultilayerCacheBase` that calls the protected `RunUnderLocksAsync` gets the distributed
  lock only, with no pre-read when none is enabled.

- **`ICachingTelemetryProvider` takes `TelemetryTags<T>` and has no default bodies.** `TrackDependency`, `TrackEvent`,
  `TrackException` and `TrackMetric` take `TelemetryTags<string>` properties and `TelemetryTags<double>` metrics in
  place of `ReadOnlySpan<KeyValuePair<…>>`. The struct holds up to nine pairs inline and more in one array, so the
  call stays allocation-free for the tags the library sends; a collection expression, a span or an array builds it
  implicitly, and it compares by value. With a span parameter, a Moq or NSubstitute mock of the interface threw
  `InvalidProgramException` on the first call and could not match the tags it received; now both work.
  `TelemetryTags.ToDictionaryOrNull` takes the struct. **Breaking** for every provider outside the library: change
  the parameter types, and implement all four methods, since the no-op default bodies are gone.

- **`ISpanCacheKeyStrategy.TryGetCacheKey<T>(ReadOnlySpan<char>, Span<char>, out int)`.** The span form of the
  composition, behind the `Span<char>` reads: the text arrives normalized, the strategy writes what `GetCacheKey`
  would build, and the library normalizes the result as `CacheKey.WithName` would. `DefaultCacheKeyStrategy` and
  `PrefixCacheKeyStrategy` implement it. A strategy outside the library implements it to keep a span read's local
  hit allocation-free; one that does not is read through `GetCacheKey`, as before. It is apart from
  `ICacheKeyStrategy` so a mock of the strategy never has to proxy a span.

- **The local tier is keyed by the key's text.** `IMemoryCache` receives `CacheKey.Name` rather than the boxed
  struct: no box per lookup, one object less per resident entry, and `MemoryCache`'s string-keyed dictionary
  instead of its object-keyed one. Observable only through a custom `IMemoryCacheFactory` that shares one
  `IMemoryCache` with other string-keyed entries, which can now collide on equal text. A key strategy that returns
  an empty key is refused with `InvalidOperationException` before any tier is touched; the local tier used to file
  every such key under one nameless entry.

- **A local write fills its entry in place.** The expiration, change token, eviction callback, size and value are
  set on the entry `CreateEntry` returns, instead of on a `MemoryCacheEntryOptions` that was copied into the entry
  and discarded, lists included. `PrefixCacheKeyStrategy` concatenates its prefix and separator, computed once,
  instead of interpolating them per key.

- **`PrefixRedisKeyStrategy` carries its prefix as the key's byte prefix.** `GetRedisKey` no longer concatenates a
  string per key: the prefix is encoded once and StackExchange.Redis writes it next to the key name. The key the server
  receives and its cluster slot are unchanged. **Breaking**: the protected `Prefix` and `Separator` setters are
  removed, since changing them would no longer change the keys, and a surrogate `separator` throws
  `ArgumentException`, since encoded apart from the name it could no longer pair with a name that starts with its
  low half.

- **The Redis caches write through a pooled buffer.** With the built-in JSON serializer, a value is serialized
  into a buffer rented from `ArrayPool<byte>.Shared` and returned once the write has completed, instead of an array
  the size of the payload for every write. The bytes match `Serialize`, custom `JsonSerializerOptions` included. A
  subclass of it, like any other serializer, keeps lending its own memory.
  Only writes use it: the write pipeline never abandons a command, so a buffer is never returned while the client
  may still be sending it.

- **Implementation classes moved to `UiPath.Caching`.** `SystemJsonByteSerializerProxy`, `RawByteSerializerProxy`,
  `Cache<T>`, `HashCache<T>`, `CacheFactoryExtensions`, `CacheExpiration`, `CacheKeyComparer`, `TelemetryTags` and
  `BatchGetOrAdd`, which is now public. The abstractions package keeps to interfaces, null implementations, DTOs and
  the extensions over them. Namespaces are unchanged. **Breaking** for code that references only
  `UiPath.Caching.Abstractions` and uses one of them: add a `UiPath.Caching` reference, or inject `ICache<T>` and
  `IHashCache<T>`, which `AddCaching` registers, instead of calling `CreateCache<T>`. A binary compiled against the
  old location fails with `TypeLoadException`, since the abstractions package cannot forward types to a package that
  depends on it.

- **`ICache`'s batch `GetOrAddAsync<T, TState>` overloads have no default implementation.** The default called
  `BatchGetOrAdd`, which left the abstractions package with the other implementations. `NullCache` implements them
  with the result `BatchGetOrAdd` gives over a cache that never hits. **Breaking** for an `ICache` implementation
  outside the library: implement the three overloads, forwarding to `BatchGetOrAdd.RunAsync` for the behavior the
  default had. An implementation compiled against the old interface fails to load with `TypeLoadException`.

### Removed

- **`ITelemetryOperation`, `TelemetryOperation`, `NullTelemetryOperation` and `ICachingTelemetryProvider.StartOperation`.**
  The caches time each operation with `TelemetryScope`, a struct read from the cache's `TimeProvider`, instead of
  allocating an operation object per call with telemetry on. It reports through the provider's `TrackMetric` and
  `TrackDependency` with the same metric names and tags, so a provider that implements those, as a provider
  normally does, needs no change. **Breaking** for code that calls `StartOperation`, overrides it, or references
  the removed types: time an operation with `new TelemetryScope(provider, timeProvider, providerName, method, type)`
  instead. `TelemetryScope` ships in `UiPath.Caching`, since the abstractions package holds no implementations. The tag
  constants moved from `TelemetryOperation` to `TelemetryScope`, and `NullTelemetryProvider` lost its
  `StartOperation` overloads.

### Fixed

- **A key rewritten from many threads no longer piles up change-token subscriptions.** A local write whose entry the
  memory cache did not keep, because a concurrent write to the key committed after it or it expired on insert, kept
  its change token subscribed until the cache's eviction callback ran on the thread pool, and a write that threw kept
  it until the topic closed. With 64 threads rewriting one key that reached hundreds of thousands of subscriptions on
  the key. The write now releases the token before it returns; the peak measured is 47.

- **Events dropped at a full dispatcher channel expire what the topic keeps.** A Redis Pub/Sub topic whose channel
  was full in `Wait` mode discarded the event, and the drop modes (`DropOldest`, `DropNewest`, `DropWrite`) discarded
  one silently on either transport; the invalidation it carried was lost. Each drop now expires the topic's local
  entries, coalesced to one expiry at a time.

- **A removal after a refresh expires the local entry.** A change token kept the metadata and expiration of an earlier
  refresh, so a later removal or loss, which carries neither, re-inserted the old value under a fresh token. It now
  clears them first, and a refresh delivered after the removal or loss no longer brings them back.

- **A refresh is broadcast as `CacheRefreshed`.** `CacheEventPublisher.CacheRefreshedAsync`, which `RefreshAsync`
  raises, sent the `CacheRemoved` type. The library's own receivers treat both alike, so only a subscriber that
  filters by event type sees the difference.

- **A Redis Streams topic that loses entries before this node reads them expires what it kept.** Before each read
  the consumer checks whether entries past its last delivery were trimmed (`MaxLength`, or the maintainer's trim) or deleted,
  since a trim can reach unread entries between any two polls, and a consumer group or stream removed while in use
  counts as a loss too. The check and the read run in one script, so a trim cannot land between them; where scripts
  are refused, they run apart, and where `XINFO` is denied, each reconnect counts as a loss. A gap is reported
  once: until the group reads past it or more entries are trimmed, later checks find the same one. The local entries on that topic then
  expire, in every cache that uses it, whatever `ClearLocalOnReconnect` says. They used to be kept, so a change
  whose invalidation was trimmed was served stale until the entry expired. Below Redis 7.0, which does not count
  the entries a group read, any trim past this node's last delivery counts as a loss, even one that removed only
  entries this node had read. From 7.0, the consumer group is created with the count of entries before it
  (`ENTRIESREAD`), so a trim before its first read shows and entries trimmed before it existed do not count.
  Where Redis cannot tell the count, as after a read past a deleted entry, the count last found at the same
  position stands in; with none found, a trim past the last delivery counts as a loss. The topic
  signals the loss through the new
  `IEventSubject<T>.Invalidate(MissedEventsReason)`: `Lost` for a Streams loss, which every cache honors, and
  `SubscriptionGap` for the Pub/Sub delay above. **Breaking** for a custom subject passed to the `RedisPubSubTopic` or
  `RedisStreamsTopic` constructor: implement `Invalidate` to expire what its observers keep. The library's change
  tokens implement the new public `IMissedEventsObserver`, so such a subject calls `OnEventsMissed(reason)` on each
  observer that implements it. A custom `IChangeTokenFactory` needs no change: for a cache that keeps its entries
  over a subscription gap, it is handed a view of the topic whose observers do not hear the gap.

- **Multi-key reads keep a usable local copy.** `GetAsync` and `GetCacheEntriesAsync` over several keys stored each
  inner-tier hit locally as a key-value pair rather than as the value, so no later read found it and every one went
  back to the inner tier. A hit is now kept as a single-key read keeps it, for `LocalMaxExpirationDisconnected` while
  the inner tier is disconnected.

- **The key prefix no longer depends on `RedisCacheOptions.KeyPrefix` being set.** A connector whose `IDatabase` is
  wrapped with `WithKeyPrefix` stores every key under that prefix, and two places need it: the stream maintainer, to
  scan for and strip it, and the cluster slot check, to hash the key the server receives. Both relied on `KeyPrefix`
  repeating the value, so a deployment that left it empty had a maintainer that found none of its streams, since the
  default SCAN pattern named keys without the prefix. The prefix is now read from the connector's database: directly
  from StackExchange.Redis's own wrapper, or, behind a decorator, by sending `ECHO` with a key argument once per
  connector, which the wrapper prefixes. `KeyPrefix` remains the fallback when neither answers, and an error is logged
  once when a configured value disagrees with the connector. A connector prefix that is not UTF-8 or
  holds a SCAN glob character (`*`, `?`, `[`, `]`, `\`) is skipped with a warning, and `KeyPrefix` is used instead.
  A `KeyPrefix` holding one of those glob characters is skipped the same way, and no prefix is assumed, since `*` or `?`
  would widen the maintainer's SCAN to other keyspaces. An explicit `MaintainerSearchPattern` is still used as written.

## [2.1.0] - 2026-09-28

### Added

- **`ResiliencePoliciesOptions.DisruptionRequestTimeout`.** Replaces `RequestTimeout` while an announced
  window is open, so a cap tight enough to fall through to another tier does not cut short a window the
  client is deliberately relaxing its own timeouts for. Resolved per operation through Polly's timeout generator
  rather than read at build time, so a window opening after startup is honoured. Left unset, the tier's own
  suggestion applies — `IDisruptionState.SuggestedTimeout`, which the Redis tier offers only for an announced
  push window, since the Azure probe route opens no native relaxation. The Azure route does not widen the cap
  either: its probe run lasts ten minutes whatever the maintenance did. Neither can shorten `RequestTimeout`, and
  with no `IDisruptionState` registered one timeout applies throughout.

- `IDisruptionState`, the two-member seam the pipeline asks whether the tier behind it is disrupted and what it is
  relaxing to. Without a registration, behaviour is unchanged.

- **Maintenance notifications from either source.** `RedisPlannedMaintenance` recognised only
  `AzureMaintenanceEvent`, the pub/sub notifications Azure Cache for Redis publishes, and returned on anything else —
  so the RESP3 push notifications Redis Enterprise and Redis Cloud send as `PushMaintenanceEvent` were discarded —
  as will Azure Managed Redis be, once its rollout lands and the option is set for it. Both are handled now, and a source neither of them models is recorded from
  the base event rather than dropped, so a provider added upstream is visible rather than silent.
  `RedisConnectionOptions.MaintenanceNotifications` opts in, defaulting to the client's own behaviour. It takes
  `RedisMaintenanceNotifications`, a neutral enum of this library's own, because the StackExchange type is marked
  experimental and would otherwise raise `SER010` in every consumer that set the option. A value outside the
  enum is refused with an `InvalidOperationException` — it arrives from configuration, not as an argument. Notifications
  arrive from the connection carrying commands, through the new `IRedisConnector.ServerMaintenance`, and one can be
  delivered more than once -- Azure's is a broadcast a retiring connection still forwards alongside its replacement,
  and a push frame is replayed to a connection that reconnects. A copy matching one recorded in
  the retention window is therefore dropped, on the notification's own identity rather than on which connection
  ought to have had it. The retention is the maximum window plus its post-event tail, floored at 30 seconds --
  see the fix below for why a fixed interval was not enough. A push notification opens a maintenance window rather than probing — the client hands the
  connection off itself, and probing would force a reconnect against it — for as long as the server announced,
  clamped to the range the client relaxes its own timeouts over (`MaintenanceRelaxedTimeout` to
  `MaintenanceRelaxedWindowMax`) and held through `MaintenancePostEventRelaxedDuration` after a completion, so the
  two agree by construction; and tracked per operation family, so one completion cannot close another's window. The
  bounds are read from the connection that delivered the notification, configurators included, so a window is sized
  right from the first notice. The
  two routes report one pair of `Redis.MaintenanceStarted`/`Ended` events between them rather than one pair each,
  since they can overlap.
- `IRedisConnector.ServerMaintenance`, the maintenance the server announced on the connection carrying commands.
  Defaulted to never raising, so an existing implementer is unaffected. It exists because the connector is what
  rebuilds that connection, so one subscription here survives a `ForceReconnect` where subscribing to the
  multiplexer directly would not.
- `IRedisConnector.GetPrimaries()`, the connected primaries a server-scoped command such as `SCAN` has to be sent to
  one by one. Defaulted to an empty sequence, so an existing implementer neither breaks nor changes behaviour.

### Changed

- **Dependency floors.** What a consumer's restore has to satisfy, taken from `Directory.Packages.props`:

  | Package | 2.0.1 | 2.1.0 |
  |---|---|---|
  | `StackExchange.Redis` (`UiPath.Caching`, `UiPath.Caching.Queue`) | 3.2.1 | 3.3.0 |
  | `AsyncKeyedLock` (`UiPath.Caching`) | 8.0.2 | 8.1.2 |

  The `Microsoft.Extensions.*` and Polly groups are unchanged. The `OpenTelemetry` packages also moved, but only tests
  and samples reference them, so they are pins of this repository, not floors on consumers.

- **`RedisConnectionOptions.ConnectionFactory` is asynchronous.** It is now
  `Func<ConfigurationOptions, CancellationToken, ValueTask<IConnectionMultiplexer>>` and is awaited. The synchronous
  `Func<ConfigurationOptions, IConnectionMultiplexer>` was called from inside the async connect, so a factory that
  needed async work, such as acquiring a token or retrying `ConnectAsync`, had to block a thread-pool thread for
  the whole attempt. The token is the connector's lifetime: one connection serves every caller, so it is cancelled
  when the connector is disposed rather than by any one caller. **Breaking**: a factory assigned in code no longer
  compiles; use an async lambda, for example
  `async (options, token) => await ConnectionMultiplexer.ConnectAsync(options)`. For Entra authentication,
  `AddAzureEntraAuthentication()` needs no factory at all.

- **The read pipeline now returns at `RequestTimeout`.** Polly's timeout is cooperative: it bounds a callback that
  observes the token it arms, and the Redis client takes no cancellation token, so on those paths `RequestTimeout`
  bounded nothing and the retry, breaker and fallback downstream never saw a failure to act on. The read pipeline
  now races the callback against that token, which carries the request timeout and the caller's own token alike,
  so control returns to the caller when either fires. This is fixed rather than configurable, and only the read
  pipeline does it: a write can carry memory the caller reclaims on return, and a custom pipeline can guard a
  destructive read such as `SPOP`. The abandoned call still runs underneath and its later failure is observed.
  A timeout is no longer retried, since the abandoned command is still in flight, and a caller's own cancellation
  no longer counts toward the circuit breaker. **This changes caller-visible timing on reads:** an operation that
  previously ran past `RequestTimeout` now returns at it.

- **`RedisConnectionOptions.FailFastBacklogPolicy` now defaults to `true`.** Queuing while disconnected makes a
  command wait out the connection's timeout with nothing to wait for. Set `false` to restore
  `BacklogPolicy.Default`. **This changes behaviour for disconnected commands**: they now fail fast rather than
  queue. `null` no longer means the library default: it fails fast like `true`, and so does a JSON `null`, as the
  shipped `appsettings.all.json` had it, whether the binder leaves the new default or writes `null`. Only `false`
  restores queuing. The Azure maintenance probe allows for this: a probe rejected while the client is reconnecting
  on its own no longer forces a reconnect, unless the disconnect outlasts the ten-second hanging time. That holds on a
  cluster where only the probe key's node is away, when the connection as a whole still reports connected, and each
  probe run counts the disconnect afresh. The ten-minute run is measured on the injected `TimeProvider`.


- **A departed cluster node is dropped in about a minute rather than six.** `StaleEndpointThreshold` defaults to one
  minute instead of five, and a reconfiguration the client reports through `ConfigurationChanged` runs the scan five
  seconds later without waiting the threshold out. Neither loosens what decides a rebuild: the node must still be
  absent from, or flagged failed in, the topology the client refreshes. That check is the only gate other clients
  use — Lettuce and ioredis close connections to nodes the refreshed topology no longer lists — and cloud guidance
  for them refreshes every 5 to 60 seconds. A burst of reconfigurations is judged once, and the scan's own refresh,
  which the client reports later on a worker, is not taken for a change.

- **A maintenance handoff is no longer reported as a connection failure.** When the client moves off an endpoint the
  server said is going away, it raises `ConnectionFailed` with `ConnectionFailureType.MaintenanceHandoff`. That was
  tracked as `Redis.ConnectionFailed` — by `RedisConnector` and again by `ConnectionStateMonitor` — which would
  alert on exactly the event advance notice exists to make uneventful. Both now track it as
  `Redis.MaintenanceHandoff`. The event is still raised to subscribers — the connection did drop — and only the
  telemetry name distinguishes them.
- `RedisHealthCheck` reports "Redis maintenance in progress" rather than naming Azure Cache for Redis, now that
  either route can open the window.
- Bumped `StackExchange.Redis` from 3.2.1 to 3.3.0. No public API change here, and nothing in this repository calls
  an API 3.2.15 or 3.3.0 altered. Three things in the range are worth knowing:
  - `SwitchPrimary` now retires the servers its rebuild drops (upstream #3225). They previously stayed in the server
    snapshot — connected and selectable — and the snapshot is what `IConnectionMultiplexer.GetServers()` reports, so
    on a Sentinel-managed connection `IRedisConnector.GetPrimaries()` could hand the stream maintainer a node no
    longer part of the service, and occasionally the same address twice.
  - `IServer.Execute` supplies the configured default database rather than refusing a database-specific command
    (upstream #3237). The maintainer passes the database explicitly regardless, so it does not depend on this.
  - Server-native maintenance notifications arrive as opt-in, through `maintNotifications` on the connection string
    or `ConfigurationOptions.MaintenanceNotifications`. Surfacing them is a behavioural decision rather than part of
    a version bump, and is covered by the `MaintenanceNotifications` entry under **Added** above.
- Dropped the `SER007` suppression in `RedisStreamSubjectWriterTests`. `RedisErrorKind` is no longer marked
  `[Experimental]` in 3.3.0, so the pragma suppressed a diagnostic that is no longer raised.

### Removed

- **`RedisPlannedMaintenance`'s constructors.** It takes `(ICachingTelemetryProvider, IRedisConnector, TimeProvider)`
  now; the configuration provider, multiplexer factory, logger, options and configurators went with the connection
  they built. Resolving it from the container is unaffected. **Binary-breaking** for code that constructs it directly.
- **`RedisConnectionOptions.PlannedMaintenanceConnectionRetryCount` and `PlannedMaintenanceConnectionRetryDelay`**,
  shipped in 1.1.0. They tuned the connection planned maintenance no longer opens. Configuration that still sets them
  binds without error and is ignored; code that sets them no longer compiles.

### Fixed

- **Planned maintenance kept a second Redis connection that retried departed nodes forever.**
  `RedisPlannedMaintenance` opened a multiplexer of its own at startup to listen for Azure's maintenance broadcast.
  When a cluster moves a shard to another node, every connection keeps the node that left, and StackExchange.Redis
  retries it for the life of the process, logging each refused handshake as an error. The stale-endpoint scan
  rebuilds only the connector's connection, so this one went on retrying each departed node every twenty seconds
  until the process restarted. Notices now come only through `IRedisConnector.ServerMaintenance`, from the
  connection carrying commands, which already delivered Azure's broadcast as well as push frames, so the rebuild
  covers everything the service listens on. It opens no connection, so with `WarmUpOnStart` off the first
  connection, and any Entra token, waits for the first cache use again. A custom `IRedisConnector` that leaves
  `ServerMaintenance` at its never-raising default now delivers no notices at all.

- **No task the library lets go of can surface as an unobserved exception.** Commands queued on a transaction,
  background loops, fire-and-forget work and probes abandoned at their timeout were discarded without anything
  observing a later failure, which `TaskScheduler.UnobservedTaskException` then reported. Each now observes its own.

- **A refused `Redis.Maintenance` record no longer releases the notification's deduplication claim.**
  Recording a notification also opens and closes health windows, so releasing the claim when the record
  failed let a replayed frame apply the same state change twice — a second operation no single completion
  closes, or a close while maintenance is still running. The record is guarded instead: a refused one is
  reported and dropped, and the claim stands. Losing an event is the lesser harm.
- **`MaintenanceRelaxedWindowMax` caps a larger `MaintenanceRelaxedTimeout`.** The floor was applied after
  the maximum, so an announcement below the floor returned the floor even when it exceeded the documented
  cap, and health suppression outlived the window the client actually relaxes over.
- **A replayed push frame could open a second operation or close someone else's.** The deduplication claim
  was retained for a fixed 30 seconds while an announced window can run far longer, so a frame replayed to a
  reconnecting connection after the claim lapsed was taken as new: a starter appended an operation the one
  real completion could not close, and a completion removed another operation still running. The retention
  now covers `MaintenanceRelaxedWindowMax` plus `MaintenancePostEventRelaxedDuration`, the full span over which
  a window can still be holding the state, whenever that exceeds the 30-second floor.
- **An explicit `maintRelaxedTimeout` in the connection string is no longer derived over.** With the typed
  option unset, the derived value replaced whatever the string supplied — while `maintRelaxedWindowMax` in
  the same string was preserved. A value from either input now counts as configured, detected by the key's
  presence rather than its value — an explicit setting that happens to equal the client's default is still
  a setting.

- **A throwing subscriber cost the remaining ones their notification.** `RedisConnector` and
  `ConnectionStateMonitor` raised their connection events with a plain multicast invoke, which stops at the first
  handler that throws; `ForceReconnect` caught the exception, but around the whole invocation list rather than around
  each handler, so the outcome was the same. Reconnection is how the pub/sub writer, the stream notify channel and the
  stream subject writer re-subscribe, and the pub/sub writer's handler can throw `ObjectDisposedException` when a
  dispose races the notification — one such throw left every subscriber after it in the list detached until the next
  reconnect. Handlers are now invoked one at a time with per-handler tracking, on every event of both types. This also
  covers the new `ServerMaintenance`, which the client raises on its own dispatch thread and which had no guard at
  all, and `ConnectionStateMonitor`, whose own re-multicast to `IConnectionState` subscribers sits inside the
  connector's — so a throw from one of those aborted the connector's list as well. Reporting the
  caught failure is itself guarded, since a sink that cannot take the report would otherwise put the exception
  back on the path the boundary exists to keep clear.

- **A notification whose recording threw was suppressed for the next 30 seconds.** `RedisPlannedMaintenance`
  claimed a notification's identity in its deduplication set before handling it, so a handler that threw --
  starting the Azure probe loop, or emitting the `Redis.Maintenance` event -- recorded nothing while every
  matching copy was collapsed into a record that did not exist. The claim is still taken up front, so a
  concurrent copy from another connection still collapses, but it is committed to the retention queue only once
  the recording succeeded and released otherwise. It is timestamped on commit rather than on claim, so the
  queue stays ordered by expiry when two copies record concurrently. Cancelling the service is guarded the same
  way: `CancellationTokenSource.Cancel` runs its registrations inline, so it raises what a caller's callback
  threw, and that left `StopReacting` before its open windows had been closed. A probe run now marks
  itself started only once `Redis.MaintenanceStarted` has been emitted, so a run that could not announce its
  start no longer emits `Redis.MaintenanceEnded` against nothing, and clears `InProgress` ahead of the rest of
  its cleanup, since leaving it set would make the guard refuse every later run. Reporting a failed probe no
  longer precedes the `ForceReconnect` it must not cost, and neither interval announcement can end the run:
  a sink that refused `Redis.MaintenanceStarted` used to kill the worker before it reached the probe loop,
  so the Azure route stopped recovering at all.

- **A rejected connection candidate stayed the newest multiplexer.** `RedisConnector` marks each candidate as
  the incoming generation before its handlers go on, so a `MOVING` arriving before the swap is still taken.
  A candidate the swap then rejected — a disposal or a newer reconnect won the race — was disposed without
  clearing that marker, leaving a disposed multiplexer referenced until the next reconnect replaced it. The
  marker is now compare-and-cleared as that instance is disposed, before its handlers come off. The maintenance
  handler also closes over the connection it was attached to rather than reading the event's sender, since a
  composite multiplexer can attach it to its children and raise with a child, which belongs to no generation
  the connector knows about — a `MOVING` for the connection carrying commands would have been dropped. The
  raiser is still used, for the opposite question: a group attaches the handler to every member, so a `MOVING`
  is taken only when it came from the member currently carrying commands. `IConnectionGroup.ActiveMember` is
  public but `ConnectionGroupMember.Multiplexer` is not, so that link is read reflectively and the check is
  skipped whenever it cannot be made — a spare notice costs a record, a lost one costs the handoff.

- **A refused telemetry report could defeat the catch that made it.** Every `catch` in `RedisConnector` and
  `RedisPlannedMaintenance` exists so the surrounding work carries on, and each reported through the sink
  directly — so a sink that threw took the recovery with it. They report through the guarded helper now. The
  worst of them was `GetVersion`, the factory behind a `Lazy<Version>`: a `Lazy` caches what its factory threw,
  so `IRedisConnector.Version` would have thrown for the life of the process instead of falling back once.
  The same holds for recording an event where recovery follows it: a refused `Redis.ForcedReconnect` skipped
  both the `OnReconnected` multicast and disposal of the retired connection, and a refused
  `Redis.StaleEndpointDetected` or `Redis.HangDetected` skipped the `ForceReconnect` it exists to announce.
  Every record raised on StackExchange.Redis's own dispatch thread is guarded for the same reason, and
  `ConnectionStateMonitor` guards its records in one place, since each precedes both a state reset and a
  multicast to its own subscribers.

- **Azure notifications the client could not parse shared one identity.** Such a payload leaves every field at
  its default, `RawMessage` included, so the deduplication key was the same for all of them and two unrelated
  ones arriving within the retention window collapsed into one record. They are recorded individually now, the
  way a push frame whose sequence could not be read already was. A notification whose *type* is unrecognised
  but whose other fields parsed still has an identity and still collapses. `Redis.Maintenance` also records
  `ReceivedTimeUtc` and `StartTimeUtc` in the round-trip format on every route, rather than the general
  invariant pattern on two of the three, so a query over the field does not have to guess which route wrote it.

- **A consumer group with a stale last-delivered-id skipped its quarantine.** The maintainer quarantines such a group
  by recording the instant in a hash field and deleting it once `MaintainerQuarantineInterval` has passed — which is
  what `CheckEmptyStreamGroupAsync` does for a group with no consumers. The stale-last-delivered-id path wrote the
  same timestamp but never read it back, deleting on the field's mere presence. Two maintenance passes that overlap —
  which the distributed lock permits, since it expires on `MaintainerCheckInterval` rather than when the pass
  finishes — therefore collapsed the wait to nothing: one pass wrote the record, the other deleted the group on it in
  the same cycle. Even without an overlap the wait was `MaintainerCheckInterval` (30 min by default) rather than the
  documented `MaintainerQuarantineInterval` (1 h). Both paths now read the recorded instant through one helper, so a
  group is deleted only once it has actually served the interval. The timestamp is read from the primary, like every
  other command this maintainer issues: a replica that had not caught up could report one the primary had
  already deleted, and the group would be reaped without serving any wait at all.

- **Stream maintenance reached only one shard.** `RedisStreamHealthMaintainer` discovered streams with a keyless
  `SCAN` sent on `IDatabase`. A keyless command carries no slot for the client to route by, so it reaches whichever
  single server the multiplexer picks, and its cursor walks that server's keyspace alone. On a cluster every broadcast
  stream whose slot lived on another primary was never discovered, and so never trimmed to `MaintainerTrimInterval`,
  never group-reaped and never deleted — held only by the `MAXLEN` each `XADD` carries, which leaves the last
  `MaxLength` entries (32,768 by default) in place of the intended window, on a key that has no TTL and so is not
  reclaimable under a `volatile-*` eviction policy. The maintainer now scans every connected primary, each with its own
  cursor, each scan carrying the configured database so it reaches the same keyspace the routed one did. The scans run
  concurrently, so a pass costs the slowest shard rather than the sum of them, and their results are merged as a set,
  so a slot caught mid-migration on both its source and its target primary is still maintained once. A connector that
  reports no primary keeps the previous single-server scan, and a primary that cannot be reached costs only its own
  shard that cycle instead of aborting the pass. Present since the maintainer was first added and invisible on a
  single-shard server, which is why it went unnoticed — and because the slot-to-shard mapping moves when a cluster is
  resized, which streams went unmaintained moved with it.
## [2.0.1] - 2026-09-21

### Changed

- **Dependency floors.** What a consumer's restore has to satisfy, taken from `Directory.Packages.props`:

  | Package | 2.0.0 | 2.0.1 |
  |---|---|---|
  | `StackExchange.Redis` (`UiPath.Caching`, `UiPath.Caching.Queue`) | 3.1.31 | 3.2.1 |
  | `Polly.Core`, `Polly.Extensions` (`UiPath.Caching.Polly`) | 8.7.0 | 8.8.0 |

  The `Microsoft.Extensions.*` groups are unchanged. `Aspire.Hosting.Redis`, `Microsoft.Testing.Extensions.CodeCoverage`
  and `OpenTelemetry.Instrumentation.StackExchangeRedis` also moved, but only tests and samples reference them, so they
  are pins of this repository, not floors on consumers.

### Fixed

- **The cluster slot check honors `RedisCacheOptions.KeyPrefix`.** An `IRedisConnector` whose `IDatabase` is wrapped
  with `WithKeyPrefix` prepends that prefix to every command, so the key the server hashes is the prefixed one — but
  the check hashed the composed key without it. An application whose prefix carries the hash tag, which is how a
  deployment keeps its whole keyspace on one slot, had every multi-key batch refused with `CrossSlotKeysException`
  although Redis would have run it: found on an Azure Managed Redis (OSS cluster policy) where a `{tag}:` prefix turned
  `GetCacheEntriesAsync` into a 500. The check now hashes the key as it is sent. Nothing changes when the prefix is
  empty, and a batch that genuinely spans slots is still refused.

## [2.0.0] - 2026-09-15

### Added

- **Named caching stacks: a second Redis connection.** `services.AddNamedCaching(name, configuration, chain)` registers a
  complete second caching stack on its own Redis server: `ICache`, `IHashCache`, `ICache<T>`, `IHashCache<T>`,
  `IDistributedLock`, `ILocalLock`, broadcast topics, warm-up and planned maintenance, all again, reached through keyed
  services under the stack's name (`[FromKeyedServices(name)]`), the stack's `ICacheKeyStrategy` and
  `ICachePolicyFactory` included, so its typed caches address the same keys as its unkeyed ones. The stack reads the same `Caching` section as the
  primary, so every cache option is shared, and its connection is `Connections:{name}` laid over `Connections:Redis`, so
  a connection key it leaves unset is inherited. It has its own L1 memory caches, locks and hosted services; logging,
  telemetry, the clock and key masking are shared with the application. `INamedCaching.Expose<T>()` reaches anything
  else the chain registers; the Queue package's `ExposeQueueCaches()` adds `ISetCache`, `ISetCache<T>` and
  `IQueueCacheFactory`. Registration refuses a stack with no `ConnectionString` of its own unless `configureConnection`
  supplies one, the name `Redis`, and a duplicate name; first resolution refuses a chain that adds its own connection, a
  connection that resolves to nothing or to the application's primary one, and a `KeyCasing` that differs from the
  application's, wherever it was set. Recipe:
  [docs/recipes/second-redis-connection.md](docs/recipes/second-redis-connection.md). The sample app runs two stacks
  against two Redis containers provisioned by the Aspire host.

- **Key masking in logs (#129).** A cache key is the caller's, so it can be a secret; the library names keys in log
  lines throughout. `builder.AddKeyMasking(prefixes)` turns masking on, and `AddKeyMasking<TPolicy>()` takes an
  `IKeyMaskingPolicy` of your own, which is asked whether one key is secret and is given the key, what the cache holds
  when the site knows it, and which provider is logging. A masked key keeps its first three characters, so two lines
  about one entry still correlate; a key of three characters or fewer keeps none. Whatever the key strategy composed around the key stays readable, and a key that is
  plainly an identifier, a number or a GUID, is left alone. Off by default: the container resolves
  `NullKeyMaskingPolicy` and nothing changes. Nothing is rendered, masked, or even converted to text unless the line is
  actually written, so a disabled level costs nothing. It reaches every component that names a key, including the
  multilayer local setter, the rehydration coordinator, the broadcast change token and the event publisher, not only
  the caches. `AddDistributedCache` is not subject to the application's policy: `IDistributedCache` keys belong to the
  consumer, so the adapter and the private tiers it configures always mask.

- **`IDistributedCache` adapter.** `builder.AddDistributedCache(providerName)` registers a
  `Microsoft.Extensions.Caching.Distributed.IDistributedCache` backed by this library's pipeline —
  shared Redis connection, resilience, telemetry — so consumers that require it (ASP.NET Core session
  state, OpenIddict, DataProtection, rate limiters, and `HybridCache` as an L2) stop running a second,
  parallel Redis stack. The backing tier is a required argument
  (`Redis` recommended, `InMemoryRedis`, or `InMemory`); full absolute + sliding expiration and
  `Refresh` semantics are supported. Keys are **always case-sensitive**, independent of
  `CacheOptions.KeyCasing`, because `IDistributedCache` keys are opaque and case-significant.
  Entries are stored as a Redis hash (`data`, `absexp`, `sldexp` — the conventional layout for this
  contract) in a keyspace disjoint from the application's own caches, so `Refresh` reads
  only the expiration metadata rather than transferring the payload. Keyspace separation is
  configurable at both levels: `CacheKeyStrategy` prefixes the `CacheKey` itself (`d:` by default) so a
  distributed entry cannot be reached through the application's own `ICache`/`IHashCache` with the bare
  caller key, and `RedisKeyDifferentiator` plus `RedisKeyStrategyFactory` separate the physical Redis
  keyspace (`dh` by default, inheriting the application's factory). A differentiator matching one of the
  application's own reserved keyspaces is rejected at registration, and because a differentiator only
  separates anything if the factory honors it, resolving the cache also proves the composed key differs
  from what the application's caches would produce. Configurable via
  `UiPathDistributedCacheOptions`: `PolicyName`, `DefaultEntryExpiration`, and
  `AllowUnboundedEntries`. Writes with no caller expiration take a bounded default rather than living
  forever in shared storage; an unset default resolves to `CachePolicy.DefaultDistributedExpiration`,
  and only `AllowUnboundedEntries` reaches "until removed".
- **`IBufferDistributedCache`** (`net9.0`+). The adapter registered by `AddDistributedCache` also
  implements the buffer-based half of the `IDistributedCache` contract, so a caller reads into an
  `IBufferWriter<byte>` and writes from a `ReadOnlySequence<byte>` instead of trading a fresh array per
  operation — `HybridCache` as an L2, output caching and session state all take that path once they find
  it. Discovery is the framework's own: a type check on the resolved `IDistributedCache`, so nothing
  extra is registered and no consumer code changes. Both halves share one read and write path — same
  keyspace, expiration, sliding and stored fields — so an entry written through either is readable
  through the other and by any other client on the conventional layout. `TryGet` reports hit and payload
  separately, the one thing the array half cannot express: an entry stored empty is a hit that writes no
  bytes, where `Get` returns an empty array for that and for a miss alike. What happens to the caller's
  buffer on a write depends on the tier: on `Redis` a single-segment sequence is handed to the connection
  as the caller's own memory — no array in between — and a segmented one is flattened into a rented
  buffer that is returned once the write has been awaited; the memory tiers keep what they are handed, so
  there the sequence is copied into an owned array. Either way a pooled buffer is safe to reuse the moment
  the call returns. The adapter's values travel as `ReadOnlyMemory<byte>` throughout, and its expiration
  fields are parsed and formatted straight from UTF-8 with no intermediate string.
  `NullDistributedCache` implements it too, so switching caching off does not change which half a
  consumer discovers. Not present on `net8.0`: that floor pins
  `Microsoft.Extensions.Caching.Abstractions` to 8.0.0, which predates the interface, and the type check
  simply comes up empty there.
- **`IMemorySerializerProxy`**, an optional refinement of `ISerializerProxy<byte[]>` for a serializer that
  can hand back memory without materializing an array: `ReadOnlyMemory<byte> SerializeToMemory<T>(T? value)`.
  `SystemJsonByteSerializerProxy` implements it as its array output wrapped — a byte payload is still
  base64 inside JSON, so the wire format is one thing whichever member a tier calls — and
  `RawByteSerializerProxy` passes a `ReadOnlyMemory<byte>` or `Memory<byte>` through untouched. `RedisCache`
  and `RedisHashCache` ask for memory when the configured serializer offers it. The memory is borrowed: it
  may alias the value it came from and is valid only until the operation completes, which the Redis tier
  honors by copying it into the connection's buffer as the command is written and awaiting the write to
  completion. That last part is a contract on `IResiliencePipeline` too — its `ExecuteAsync` must not
  complete before the callback has — and the Polly pipeline this library ships awaits throughout.
- **`ReadOnlyMemory<byte>` is a cacheable value type.** `NotCacheableException` admits it alongside classes
  and nullable structs: its default is empty memory, which the storage tiers already read as absent, so a
  miss has something to return. The raw serializer had special-cased the type in both directions without
  any cache being able to reach it. Like a zero-length payload, an empty `ReadOnlyMemory<byte>` value is
  indistinguishable from absent. `Memory<byte>` stays out — a cache must not hand back a window it can be
  written through.
- **Topic keys are computed once per type.** `DefaultTopicKeyStrategy` memoizes the key it derives from a
  value type. It runs on every operation of the memory-backed tiers, and for a generic value type — a
  `List<T>`, and now the distributed adapter's `ReadOnlyMemory<byte>` — the friendly name was rebuilt with a
  `StringBuilder` each time, about 260 bytes per call; a non-generic type's name was a cached lookup, so the
  cost went unnoticed. The keys themselves are unchanged.
- **Benchmarks.** `DistributedCacheBenchmark` covers both halves of the contract per tier and payload size
  with allocation columns; the harness now finds the sample's `appsettings.json` under `samples/`, which had
  moved from under it.
- **`CacheOptions.KeyCasing`** selects how keys built without an explicit mode are normalized —
  `Insensitive` (trim + lowercase, the historical behavior and the default) or `Sensitive` (preserve
  the caller's casing). `CacheKey` gained a `(string?, CacheKeyCasing)` constructor, a `Casing`
  property, and `WithName` so key transformations preserve the mode. Changing the app-wide setting
  relocates every cache key, so existing entries are rewritten under the new spelling.
- **`CacheKeyComparer`** exposes cached `Sensitive` / `Insensitive` equality comparers over
  `CacheKey`, in the `StringComparer` shape.
- **`ISerializerProxy<byte[]>`** is now the library's only serialization seam, replacing
  `ISerializerProxy<RedisValue>` everywhere — see **Removed** below. The serialization contract no
  longer names a Redis type, so a custom serializer needs no dependency on StackExchange.Redis, and
  one registration covers `ICache`, `IHashCache` and `ISetCache`. Swapping in a binary serializer
  such as MessagePack is one class and one registration — see
  [how-to/extending.md](docs/how-to/extending.md). Two implementations ship:
  - `SystemJsonByteSerializerProxy` (the default) is UTF-8 JSON for every value, which is
    byte-for-byte the format `SystemJsonSerializerProxy` wrote — **no cache entry needs migrating**,
    byte payloads and nulls included.
  - `RawByteSerializerProxy` stores byte payloads verbatim — no base64, no JSON, no encoding layer —
    and JSON for every other type. `AddDistributedCache` constructs this for the provider it builds
    rather than resolving one from DI, because an `IDistributedCache` payload is caller bytes the
    caller has already serialized and re-encoding them would be both wasteful and wrong; that also
    means replacing the app-wide serializer does not change what the adapter stores. Registering it
    app-wide is available as an opt-in, and *is* a wire-format change: it returns stored bytes as-is
    rather than base64-decoding them, so relocate the keyspace (a version segment in `AppShortName`
    or the cache-key strategy) when switching an existing deployment over.
- **`RedisCacheOptions.AwaitRefresh`** (default `false`) waits for the server to apply a refresh instead of
  sending `KEYEXPIRE`/`PERSIST` fire-and-forget. Off by default, which keeps the round trip off the
  sliding-expiration path — it runs on every read of a sliding entry — and preserves existing behavior.
  `AddDistributedCache` enables it on the private provider it builds, because a silently dropped refresh
  shortens a session and fire-and-forget cannot report one. Honored by both `RedisCache` and
  `RedisHashCache`.
- **Conditional add: `ICache.TryAddAsync` / `ICache<T>.TryAddAsync` (+ blocking `ICache<T>.TryAdd`).**
  Writes a key only if it does not already exist, returning `true` only to the caller that created it.
  On Redis-backed caches this is StackExchange.Redis `When.NotExists` — `SET key value EX … NX` — a
  single atomic round-trip with the TTL applied by the same command, so exactly one caller across all
  nodes wins a given key and a won key is never briefly immortal. Intended for at-most-once semantics
  keyed by something: idempotency keys, dedup markers whose TTL is the dedup window, electing which
  replica runs a job. `false` deliberately conflates "the key already existed" with "the write could
  not be completed" (disconnected, threw, or a `null`/`default` value the cache cannot represent) —
  fail-closed, so a caller treating `true` as "I own this key" is never wrongly told it won; the same
  conflation `IDistributedLock.TryAcquireAsync` already documents. Unlike `SetAsync`, it never
  deletes: where `SetAsync` removes the key when handed a `null` with `CacheNullValues` off,
  `TryAddAsync` reports `false` and leaves the key untouched. Per tier: `RedisCache` issues the `NX`
  write; `MultilayerCache` runs one sequence for every provider — take the local lock, probe the
  local tier, ask the L2, populate L1 (plus an invalidation broadcast) on a win, best-effort. A
  broadcast or L1 failure never downgrades a real win to `false`, since that would strand the entry
  with no owner; with the L2 disconnected the call returns `false` rather than granting a local-only
  claim every node would also be granted (`SetAsync` degrades to a local write there). The probe is
  what bounds exclusion at one winner per process, and the only thing doing so on the `InMemory`
  provider, whose `NullCache` L2 tells every caller it added the key; it also reports a local hit as
  a loss without asking the L2, which is fail-closed but costs a win the L2 would have granted when
  the local copy outlived the shared one. The local lock is what makes that probe-then-write atomic,
  so a conditional add takes it regardless of `Lock.LocalLockEnabled` (which trades single-flight for
  throughput on `GetOrAddAsync` and must not be able to hand one key to two callers), and a caller
  that cannot acquire it within `Lock.LocalLockTimeout` is told it lost rather than proceeding
  unserialized. The L2 itself is only ever asked, never classified by type or provider name, so a
  provider a consumer registers participates on the same terms — with the corollary that an L2
  arbitrating in-process only is trusted as though it arbitrated for every sharer. On the `InMemory`
  provider no invalidation broadcast is published: `ChangeTokenFactory` accepts only `CacheRemoved`
  and `CacheRefreshed` there, so peers ignore `CacheSet` — deliberately, since each node's memory is
  the store rather than a copy of a shared one.
  An expiration that is not in the future reports `false` on every tier: the entry would be evicted on
  arrival, so a `true` would be handed to every later caller as well. `NullCache` returns `false` —
  the one member where it does not degrade to "caching is off, carry on", because it cannot complete
  the write (which is exactly what a fail-closed `false` means) and because `true` there would hand
  every caller a claim of exclusive ownership; it is reached by accident, being what
  `ICacheFactory.CreateCache` resolves to when the requested provider is absent or has
  `Enabled=false`. Added to both interfaces as **default interface methods**, so
  **required** members rather than default interface methods: a probe followed by a write is not
  atomic, and no fallback body could stand in for one without either voiding the guarantee or hiding
  which stores can arbitrate, so an implementation states what its own store can do. **This is
  source-breaking for hand-written `ICache` / `ICache<T>` implementations,** which must add the three
  overloads. `NullCache` returns `true`, as its `SetAsync` does — the null store retains nothing, so
  no key pre-exists and no caller loses; note that it provides no exclusion at all and is what
  `ICacheFactory.CreateCache` resolves to for an absent or disabled provider, so assert the provider
  you expect at startup when the `true` branch runs a side effect that must not repeat. `CacheExtensions` carries the token-positional overloads
  (`TryAddAsync(key, value, token)` and the `TimeSpan?`/`DateTimeOffset?` pairs), matching `SetAsync`.
  No multi-key overload: Redis has no atomic multi-key `NX`, and all-or-nothing versus per-key
  semantics would be a guess. This is a cache primitive, not a lock — no ownership token, no early
  release, and a later `SetAsync`/`RemoveAsync` ignores the claim; for a fencing token and explicit
  release use `IDistributedLock`. No conditional-add member was added to the hash surface, where `NX`
  is per-field (`HSETNX`) and a different shape.
- A cancellation raised while the `SET … NX` write is in flight now propagates instead of being
  reported as `false`, which would have claimed the key belongs to someone else — a fact the cancelled
  call never established. The write itself stays on the shared `Write` resilience pipeline: retries
  fire on exceptions only, and re-issuing `NX` is harmless, since an attempt whose reply was lost is
  refused by the key it just wrote and reports the same `false` the exception would have, while an
  attempt that never reached Redis is recovered as the `true` it should have been.
- **Stale cluster endpoint detection.** `RedisConnector` now rebuilds the multiplexer when a cluster
  node it discovered has left the cluster but is still being retried. StackExchange.Redis never forgets
  a server it has seen, so when a node is replaced — an Azure Managed Redis patch swaps the node VMs
  and retires their endpoints — the old address is retried and logged as an error every few seconds
  for the life of the process; only a fresh multiplexer re-reads the topology. Every
  `StaleEndpointScanInterval` (default 30 s) the connector notes which topology-discovered servers are
  disconnected; one that stays down for `StaleEndpointThreshold` (default 5 min) is checked against
  `CLUSTER NODES` on a connected server, and if it is no longer a member the connector emits
  `Redis.StaleEndpointDetected` and calls `ForceReconnect()`. Configured endpoints are never judged
  stale, a node that is down but still in the cluster is left to StackExchange.Redis, and a
  non-cluster server is ignored. Opt out with `EnableStaleEndpointDetection = false`. `RedisConnector`
  gains a constructor overload that takes a `TimeProvider`; the existing one keeps using `TimeProvider.System`.
  `RedisHealthCheck` reports the disconnected endpoints under
  `ConnectionMultiplexer.DisconnectedEndPoints`.

### Changed

- **A cache key that cannot carry a hash tag is refused rather than wrapped.** `ShardPrefixRedisKeyStrategy` used
  to wrap any key without a valid tag, but wrapping one that already holds a brace pairs the added `{` with it: the
  tag Redis reads is then only a prefix of the key, so unrelated keys quietly collapse onto one slot — `bla{}bla`
  wrapped to `{bla{}bla}` hashes as `bla{`. Such a key now throws `InvalidOperationException` naming the strategy,
  which is the rule `StreamSuffixShardedChannelStrategy` already applied to stream keys; both go through one helper
  now. `bla{bla` previously wrapped to a correct tag by accident and now throws as well, as does an empty key,
  which wrapped to `{}` — a brace pair Redis reads as no tag at all. Non-empty keys with no braces, and keys with
  a valid tag, are unaffected.

- **BREAKING: `RedisTypePrefixes` is now `RedisKeyspaces`.** The library called the same thing two
  names — the short segment between `AppShortName` and the cache key was a "type prefix" in the core
  and a "keyspace" in the reservation API that packages register against. It is a keyspace
  everywhere now: the class, the error messages, and the docs. Only the type rename is a break;
  `IReservedRedisKeyspace` ships for the first time in this release, so its `Keyspace` member had no
  earlier spelling to migrate from. The constant values are
  untouched (`"s"`, `"h"`, `"ps"`, `"st"`), so no Redis key changes and no data moves; only the
  identifiers do. Replace `RedisTypePrefixes` with `RedisKeyspaces` at the call site. "Prefix" now
  means only what it says: `UiPathDistributedCacheOptions.CacheKeyStrategy`'s key prefix,
  `PrefixStrategy`, and StackExchange's own `KeyPrefix`.
- **BREAKING:** an unset expiration no longer means "keep this forever". `CachePolicy` gained
  `DefaultDistributedExpiration` (1 hour), applied as the floor under
  `CachePolicy.DistributedExpiration` and the providers' `DefaultExpiration` on every write that
  carries no caller expiration. Previously the whole chain was nullable with nothing underneath it,
  so a provider whose `DefaultExpiration` was set to `null` — in code or bound from configuration —
  wrote entries with no TTL into shared storage. The 1 hour that four options classes each declared
  as a property initializer now comes from that one constant, so the value is stated once.
  Unbounded entries are still available and now have to be asked for: configure a lifetime of
  `TimeSpan.MaxValue`, which is what the providers already read as "no TTL" (`SET` with no TTL,
  `PERSIST` on refresh). `TimeProvider.ToDateTimeOffset` is the single place a duration becomes a deadline, and it
  saturates at `DateTimeOffset.MaxValue` rather than overflowing, so `TimeSpan.MaxValue` reaches the
  providers as the sentinel they read as "no TTL". The memory-only set tier and the rehydrate write
  each did that conversion themselves with a bare `Add` and threw on it; both go through the clock
  now.
  Jitter leaves that sentinel alone: jitter spreads an expiry that is coming, and clamping an
  unbounded lifetime would put the key back on the `EXPIRE` path.

  The floor is on the **write** paths only — `MultilayerCacheBase.ResolveWriteDuration`,
  `RedisCacheBase.PolicyDuration` / `GetExpiration` — deliberately not in the
  resolved default policy, so "nothing configured" stays observable as `null` and a *read* never
  consults a default: a key that genuinely has no TTL in Redis reports `DateTimeOffset.MaxValue`
  rather than a fabricated `now + default`.
  `MultilayerCacheBase.ResolveWriteDuration` returns `TimeSpan` rather than `TimeSpan?` as a result.
- **BREAKING:** `CacheClock` is gone (see **Removed**); the clock is `System.TimeProvider`. No default
  expiration is carried anywhere near the clock any more. Previously a configured `DefaultExpiration`
  was also filled in on **reads** — `GetCacheEntryAsync` on a key with no TTL in Redis reported
  `now + DefaultExpiration`, while `ExpireTimeAsync` on the same key returned `null` — and the
  fabricated deadline doubled as a hidden L1 cap: the L1 copy of a no-TTL key turned over every
  `DefaultExpiration`. Reads now report `DateTimeOffset.MaxValue` for such a key, and the L1 copy
  lives until a broadcast invalidation or `LocalMaxExpiration` evicts it, which is what that knob is
  for. Write paths are unaffected: every one resolves its lifetime before asking the clock.

  `GetOrAddAsync` resolves the lifetime **once** and uses it for both the write deadline and the
  rehydrate threshold. These were two separate chains, and the floor would otherwise have applied to
  only one of them: entries written under the default would expire after an hour while proactive
  rehydration, still bottoming out at `null`, never fired. Only the write is jittered: the rehydrate
  threshold and timeout are measured against the configured, unjittered lifetime, so a hit crosses
  the soft-TTL threshold at the same point on every read rather than against a fresh random draw.
  Rehydration is skipped for an unbounded lifetime, which has no deadline to pre-empt.
- **BREAKING:** every conversion from a duration to a deadline goes through the `TimeProvider`, so a
  per-call `TimeSpan.MaxValue` means "no TTL" on every surface. It already did on `RedisCache.SetAsync`
  (the driver maps it) and on the set caches, but `MultilayerCache.SetAsync`, `RefreshAsync` and
  `GetOrAddAsync`, `RedisCache.RefreshAsync`, and `RedisHashCache.GetOrAddAsync` / `RefreshAsync` /
  `SetAsync` each added the duration to the clock themselves and threw `ArgumentOutOfRangeException`
  from `DateTimeOffset.Add` instead; a `LocalMaxExpiration` of `TimeSpan.MaxValue` threw on every L1
  write. The sentinel now survives the other direction too: a per-call deadline of
  `DateTimeOffset.MaxValue` becomes a duration of `TimeSpan.MaxValue` rather than a finite eight
  thousand years, so the rehydrate guard recognizes it. With the downstream add saturating, the two
  pre-clamps that protected it are gone — `MultilayerCacheBase.ApplyJitter` no longer trims the
  jittered lifetime to the clock's remaining range (and takes one clock reading per write fewer), and
  the `IDistributedCache` adapter hands `TimeSpan.MaxValue` to the backing cache untouched.
  `ApplyJitter` is now `(TimeSpan, TimeSpan?) -> TimeSpan`; `RedisCacheBase.GetExpiration(TimeSpan)`
  is added.
- **BREAKING: one clock.** `System.TimeProvider` is the single source of "now". `AddCaching` registers
  `TimeProvider.System` unless the container already has a `TimeProvider`, and every provider, cache
  base class, helper, the profiler and the stream maintainer take it from DI. No library clock type
  remains: the BCL's own seam is the one, and any `TimeProvider` subclass drives it in tests. The per-options `Clock`
  properties are gone (see **Removed**): they let each cache carry its own clock while the
  `IMemoryCache` judging its deadlines ran on the DI-wide one, so a configured clock made L1 entries
  expire early or late against the ambient clock, and the memory-only set tier read
  `DateTimeOffset.UtcNow` directly and ignored a clock altogether. The conversion from a lifetime to
  an expiration is `TimeProviderExtensions.ToDateTimeOffset(...)` in the abstractions package, so a
  fake converts exactly like the real clock. To control time, register a `TimeProvider` before
  `AddCaching`; an `ISystemClock` registration is no longer consulted.
- **`IMultilayerSetCacheOptions`** is what `MultilayerSetCache` reads its settings from — the memory
  tier's knobs through `IMemoryCacheOptions`, plus `DefaultExpiration`, `LocalMaxExpiration`,
  `ConnectionMonitorEnabled`, `ConnectionMonitorPeriod`, `UseLocalOnlyWhenDisconnected` and
  `LocalMaxExpirationDisconnected` — instead of each setting as a constructor argument. Both queue
  options types implement it: `InMemoryRedisQueueCacheOptions` gains `DefaultExpiration`, which — like
  `InMemoryRedisCacheOptions.DefaultExpiration` — outranks the Redis tier's own default for sets written
  through the provider (`null`, the default, inherits as before), and `InMemoryQueueCacheOptions` gains
  the connection and local-cap settings for parity, which have nothing to observe on a provider with no
  backing tier and default to off.
- **BREAKING:** `AddDistributedCache` no longer fails at registration when no bounded default
  resolves, because that state no longer exists — an unset default now resolves to the floor. The
  `"would store entries without an expiration"` throw is removed; the non-positive check stays,
  since a value someone configured as zero or negative is still a real misconfiguration.
  `UiPathDistributedCacheOptions.AllowUnboundedEntries` keeps its meaning and is now the only way
  to reach an unbounded entry through that adapter without naming a lifetime.
- **BREAKING:** the per-call `expiration` is no longer nullable. Every write on `ICache`,
  `ICache<T>`, `IHashCache`, `IHashCache<T>`, `ISetCache`, `ISetCache<T>` and their extension
  surfaces takes `TimeSpan` / `DateTimeOffset` instead of `TimeSpan?` / `DateTimeOffset?`. The
  nullable was a redundant third state: each of these members already has a sibling overload with no
  `expiration` parameter, and passing `null` meant exactly the same thing as not passing it — fall
  back to `CachePolicy.DistributedExpiration`, then the provider's `DefaultExpiration`. It also made
  `cache.SetAsync(key, value, null)` ambiguous (`CS0121`) between the `TimeSpan?` and
  `DateTimeOffset?` overloads, which is why the forwarders had to spell out `(CachePolicy?)null`.
  Callers passing a real value are unaffected; a caller forwarding its own `TimeSpan?` now branches
  on it and calls the overload without an expiration for the null case. Nullability stays where it
  means *inherit* — `CachePolicy.LocalExpiration` / `DistributedExpiration`, the providers'
  `DefaultExpiration`, `HashCacheEntryOptions.ExpireTime` / `TimeToLive` — and on reads, where
  `TimeToLiveAsync` / `ExpireTimeAsync` still return `null` for a key with no TTL.
- **BREAKING:** a per-call `expiration` that cannot be honored is now rejected instead of silently
  absorbed. A `TimeSpan` that is not strictly positive, or a `DateTimeOffset` at or before the
  cache's current time, raises `ArgumentOutOfRangeException` (`ParamName` `"expiration"`) and nothing
  is written. Previously such a value was quietly treated as "no expiration" and, on `TryAddAsync`,
  answered `false` — the same answer as "somebody else holds the key". With the nullable gone there
  is no third state left to carry that meaning, so the argument is refused at the boundary. The new
  public `CacheExpiration` helper (`ThrowIfNotPositive`, `ThrowIfNotFuture`, `ToDuration`) holds the
  guard for out-of-tree implementations. `TimeSpan.MaxValue` and `DateTimeOffset.MaxValue` remain
  valid — they are how the providers spell "no TTL". `NullCache`, `NullHashCache` and `NullSetCache`
  read no argument at all and so enforce nothing, keeping "caching is off, carry on".
- **BREAKING:** `ICache.Compat.cs`, `IHashCache.Compat.cs` and `ISetCache.Compat.cs` are gone. The
  pre-`CachePolicy` convenience overloads they carried as default interface methods —
  `GetAsync<T>(key, token)`, `SetAsync<T>(key, value, expiration, token)`,
  `TryAddAsync<T>(key, value, token)`, `AddAsync<T>(key, item, token)`, `PopAsync<T>(key, token)` and
  the rest — now live as extension methods on the new `CacheExtensions`, `HashCacheExtensions` and
  `SetCacheExtensions` static classes in the same `UiPath.Caching` namespace (`SetCacheExtensions`
  ships in `UiPath.Caching.Queue`, alongside `ISetCache`). Production call sites are unchanged and need
  no edit; the interfaces shrink to just the policy-bearing members, so an implementation now has one
  member to write per operation instead of one plus an inherited forwarder it could accidentally
  override. **Moq tests that mock the short forms do break, and not at compile time:** Moq reads the
  `Setup`/`Verify` expression and refuses a call to a static method, so `GetAsync<T>(key, token)` or
  `SetAsync(key, value, expiration, token)` inside one throws `NotSupportedException` when the test
  runs. Re-target the setup at the member the extension forwards to, with `It.IsAny<CachePolicy>()` in
  the new slot, since the extension passes `null` there. NSubstitute is unaffected: it records the call
  the extension actually makes on the substitute, so `Returns` and `Received()` on the short form keep
  working. See [Upgrading to 2.0](docs/upgrade-to-2.0.md#tests-that-mock-icache).
- **BREAKING:** `ICacheOfT.Sync.cs`, `IHashCacheOfT.Sync.cs` and `ISetCacheOfT.Sync.cs` are gone the
  same way. The 59 blocking forwarders they carried as default interface methods — `Get`, `GetOrAdd`,
  `Set`, `TryAdd`, `Refresh`, `Remove`, `Contains`, `TimeToLive`, `ExpireTime`, the hash surface's
  `GetItem`, `GetCacheEntry`, `GetMetadata` and `SetMetadata`, and the set surface's `Add`, `Pop`,
  `Members`, `ContainsItem`, `Count`, `RemoveItem` and `RemoveItems` — now live on the new
  `CacheSyncExtensions`, `HashCacheSyncExtensions` and `SetCacheSyncExtensions` static classes
  (`SetCacheSyncExtensions` ships in `UiPath.Caching.Queue`). Each still blocks on the async member
  via `.AsTask().GetAwaiter().GetResult()`; nothing about the blocking behavior changed. `T` becomes a
  method type parameter inferred from the receiver, so call sites are unchanged, and they stay
  reachable through the concrete `Cache<T>` / `HashCache<T>` / `SetCache<T>` classes as well as the
  interfaces. `partial` comes off `ICache<T>`, `IHashCache<T>` and `ISetCache<T>`, which nothing else
  extends now, leaving all three as pure async contracts: an implementation writes only the members it
  actually implements, rather than inheriting blocking forwarders it could accidentally override.
- **BREAKING:** `CachePolicy? policy` is now a **required** parameter on every `ICache`,
  `IHashCache` and `ISetCache` member that takes one, along with the `expiration` / `setOption`
  parameters that precede it — the `= null` defaults are removed. `CacheExtensions` /
  `HashCacheExtensions` / `SetCacheExtensions` supply the short forms, so
  `cache.GetAsync<T>(key, token)` and `cache.SetAsync(key, value, expiration)` still
  compile; what no longer compiles is an interface call that relied on the defaults to skip the policy
  slot positionally. This is also what makes the extensions load-bearing rather than decorative: while
  the interface still declared `policy = null`, an applicable interface overload existed for every
  short call and instance members always beat extension members, so `cache.GetAsync<T>(key, token)`
  kept binding to the interface and the extension was never reached. With `policy` required, no
  interface overload is applicable to the short forms and there is exactly one way to spell "no
  policy". Implementations (`MultilayerCache`, `RedisCache`, their hash
  counterparts, `NullCache`, `NullHashCache`, `MultilayerSetCache`, `RedisSetCache`, `NullSetCache`)
  drop the defaults too, so behavior is identical whether the call goes through the interface or the
  concrete type. The typed `ICache<T>` / `IHashCache<T>` / `ISetCache<T>` façades are unchanged —
  they never had a `policy` parameter, since they resolve one at construction.
- **BREAKING:** `CacheKey.Equals` and `GetHashCode` are now ordinal rather than
  `InvariantCultureIgnoreCase`. Insensitive keys are still lowercased at construction, so the stored
  key and every comparison between insensitive keys are unchanged; what changes is that equality is
  now consistent with `GetHashCode` (the previous pairing could report two keys equal while hashing
  them into different buckets) and that keys built with `CacheKeyCasing.Sensitive` compare
  case-sensitively.
- **Dependency floors.** What a consumer's restore has to satisfy, taken from `Directory.Packages.props`
  rather than from memory — an earlier draft of this entry said 10.0.11 and left StackExchange.Redis out:

  | Package | 1.3.0 | 2.0.0 |
  |---|---|---|
  | `StackExchange.Redis` | 3.1.13 | 3.1.31 |
  | `Microsoft.Extensions.*` (`net10.0` group) | 10.0.10 | 10.0.12 |
  | `Microsoft.Extensions.Logging.Abstractions` (both TFMs) | 10.0.11 | 10.0.12 |

  The `net8.0` group is unchanged at 8.0.x. `UiPath.Caching.OpenTelemetry` references no OpenTelemetry
  package, so the 1.18.0 family this repository's samples moved to is a pin of the samples, not a floor
  on consumers; `OpenTelemetry.Instrumentation.StackExchangeRedis` only needs to be a version without an
  upper bound on StackExchange.Redis 3.x.
- **BREAKING (source, not data):** every cache now serializes through `ISerializerProxy<byte[]>`.
  `RedisCache`, `RedisHashCache`, `RedisSetCache`, the memory set tier and all four providers take
  `ISerializerProxy<byte[]>` in place of `ISerializerProxy<RedisValue>`, and the broadcast change
  token is built as `ChangeTokenFactory<byte[]>`. **No stored entry changes format and nothing needs
  migrating** — the default serializer emits exactly what `SystemJsonSerializerProxy` emitted.
  Consumers who pass these constructors a serializer directly, or who register a custom one, need a
  one-line type change; `AddCaching` now throws at startup if a leftover
  `ISerializerProxy<RedisValue>` registration is present, rather than ignoring it and silently
  falling back to JSON. The check runs whether or not `CacheOptions.Enabled` is set, so a disabled
  test profile reports the same leftover the production profile would.
- **Multi-key commands are checked against the Redis Cluster slot map before they run.** `GetAsync(CacheKey[])`, `GetCacheEntriesAsync`, the multi-key `SetAsync` and `RemoveAsync(CacheKey[])` each reach Redis as one command on one node, so a batch whose keys span slots is answered with an error, which the caches log and report as a miss: migrating an app onto a cluster turned a working batch read into a cache that never hits, with nothing thrown to say so. Each of those paths now compares the slots first and throws `CrossSlotKeysException`, naming the two keys that disagree and how to fix it. A non-clustered server maps every key to the same slot, so nothing changes off a cluster, and a disconnected cache skips the check and keeps answering from its own disconnected branch.
- **`ShardKeyEnabled` leaves a key that already carries a hash tag alone.** The shard strategy used
  to wrap every key in braces, so a caller who had placed its own `{tag}` in the key ended up with
  `app:s:{authz_{orgid}_groups_1}` — Redis hashes from the first `{` to the next `}`, so the tag was
  the accidental `authz_{orgid` rather than the org the caller asked for. A key with a valid hash tag
  (non-empty content between the first `{` and the next `}`) is now prefixed and left as-is, so the
  caller's tag picks the slot and multi-key commands land where the caller intended, given a brace-free prefix. Non-empty keys with no
  braces are wrapped exactly as before; keys whose braces form no valid tag were wrapped by this change too, and
  are refused outright by *A cache key that cannot carry a hash tag is refused rather than wrapped* later in this
  release. This relocates existing entries only for apps
  that run `ShardKeyEnabled: true` *and* put braces in their keys.

### Deprecated

- **`CacheOptions.ShardKeyEnabled`.** It wraps a brace-free cache key in a `{...}` hash tag so the slot follows
  the key rather than `AppShortName` and the differentiator. That changes *which* keys share a slot, not how
  evenly they spread: the tag becomes the whole key, unique per key just as the untagged key was, so it spreads no better, and
  it cannot make a multi-key batch land on one node. A key that already carries a valid `{tag}` is rendered
  identically on either setting, so the flag makes no difference to batching in either direction; a key whose
  braces form no valid tag is refused outright when it is set. What it does do is co-locate one key across every
  cache that renders through `DefaultRedisKeyStrategyFactory`, and across apps with a different `AppShortName` —
  neither of which anyone asked for. What does matter on a cluster is unconditional: a caller's own tag survives,
  and a cross-slot batch is refused with a message naming the two keys that disagree. The property is still read,
  because a deployment that set it would relocate every brace-free key if it stopped being honored; to batch
  across keys on a cluster, give the keys a shared hash tag yourself. All of this assumes `AppShortName`,
  `Separator` and the differentiator are brace-free, since braces there change which span Redis hashes.

### Removed

- **BREAKING:** `SystemJsonSerializerProxy` and the `ISerializerProxy<RedisValue>` registration.
  `SystemJsonByteSerializerProxy` (in `UiPath.Caching.Abstractions`) is the single default and writes
  the same bytes. The generic `ISerializerProxy<T>` interface itself is unchanged.
- **BREAKING:** `IEventFormatterProxy<T>.Decode(string)` and `EncodeAsString(T)`, both obsolete since
  the broadcast path moved to `ReadOnlyMemory<byte>` and both unused — nothing in the library called
  them and no implementation overrode the default bodies. Use `Decode(ReadOnlyMemory<byte>)` and
  `Encode(T)`.
- **BREAKING:** `CacheClock.DefaultDateTimeOffset()`, `CacheClock.ToTimeSpan(DateTimeOffset?)` and
  `CacheClock.ToTimeSpan(TimeSpan?)`. Nothing called them, and the first `ToTimeSpan` was another
  place a configured `TimeSpan.MaxValue` overflowed. `ToDateTimeOffset` is the conversion that remains,
  now as a `TimeProvider` extension.
- **BREAKING:** `CacheClock` itself. `System.TimeProvider` is the clock; nothing wrapped
  `Microsoft.Extensions.Internal.ISystemClock` any more, so the type had no job left.
- **BREAKING:** the per-options `Clock` properties: `ICacheOptions.Clock` and its implementations on
  `InMemoryCacheOptions`, `InMemoryRedisCacheOptions` and `RedisCacheOptions`, and
  `RedisConnectionOptions.Clock`. Time comes from the one `TimeProvider` in DI — see **Changed**. `MemoryCacheFactory`, `UiPathDistributedCache`, the provider constructors,
  `MultilayerCacheBase`, `RedisCacheBase`, `RedisProfiler` and `RedisStreamHealthMaintainer` take an
  `TimeProvider` instead of an `ISystemClock?` or nothing.
- **BREAKING:** the `[Obsolete]` options properties. The rename aliases `PrimaryMaxExpiration`,
  `PrimaryMaxExpirationDisconnected` and `UsePrimaryOnlyWhenDisconnected` on `IMultilayerCacheOptions`,
  `InMemoryCacheOptions` and `InMemoryRedisCacheOptions` — use `LocalMaxExpiration`,
  `LocalMaxExpirationDisconnected` and `UseLocalOnlyWhenDisconnected`, which they forwarded to — and
  `RedisConnectionOptions.ThreadPoolSocketManager`, a no-op since StackExchange.Redis 3.0. The binder
  skips a key it cannot place without a word, so a `PrimaryMaxExpiration` left in `appsettings.json`
  would not fail the build, startup or a log line — the L1 cap would simply be gone. The section-bound
  overloads (`AddMemory(sectionName)`, `AddInMemoryRedis(sectionName)`, `AddRedisConnection(sectionName)`
  and the `(sectionName, configure)` form) therefore **throw `InvalidOperationException` at registration**
  when one of the four removed keys is present under the section, naming the section, every offending key
  and its replacement. The check runs whether or not the provider is enabled, since a disabled profile is
  usually the non-production copy of one that is on elsewhere. The code-only overloads are not checked:
  the property no longer exists to assign.

### Fixed

- **The streams maintainer no longer depends on its quarantine hash sharing a Redis Cluster slot with the stream.**
  Two faults, one cause. The maintainer built its quarantine key by rendering a *prefix* through
  `IRedisKeyStrategy.GetRedisKey` and concatenating the stream key onto it, but that method renders a complete
  key: with `ShardKeyEnabled` the rendered prefix carried its own `{...}`, so every quarantine hash inherited one
  constant tag and all of them landed on a single slot. And `CheckEmptyStreamAsync` deleted the stream and its
  quarantine hash in one multi-key `KeyDelete`, which Redis Cluster answers with `CROSSSLOT` unless both keys hash
  to the same slot — so the empty-stream cleanup never completed on a cluster. The key is built in one call now,
  so it spreads with the stream key that names it, and the pair is deleted one command at a time. Co-locating them
  instead would not have been sound: the configured `IRedisKeyStrategy` and any connection key prefix both shape
  the key that reaches Redis, and either can put the two on different slots whatever the library composes. A
  trailing terminator keeps `CacheKey`'s trim — which runs even under sensitive casing — from collapsing two
  stream keys that differ only by surrounding whitespace. Quarantine entries written under the old key are
  orphaned. Most carry the TTL the maintainer attaches, `MaintainerQuarantineInterval` × 10, and expire on their
  own; one created after its stream's last expiry-attaching pass carries none — the transaction's empty `HashSet`
  creates no key for the adjacent `KeyExpire` to land on, and the per-group `HashSet` that does create the hash
  sets no expiry — and has to be deleted by hand.

- **Multi-key `RemoveAsync` honors its cancellation token again.** The `CacheKey[]` overload built
  each entry's options without the token, so an already-cancelled call did the work anyway and only
  noticed at the removal itself. It now forwards the token, matching the single-key overload. The
  rest of this change is build-warning cleanup with no behavior change: the periodic-timer loops in
  `CacheMemoryMonitor` and the stream health maintainer, and the planned-maintenance probe task, now
  say explicitly that they opt out of token propagation, each with the reason — disposal is what
  stops the loops, and the probe delegate must always run so its `finally` clears the in-progress
  flag. Test fakes moved off the obsolete `RedisServerException(string)` constructor.

- **Reserved Redis keyspaces are extensible across packages (#130).** `AddDistributedCache` used to
  compare `RedisKeyDifferentiator` against a hardcoded list of the core's Redis keyspaces, so a
  package layered on top — `UiPath.Caching.Queue`'s set cache on `"se"` — was invisible to it and a
  differentiator landing on those keys was accepted. Packages now declare the keyspaces they occupy
  through `IServiceCollection.ReserveRedisKeyspace` (`IReservedRedisKeyspace`); the core reserves its
  four and `AddQueueRedis` / `AddQueueInMemoryRedis` reserve `"se"`, whether or not that backing is
  enabled. `AddDistributedCache` reserves its own differentiator the same way, so one registry answers
  every case: the second party to claim a keyspace is rejected whichever it is, in either order, across
  separate `AddCaching` calls, and even when `CacheOptions.Enabled` is false — a collision is a
  configuration error rather than a runtime condition. Two packages claiming one keyspace is rejected
  too, where before the later reservation was silently dropped; a repeat by the same owner is still
  ignored. A keyspace, `RedisKeyDifferentiator` included, must be one segment of letters and digits, so
  none can span segments under a punctuation separator; `CacheOptions.Separator` may itself be
  alphanumeric, so one keyspace nesting inside another under the configured separator is rejected when the
  options resolve. The resolution-time probe that guards a custom `IRedisKeyStrategyFactory` enumerates the
  same reservations, skipping any the factory refuses to build a key for, and the default
  differentiator is checked too in case a package reserves it.
- **Stale endpoint detection works under the default `allowAdmin=false`.** The membership check
  issued `CLUSTER NODES`, which StackExchange.Redis refuses client-side unless admin mode is on, so on
  a default connection every scan recorded a `RedisCommandException` and never reconnected. The scan
  now refreshes the client's own cluster topology (`IConnectionMultiplexer.ConfigureAsync`, whose
  `CLUSTER NODES` is an internal call) and reads the `ClusterConfiguration` it records on the
  configured endpoints, judging only once the refresh has replaced it. When a refreshed node has none
  on three consecutive refreshes — a non-cluster server, or a Redis user that may not run
  `CLUSTER NODES` — the scan emits one `Redis.StaleEndpointScanDisabled` event and stops, instead of
  reporting the same failure every interval. A down node the topology still lists is re-checked only
  once per threshold and reported once through `Redis.StaleEndpointStillAMember`; a refresh that keeps
  throwing backs off exponentially, up to an hour between attempts.
- **`RefreshAsync` on Redis reported nothing.** Both Redis caches sent their standalone
  `KEYEXPIRE`/`PERSIST` fire-and-forget, so the call returned `false` whether the refresh succeeded, failed
  or the key did not exist, the server's reply was never seen — a rejected command was neither logged nor
  retried by the resilience pipeline — and telemetry recorded every refresh as unsuccessful. Setting
  `RedisCacheOptions.AwaitRefresh` makes the result meaningful; the default is unchanged, so existing
  callers see the previous behavior until they opt in. The fire-and-forget flags inside the write
  transactions are untouched: those replies are discarded, but the transaction itself is awaited.
- **`AddCaching(IConfigurationSection)` never bound `CacheOptions`.** The overload passed its options
  binder positionally, so it landed on the `configure` parameter and type-checked as
  `Action<ICachingBuilder>` — leaving `configureOptions` null, the section unread, and the config bound
  onto the builder object instead. Every `CacheOptions` value supplied through that entry point was
  silently ignored; `Enabled`, `AppShortName`, `KeyCasing` and the rest now take effect, which changes
  behavior for anyone who was unknowingly running on the defaults.
### Documentation

- New recipe [`conditional-add.md`](docs/recipes/conditional-add.md) — claiming a key exactly once with
  `TryAddAsync`, why check-then-set is not equivalent, and when to reach for `IDistributedLock`
  instead. `interfaces.md` documents the member on both cache surfaces with a per-provider table of
  who arbitrates and how far exclusion reaches; `concepts.md` places it next to the two lock
  abstractions, whose goal is reducing redundant work rather than a decision the caller can branch on.
  The recipe's worked example is a daily digest rather than a payment capture, and says why: a claim
  marker records that someone *started*, never that anyone finished, so an operation that must
  eventually happen needs a recorded outcome instead. Both documents also state plainly that the
  ambiguity in `false` is not recoverable — a serialization or command failure returns `false` with
  the connection snapshot still healthy — rather than pointing at `IConnectionState` as an escape
  hatch, and both explain why the `NX` write is safe to retry where `SPOP` is not.
- New `IConnectionState` section in `interfaces.md`, a public type that was documented nowhere (and
  that the `TryAddAsync` guidance linked to through an anchor resolving to `IDistributedLock`). It is
  described as what it is — a cache-health snapshot for probes, metrics and backoff — not as a way to
  explain a negative result.
- Corrected `interfaces.md`, which described the `IHashCache<T>.SetAsync(…, HashCacheEntryOptions, …)`
  overload as offering "conditional set, individual field TTL". `HashCacheEntryOptions` carries
  neither: `HashCacheSetOption` selects write *scope* (`HashReplace` merges fields, `KeyReplace` drops
  the key first), and there is no per-field TTL.

## [1.3.0] - 2026-08-19

### Added

- **Batch `GetOrAddAsync` with caller state.** `ICache`, `ICache<T>` and `Cache<T>` gained multi-key
  `GetOrAddAsync` overloads that pair each key with an opaque caller state (`TState`) — a database id,
  a request object, whatever identifies the entry in the caller's own vocabulary. The generator is
  invoked at most once on the calling path, receiving only the states of the entries that missed every
  cache layer, never the keys; results come back keyed by state, one entry per distinct requested
  state, in first-occurrence order. Cache operations de-duplicate by `CacheKey` (one probe, one write
  per distinct key); results de-duplicate by state — when two states share a key the generator is
  asked once, about the first state seen for that key, and the value is reported under both. A state
  the generator omits is returned as `default(T)` and left uncached, so the next call retries the
  source. A caller whose keys are their own identity pairs each key with
  itself (`TState = CacheKey`); the blocking `GetOrAdd` facade on `ICache<T>` accepts `CacheKey[]`
  directly and does that pairing for you. On `MultilayerCache` the generator call is guarded by
  a single composite lock derived from the missing key set, and proactive rehydration of hit keys is
  coalesced into one background call that locks per key, so overlapping aging sets on different
  nodes refresh each shared key once. Added to `ICache` as default interface methods, so existing
  `ICache` implementations keep compiling; the `ICache<T>` overloads are **abstract** — see the
  breaking note under **Changed**.
- **`InMemoryRedisCacheOptions.BroadcastEnable`** (default `true`) turns cross-node L1 invalidation off for the `InMemoryRedis` provider alone, so L1+L2 tiering can be used without broadcast traffic. Intended for single-node deployments, where cross-node invalidation buys nothing, and for Redis-compatible backends whose Streams support does not cover `XREADGROUP`. When `false` the provider gets `NullTopicFactory` / `NullChangeTokenFactory` / `NullCacheEventFactory`, mirroring what `InMemoryCacheProvider` already did for `InMemoryCacheOptions.BroadcastEnable`. The flag can only narrow `CacheOptions.BroadcastEnabled`, never widen it; note its default is the opposite of the `InMemory` equivalent, which is opt-in. ([#103](https://github.com/UiPath/dotnet-caching/issues/103))

### Changed

- **BREAKING:** `ICache<T>` gains three **abstract** multi-key `GetOrAddAsync` members. They could not
  ship as default interface methods: `ICache<T>` has no `GetCacheEntriesAsync`, so a default body
  could not tell a miss from a cached null. This is **source- and binary-breaking** for any
  hand-written `ICache<T>` implementation (test fakes, typically) — source-breaking because such a
  type no longer compiles until the members are added, and binary-breaking because an already-compiled
  assembly implementing `ICache<T>` throws `TypeLoadException` when loaded against the new
  `UiPath.Caching.Abstractions`, with no recompile involved. Consumers that pick the new abstractions
  up transitively must rebuild, not just restore. The `ICache` overloads are default interface
  methods, so `ICache` implementations are unaffected.

### Fixed

- The Redis Streams fetch loop no longer hammers a server that does not implement `XREADGROUP`. It previously re-issued the command and logged `Fetch events loop error` once per `PollInterval` (250 ms by default) forever. The unsupported-command case is now reported once at `Critical` with the available remedies, then retried every 30 s; a successful fetch lifts the quarantine and logs recovery, and a connection-restored/reconnected event wakes the loop early so recovery does not wait out the backoff. Recovery does not depend on those events: with the connection monitor off (the default) the timed retry still gets there. Other consecutive fetch failures now back off exponentially from `PollInterval` up to 30 s instead of retrying at the poll rate. ([#103](https://github.com/UiPath/dotnet-caching/issues/103))
- **`CacheOptions.BroadcastEnabled` now actually disables broadcast.** It was read only by `RedisStreamHealthMaintainer`, so setting it through the `AddCaching` options lambda stopped the maintainer while the Streams fetch loop kept running, despite sharing a name with the configuration key that does perform the real opt-out. `TopicFactory` now resolves every topic to `NullTopicProvider` and reports no provider names when the flag is `false`. ([#103](https://github.com/UiPath/dotnet-caching/issues/103))

### Documentation

- `concepts.md` now states that `AddInMemoryRedis()` wires broadcast internally and that a code-only `AddBroadcast(enabled: false)` does not stick, and links to the broadcast how-to. The how-to gains a section on disabling broadcast for `InMemoryRedis` alone and a troubleshooting entry for `ERR unknown command` against Redis-compatible backends. ([#103](https://github.com/UiPath/dotnet-caching/issues/103))

## [1.2.0] - 2026-08-04

### Added

- Set caches in the `UiPath.Caching.Queue` package gained **`InMemory` and `InMemoryRedis` backings** alongside Redis, via a provider model that mirrors the core cache. New per-backing registrations `AddQueueMemory` / `AddQueueRedis` / `AddQueueInMemoryRedis` on `ICachingBuilder` (each with a config-section overload binding the same `KnownCacheProviderNames` sections the core providers use); a new `IQueueCacheProvider` (one per backing — `InMemoryQueueCacheProvider` / `RedisQueueCacheProvider` / `InMemoryRedisQueueCacheProvider`); and `QueueCacheFactory` now selects a provider by name via `CacheOptions.DefaultCache` (or an explicit name, falling back to the default then `NullSetCache`), and is `IDisposable` with `AddProvider` — exactly like `ICacheFactory`. The `InMemoryRedis` backing is a multilayer set cache: a local in-process snapshot (L1) over the Redis set cache (L2), with the same connection-monitor / local-only-when-disconnected behavior as the core `InMemoryRedis` cache. New options `InMemoryQueueCacheOptions` / `InMemoryRedisQueueCacheOptions` (shared by all collection kinds of a backing, like `InMemoryCacheOptions`) and `RedisSetCacheOptions.Enabled`.

### Changed

- Bumped the OpenTelemetry package family from 1.16.0 to 1.17.0 (`OpenTelemetry`, `OpenTelemetry.Exporter.OpenTelemetryProtocol`, `OpenTelemetry.Exporter.Console`, `OpenTelemetry.Extensions.Hosting`, `OpenTelemetry.Instrumentation.AspNetCore`, `OpenTelemetry.Instrumentation.Http`, `OpenTelemetry.Instrumentation.Runtime`, and `OpenTelemetry.Instrumentation.StackExchangeRedis` 1.16.0-beta.1 → 1.17.0-beta.1). Bumped as one set so the family stays version-consistent.
- **BREAKING:** `SetCacheCollectionExtensions` and its `AddRedisSetCache` overloads (both `IServiceCollection` and `ICachingBuilder`) are removed. Register the Redis set cache via `AddCaching(... builder.AddQueueRedis())` (same `RedisSetCacheOptions`, same behavior). Registration now always goes through the `ICachingBuilder`, which guarantees the core caching services are present and honors the builder's `Enabled` flag. (`AddRedisSetCache` shipped only in 1.1.0.)
- **BREAKING:** the parameterless `IQueueCacheFactory.CreateSetCache()` / `QueueCacheFactory.CreateSetCache()` members and the parameterless `CreateSetCache<T>(this IQueueCacheFactory)` extension are removed in favor of a single `CreateSetCache(string? providerName = null)`, exactly like `ICacheFactory.CreateCache`. Source-compatible (`factory.CreateSetCache()` still compiles and means "default provider"), but binary-breaking — recompile against the new package. (Shipped only in 1.1.0.)
- **BREAKING:** the `QueueCacheFactory(ISetCache)` constructor is removed — the factory is provider-based only, like `CacheFactory`. To pin a fixed `ISetCache` (e.g. in tests), register it through a stub `IQueueCacheProvider` and point `CacheOptions.DefaultCache` at its name. (Shipped only in 1.1.0.)

## [1.1.0] - 2026-07-17

### Added

- `RedisCollectionExtensions.AddRedisConfigurationOptionsProvider(this ICachingBuilder, Func<IServiceProvider, IRedisConfigurationOptionsProvider>)` — registers a custom `IRedisConfigurationOptionsProvider` via a factory delegate, replacing the default `RedisConfigurationOptionsProvider`. Order-independent (uses `Replace`), so it can be called before or after `AddRedisConnection` and the supplied factory always wins. Previously a custom provider required manually pre-registering it so the `TryAdd` default backed off.

### Changed

- Bumped `StackExchange.Redis` from 2.10.1 to 2.13.17 (latest 2.x), the first step of the staged upgrade toward 3.0. No public API or runtime behavior change.
- Bumped `Microsoft.Extensions.Logging.Abstractions` on `net8.0` from 8.0.3 to 10.0.10 (matching the `net10.0` pin). Prerequisite for `StackExchange.Redis` 3.0, which requires `>= 10.0.5`; `net8.0` consumers now transitively resolve the 10.x abstractions package (compatible — it targets net8.0).
- Bumped `StackExchange.Redis` from 2.13.17 to 3.0.17. 3.0 is an internal IO-core rewrite that mirrors 2.13.17's public API, so there is no public API change here. The live hang-detection and profiling reflection paths were verified against a real Redis on both target frameworks.
- **Obsolete:** `RedisConnectionOptions.ThreadPoolSocketManager` no longer has any effect and is marked `[Obsolete]`. StackExchange.Redis 3.0 removed `SocketManager` (its IO core no longer uses one), so the setting is now a no-op; it will be removed in a future major release.

### Performance

- Serialization now stays on UTF-8 bytes end-to-end instead of round-tripping through UTF-16 `string`s. JSON is UTF-8 on the wire, so the string hop was a pure allocation + transcode on every read/write. Measured with BenchmarkDotNet (`SerializerBenchmark`), this roughly **halves allocations** on the serialize/deserialize paths (e.g. large-payload deserialize `1,385,629 B → 607,714 B`, serialize `778,689 B → 389,660 B`) with equal-or-better throughput. No public API changes.
  - `SystemJsonSerializerProxy` (the default proxy shared by `RedisCache`, `RedisHashCache`, `RedisSetCache`, `RedisCacheProvider`) serializes via `JsonSerializer.SerializeToUtf8Bytes` and deserializes from the `RedisValue` payload as `ReadOnlySpan<byte>`. The benchmark also measured a `ReadOnlySequence<byte>`/`Utf8JsonReader` variant; it allocated identically to the span path but was slower for these (single-segment) payloads, so the span path was chosen.
  - `CacheEventFormatter.Encode` uses `SerializeToUtf8Bytes` (dropping the intermediate string + `Encoding.UTF8.GetBytes`); the Redis broadcast publish paths (`RedisPubSubTopic`, `RedisStreamsTopic`) publish the encoded bytes as a `RedisValue` directly instead of via `EncodeAsString`; and the subscribe paths (`RedisPubSubSubjectWriter`, `RedisStreamSubjectWriter`) decode from the `RedisValue` bytes instead of `value.ToString()`.
  - Wire format is unchanged everywhere — values written by the previous string paths still deserialize.

### Deprecated

- `IEventFormatterProxy<T>.Decode(string)` and `IEventFormatterProxy<T>.EncodeAsString(T)` are marked `[Obsolete]`. They forced a UTF-16 transcode and are no longer used by the library; use the byte-based `Decode(ReadOnlyMemory<byte>)` / `Encode(T)` instead.

## [1.0.1] - 2026-07-15

### Changed

- Repository extracted from `UiPath/ServiceCommon` to the public `UiPath/dotnet-caching` repository under the MIT license. PackageIds are renamed `UiPath.Platform.Caching.*` → `UiPath.Caching.*` at the first nuget.org release.
- **BREAKING:** `IConnectionMultiplexerFactory` is now async-only — the synchronous `Create(ConfigurationOptions)` member is removed and `CreateAsync(ConfigurationOptions, CancellationToken)` is the sole member. Custom factory implementors must implement `CreateAsync`. `RedisConnector` now caches the connection as `Lazy<Task<IConnectionMultiplexer>>` (one shared creation for async warm-up and sync first-use, with faulted-task self-heal), `IsConnected`/`GetEndPoints` are non-blocking snapshots (never block or throw), and `RedisPlannedMaintenance` is an `IHostedService` that connects/subscribes off the startup critical path with bounded retry + warning logging and deterministic shutdown.

### Added

- `UiPath.Caching.Azure` package + `ICachingBuilder.AddAzureEntraAuthentication(...)` — Microsoft Entra ID authentication for Azure Managed Redis. The core stays Azure-free via a new async extension point `IRedisConnectionConfigurator` (in `UiPath.Caching`), applied to the `ConfigurationOptions` once per connect (before `IConnectionMultiplexerFactory`); the Azure package implements it through `Microsoft.Azure.StackExchangeRedis`, which owns automatic token refresh. External apps implement `IRedisConnectionConfigurator` for other schemes (AWS IAM, custom tokens) with no core change. With no configurator registered, behavior is unchanged. `IRedisConnector` gains a `ConnectAsync` warm-up as a default-interface method (existing implementors unaffected). Entra connections default to TLS (`AzureEntraOptions.RequireSsl`) and RESP3 (`AzureEntraOptions.RequireResp3`) so pub/sub survives token rotation. Credential selection: an explicit `Credential`, else `ManagedIdentityCredential` from `ManagedIdentityOptions` when set (its `ManagedIdentityId` — set on `ManagedIdentityCredentialOptions` at construction — chooses the identity, defaulting to system-assigned; sovereign-cloud `AuthorityHost` supported here), else a user-assigned `ManagedIdentityCredential` from a non-empty `ManagedIdentityClientId`, else `DefaultAzureCredential`. `ManagedIdentityOptions` takes precedence over `ManagedIdentityClientId`; to combine a user-assigned identity with custom options, construct `ManagedIdentityOptions` with `ManagedIdentityId.FromUserAssignedClientId(clientId)`.
- `RedisConnectionOptions.WarmUpOnStart` (default `false`) — opt-in hosted-service warm-up that establishes the Redis connection at host startup (running any registered `IRedisConnectionConfigurator`s); best-effort, never blocks or fails startup. Left `false`, the connection is established lazily on first cache use. `RedisConnectionOptions.PlannedMaintenanceConnectionRetryCount` (default `5`) / `PlannedMaintenanceConnectionRetryDelay` (default `5s`, negative/zero clamped to `1s`) tune the planned-maintenance subscription's background retry.

- The hit/miss metric (`Caching.Stats.Hits`/`Misses`) is now emitted **once per read operation** with a `Keys` dimension carrying the number of keys in the operation (a batch's outcome is hit when any key hit). A multi-key `MGET` no longer fans out into one elapsed-time sample per key, which would skew the metric's latency distribution by batch size. Every hit/miss metric carries the `Keys` dimension (`1` for single-key and non-read operations such as writes/deletes/TTL, the batch size for multi-key reads and bulk set/remove) so the dimension is consistent across the metric name and group-by-`Keys` queries don't bucket operations into a null dimension.
- `RedisCacheOptions.KeyReadTelemetryEnabled` (default `false`) — opt-in per-key read attribution for `RedisCache` and `RedisHashCache`. When enabled, the read paths additionally emit a dependency **per key** with `type = "Redis"`, the Redis key in `data`, hit/miss carried by `resultCode` (`Hit`/`Miss`) plus an `Outcome` property, `Provider`/`Method`/`Type` properties, and a `BatchId` shared by all keys of one operation so the keys read together can be correlated (`summarize by BatchId` — a `Guid` minted per operation, since neither Redis nor StackExchange.Redis exposes a transaction id). The dependency's `success` is always `true` (a cache miss is not a failure, and `type = "Redis"` is shared with the profiler's dependency telemetry, so misses must not skew dependency-failure dashboards). For `RedisHashCache` it is **one dependency per hash key** (not per field) — an HGETALL on a hash with thousands of fields must not fan out into thousands of telemetry items — so `data` is the hash key and the field set is not enumerated. It lands in the same `dependencies` table the Redis profiler uses, so existing key-level KQL keeps working — restoring the "which keys are being read" attribution the profiler can no longer provide once batched reads are bundled into a single `MULTI`/`EXEC` transaction (the keys are no longer visible on the wire). It is opt-in because raw keys are high-cardinality. Surfaced through two new `ITelemetryOperation` members — `Track(bool hit, int keyCount)` (per-operation hit/miss metric with key count) and `TrackKeyReads((string Key, bool Hit)[] reads)` (opt-in per-key dependencies sharing a `BatchId`) — implemented by `TelemetryOperation` and no-op'd by `NullTelemetryOperation`. The per-key dependency's start time is captured at operation start (not at emit time).
- `IQueueCacheFactory` / `QueueCacheFactory` (+ `QueueCacheFactoryExtensions.CreateSetCache<T>()`) in `UiPath.Platform.Caching.Queue` — a dedicated set-cache factory that lets you create a typed set the same ergonomic way you create a cache: `queueCacheFactory.CreateSetCache<T>()`. It mirrors `CacheFactory.CreateCache` / `CacheFactoryExtensions.CreateCache<T>` for Redis sets. It is a separate factory (not a method on `ICacheFactory`) because the entire set implementation lives in the optional `Caching.Queue` package, which is downstream of `ICacheFactory` in `Caching.Abstractions` — so `ICacheFactory` cannot reference `ISetCache`. `QueueCacheFactory` hands out the singleton `ISetCache` registered by `AddRedisSetCache` (sets have a single Redis backing); `CreateSetCache<T>()` wraps it in `SetCache<T>` with no policy factory, exactly like `CreateCache<T>` (the underlying `RedisSetCache` still applies the global default policy). Registered automatically by `AddRedisSetCache`; inject `IQueueCacheFactory` where you need sets.
- `NullQueueCacheFactory` (in `UiPath.Caching`) — a no-op `IQueueCacheFactory` whose `CreateSetCache()` returns the singleton `NullSetCache.Instance`. `AddRedisSetCache` now registers `NullSetCache`, `NullQueueCacheFactory`, and `SetCache<>` when caching is disabled (`ICachingBuilder.Enabled == false`), mirroring how the core cache degrades to no-ops. Previously the disabled branch registered nothing, so any service depending on `ISetCache` / `IQueueCacheFactory` failed to resolve (and `ValidateOnBuild` threw) when caching was turned off; dependents now construct against the null set cache instead.
- `IResiliencePipelineProvider` — a name-based factory (`IResiliencePipeline Get(string? name)`) that replaces `IResiliencePipelineHolder`. It builds and caches a `ResiliencePipelineWrapper` for any **registered** name on first use, and returns a noop `EmptyResiliencePipeline` for a null/empty/unregistered name, so every component resolves the pipeline it needs through DI instead of receiving a fixed holder. The default registration is `EmptyResiliencePipelineProvider.Instance` (all-noop); `AddResilienceStrategies` registers the real `ResiliencePipelineProvider`. **BREAKING:** `IResiliencePipelineHolder` / `ResiliencePipelineHolder` are removed in favor of `IResiliencePipelineProvider`; the cache and topic components now take an `IResiliencePipelineProvider` constructor parameter. Consumers using the shipped DI wiring are unaffected; direct callers must pass the provider (use `EmptyResiliencePipelineProvider.Instance` to opt out of resilience).
- `CachingBuilderExtensions.AddResiliencePipeline(name, Action<ResiliencePoliciesOptions>)` — registers a named resilience pipeline, where `name` is the scope passed to `IResiliencePipelineProvider.Get(name)`. Each name gets its own `ResiliencePoliciesOptions` (resolved via `IOptionsMonitor` named options), so different scopes can carry different retry/timeout/circuit-breaker settings. `AddResilienceStrategies` predefines `ResiliencePipelineNames.Read` and `Write` from the base configuration; consumers can add new pipelines or retune the built-ins. Only registered names resolve to a real pipeline. The `ResiliencePipelineFactory` constructor now takes `IOptionsMonitor<ResiliencePoliciesOptions>` instead of `IOptions<ResiliencePoliciesOptions>` so it can resolve per-scope options; `Create<TResult>` selects the options for its scope via `optionsMonitor.Get(scope)`.
- `RedisSetCacheOptions.ResilienceKeyName` — the name of the resilience pipeline used for the **destructive-read** `SPOP` operations behind `RedisSetCache.PopAsync` (both overloads). The pipeline is resolved generically via `IResiliencePipelineProvider.Get(ResilienceKeyName)`, so a consumer who needs special handling for non-idempotent commands (e.g. a no-retry pipeline — a retry after a lost response would re-run `SPOP` server-side and silently drop members the first attempt already popped) registers their own scope and points `ResilienceKeyName` at it. When `ResilienceKeyName` is null/empty (the default), pops run with no resilience pipeline (`EmptyResiliencePipeline`), i.e. **at-most-once** with no retry. Wire it via `AddRedisSetCache(opt => opt.ResilienceKeyName = "...")`. The idempotent read/write operations continue to use the `Read` / `Write` pipelines.
- `ICachingTelemetryProvider` is now span-based: `TrackDependency`, `TrackEvent`, `TrackException`, and `TrackMetric` take `ReadOnlySpan<KeyValuePair<string, string>>` / `ReadOnlySpan<KeyValuePair<string, double>>` instead of `IDictionary`. `NullTelemetryProvider` is span-aware as a true no-op (zero allocation when telemetry is disabled). `CachingTelemetryProvider` (the adapter to `UiPath.Platform.Telemetry.ITelemetryProvider`) materializes the span once at the boundary via `TelemetryTags.ToDictionaryOrNull` to forward to the dict-based upstream. All hot-path call sites in `Caching.Runtime` migrated to span — cold-miss `RedisDistributedLock` events, Redis connection/maintenance events, memory-cache setter failure events, and stream subject writer events no longer allocate a tag dictionary at the call site.
- `ICache.GetCacheEntryAsync<T>` and `ICache.GetCacheEntriesAsync<T>` — bundled GET + TTL reads. Implementations may fetch the value and its remote expiration in a single network round-trip; `RedisCache` uses a `MULTI`/`EXEC` transaction (mirroring the hash-cache pattern).
- `RedisStreamsTopic` supports an optional Pub/Sub notify doorbell (`NotifyEnabled`) that drops publish-to-deliver latency from the poll interval to network RTT, while preserving stream durability and consumer-group semantics. Channel name defaults to the stream's Redis key joined with `NotifyChannelName` (default `"notify"`) using the same `CacheOptions.Separator` as the rest of the key scheme. Set `NotifyShardedPubSub = true` to use sharded Pub/Sub (`SPUBLISH`/`SSUBSCRIBE`, requires Redis 7.0+) so the doorbell does not fan out across cluster nodes; the sharded strategy wraps the stream key as a Redis Cluster hash tag (or inherits an existing one) so the channel and stream share the same slot — `XADD` and `SPUBLISH` go to the same node. Otherwise regular `PUBLISH`/`SUBSCRIBE` is used. Override the channel entirely via `NotifyChannelStrategy`. Pub/Sub is best-effort — the existing poll continues to run as a safety net.
- Single-flight locking on `MultilayerCache.GetOrAddAsync` / `MultilayerHashCache.GetOrAddAsync` via two new abstractions: `ILocalLock` (in-process serialization per cache key, default impl `AsyncKeyedLocalLock` backed by `AsyncKeyedLock`) and `IDistributedLock` (cross-process coordination, default impl `RedisDistributedLock` using `LockTakeAsync`/`LockReleaseAsync` with a `SourceUri`-prefixed token). Both are layered behind double-checked reads so a single generator runs per key on cache miss; lock-take failures fall back to a no-op disposable so a Redis outage degrades to thundering-herd behavior rather than stalling. New options on `IMultilayerCacheOptions`: `LocalLockEnabled`, `DistributedLockEnabled`, `DistributedLockTimeout` (default 500 ms), `DistributedLockExpiry` (default 5 s), `LockKeyStrategy` (default appends `:lck`). New options on `CacheOptions`: `LocalLockPoolSize` (default 100), `LocalLockPoolInitialFill` (default 10). Wire via `AddLocalLock()` / `AddRedisDistributedLock()` on `ICachingBuilder`; `AddMemory` and `AddInMemoryRedis` register them automatically. `CachingBuilder.Complete` registers `NullLocalLock` / `NullDistributedLock` as singletons via `TryAddSingleton`, so providers that do not register a lock get a safe no-op. Direct (non-DI) callers can also pass `NullLocalLock.Instance` / `NullDistributedLock.Instance` (both `public`) to opt out of locking.

### Changed

- **BREAKING:** `ITelemetryOperation` gains two members — `Track(bool hit, int keyCount)` (per-operation hit/miss metric carrying the key count) and `TrackKeyReads((string Key, bool Hit)[] reads)` (opt-in per-key dependencies). External implementers of `ITelemetryOperation` must add them; the shipped implementations (`TelemetryOperation`, `NullTelemetryOperation`) already do. `TrackKeyReads` is invoked only when `RedisCacheOptions.KeyReadTelemetryEnabled` is enabled; the keyless `Track(bool)` path is unchanged for existing consumers.
- `MultilayerCache` and `MultilayerHashCache` no longer issue a separate `ExpireTimeAsync` call after a cache-miss read. They now use the bundled `ICacheEntry.Expiration` returned by the inner cache. For Redis-backed multilayer caches this collapses N + 1 sequential commands to one transaction (one round-trip, one `EXEC` dependency in telemetry) when fetching N keys, eliminating per-key `PTTL`/`EXPIRETIME` traffic on the read path. Public APIs are unchanged.
- **BREAKING:** `ICachingBuilder.RegisterOnCompleteCallback(Action<ICachingBuilder>)` is replaced by `RegisterOnCompleteCallback(object key, Action<ICachingBuilder>)`. `CachingBuilder` keeps a per-builder set of seen keys and ignores re-registrations against an already-seen key on the same instance. Callers should pass a deterministic key (typically `typeof(YourExtensionsClass)`).
- **BREAKING:** `Caching.Polly.CachingBuilderExtensions.ConfigureTelemetry(this ICachingBuilder, bool, Action<TelemetryOptions>?)` is removed. Telemetry is now configured exclusively through `AddResilienceStrategies(..., configureTelemetryOptions: ...)` and `ResiliencePoliciesOptions.TelemetryEnabled`, which flow through `IOptions<TelemetryOptions>` / `IOptions<ResiliencePoliciesOptions>` per-container instead of process-static fields.
- **BREAKING:** `MultilayerCache` and `MultilayerHashCache` constructors take two new required parameters — `ILocalLock localLock` and `IDistributedLock distributedLock` — inserted before `ILogger logger` (now the last parameter). Apps using the built-in DI extensions (`AddMemory`, `AddInMemoryRedis`) are wired automatically through the new `AddLocalLock()` / `AddRedisDistributedLock()` registrations. Callers that instantiate these caches directly must pass both arguments; use the now-public `NullLocalLock.Instance` / `NullDistributedLock.Instance` to opt out (or resolve `ILocalLock` / `IDistributedLock` from DI). No overload was added on purpose so the breakage is loud rather than silent.
- **BREAKING:** `InMemoryCacheProvider` and `InMemoryRedisCacheProvider` constructors take new **required** parameters for the lock services — `ILocalLock` on the former, `ILocalLock` + `IDistributedLock` on the latter. Same rationale as the multi-layer cache ctors: DI users are unaffected (`CachingBuilder.Complete` `TryAddSingleton`-registers `NullLocalLock` / `NullDistributedLock`), but direct callers must now pass them explicitly. The previous optional / nullable parameter shape was rejected because it preserves source compatibility while still binary-breaking, which is the worst combination — silent for source readers, loud for production consumers.
- **BREAKING:** `IMultilayerCacheOptions` gains five lock-related members — `LocalLockEnabled`, `DistributedLockEnabled`, `DistributedLockTimeout`, `DistributedLockExpiry`, `LockKeyStrategy`. External types that implement this interface (rather than deriving from `InMemoryRedisCacheOptions` / `InMemoryCacheOptions`) must add the new properties; default interface implementations were avoided so missing storage cannot silently drop user-set values.
- **BREAKING:** `ICachingTelemetryProvider`'s `TrackDependency` / `TrackEvent` / `TrackException` / `TrackMetric` members no longer take `IDictionary<string, string>?` / `IDictionary<string, double>?` — they now take `ReadOnlySpan<KeyValuePair<string, string>>` / `ReadOnlySpan<KeyValuePair<string, double>>`. The dict-based members are removed from the interface entirely. The members carry empty default interface bodies (no-op) so that proxy-based mocking frameworks (NSubstitute / Castle.DynamicProxy) can build proxies for the interface — those frameworks cannot generate valid IL for abstract methods with ref-struct parameters. **External implementers must override the span methods** to do real work; relying on a previous dict-based implementation will silently drop telemetry because the dict members are no longer part of the interface contract. Migration: implement the four span methods directly; for callers building tag bags dynamically, use `List<KeyValuePair<string, string>>` plus `CollectionsMarshal.AsSpan(list)`, or convert an existing dictionary via `dict.ToArray()`.

### Fixed

- `RedisCache.GetCacheEntryAsync` / `GetCacheEntriesAsync` and `RedisHashCache.GetCacheEntryAsync` now pass `CommandFlags.PreferReplica` to `transaction.ExecuteAsync(...)`, so the bundled GET + TTL transaction actually routes to a replica. SE.Redis ignores the flag set on inner queued commands when picking a server for the transaction; without an explicit flag on `ExecuteAsync` the read landed on the master. These read paths now also use the `_read` resilience pipeline instead of `_write`.
- Bulk write transactions in `RedisCache.SetAsync(KeyValuePair<>[])` and `RedisHashCache` Set/Refresh paths now pass `CommandFlags.DemandMaster` to `transaction.ExecuteAsync(...)` for symmetry with the read fix and to make the master-only routing intent explicit (inner-command flags do not propagate to transaction routing in SE.Redis).
- `AddInMemoryRedis` and `AddResilienceStrategies` no longer use a process-static `_callbackRegistered` flag to gate their on-complete callback. The static guard silently broke L1 invalidation / resilience pipelines for any second host wired up in the same process: only the first builder's callback ever ran, leaving subsequent builders with `NullChangeTokenFactory` and `EmptyResiliencePipelineProvider.Instance`. Both extensions now register their callbacks against a deterministic per-extension key (e.g., `typeof(InMemoryRedisCollectionExtensions)` / `typeof(CachingBuilderExtensions)`) via the keyed `RegisterOnCompleteCallback` overload.
- `AddResilienceStrategies` no longer stores telemetry enable/config in process-static fields. `IResiliencePipelineFactory` now resolves `IOptions<TelemetryOptions>` and `IOptions<ResiliencePoliciesOptions>` from its own container, so two hosts in the same process keep independent telemetry settings. Previously the second `AddResilienceStrategies` call would silently overwrite the first host's telemetry configuration (the factory singleton read the statics at resolution time, not at registration time).

### Documentation

- Restructured `docs/` into a layered surface: `quickstart.md`, `concepts.md`, `how-to/`, `recipes/`, `reference/`. `docs/basics.md`, `docs/advanced-usage.md`, and `docs/telemetry.md` are removed; their content is migrated. See `docs/index.md` for the decision tree.
- `Sample.AspNetCore/appsettings.all.json` now lists every binding-visible option on every shipped options class with shipped defaults. Verified by `Caching.Tests/AppSettingsAllJsonBindingTests.cs`.

