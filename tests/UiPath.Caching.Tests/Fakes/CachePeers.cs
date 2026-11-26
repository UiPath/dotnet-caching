using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using UiPath.Caching.Config;
using UiPath.Caching.Locking;
using UiPath.Caching.Telemetry;
using UiPath.Caching.Tests.Broadcast;
using UiPath.Caching.Tests.Telemetry;

namespace UiPath.Caching.Tests.Fakes;

/// <summary>Real multilayer caches, one per node, over one shared stand-in for Redis, with a bus that carries their events as JSON, as the wire does.</summary>
internal sealed class CachePeers(TimeProvider? defaultClock = null) : IDisposable
{
    private static readonly FieldInfo BackgroundField = typeof(MultilayerCacheBase).GetField("_background", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private readonly List<Node> _nodes = [];
    private readonly CacheEventFormatter _formatter = new();

    public SharedStore Store { get; } = new();

    /// <summary>Whether the nodes' inner tier leaves remove tombstones, so a fill is told how long its generator ran.</summary>
    public bool Fenced { get; set; }

    /// <summary>When off, events wait in <see cref="Node.Published"/> for <see cref="Deliver"/>.</summary>
    public bool AutoDeliver { get; set; } = true;

    /// <param name="clock">The cache's clock; a manual clock when null.</param>
    /// <param name="localClock">The local tier's clock; the cache's clock when null.</param>
    public Node Add(string name, InMemoryRedisCacheOptions? options = null, RecordingTelemetryProvider? telemetry = null, TimeProvider? clock = null, TimeProvider? localClock = null, ICachePolicyFactory? policies = null)
    {
        clock ??= defaultClock ?? new ManualTimeProvider(localClock?.GetUtcNow() ?? DateTimeOffset.UnixEpoch);
        var node = new Node(this, name, options ?? new InMemoryRedisCacheOptions(), telemetry, clock, localClock ?? defaultClock ?? clock, policies);
        _nodes.Add(node);
        return node;
    }

    public void Deliver(Node to, ICacheEvent @event) =>
        to.Subject.OnNext(_formatter.Decode(_formatter.Encode(@event))!);

    /// <summary>Hands the event to one observer only, as a dispatch that took its observers before a later one subscribed does.</summary>
    public void Deliver(IObserver<ICacheEvent> to, ICacheEvent @event) =>
        to.OnNext(_formatter.Decode(_formatter.Encode(@event))!);

    public void Dispose()
    {
        foreach (var node in _nodes)
        {
            node.Dispose();
        }
    }

    /// <summary>Waits until <paramref name="telemetry"/> holds <paramref name="outcomes"/> kept, discarded or dropped keys in all, flushing as it waits: the refresher ends a key's run just before it counts the key.</summary>
    internal static async Task Reported(MultilayerCacheBase cache, RecordingTelemetryProvider telemetry, int outcomes)
    {
        cache.FlushPeerRefreshMetrics();
        await telemetry.Changes.WaitUntil(() => Outcomes(telemetry) >= outcomes);
    }

    internal static Task BackgroundSettled(MultilayerCacheBase cache) =>
        ((BackgroundWrites)BackgroundField.GetValue(cache)!).WhenIdleAsync();

    /// <summary>Whether a background write of <paramref name="key"/> is in flight, as a peer refresh's keep checks it: false once the write promoted its entry and left its turn.</summary>
    internal static bool WriteInFlight(MultilayerCacheBase cache, string key) =>
        ((BackgroundWrites)BackgroundField.GetValue(cache)!).WriteInFlight(key);

    internal static async Task Until(Func<bool> condition)
    {
        var changed = OperationSignals.Shared.WhenAsync(condition);
        if (!changed.IsCompleted)
        {
            RunTables.ObserveCompletions();
        }
        await changed;
        condition().Should().BeTrue();
    }

    private static double Outcomes(RecordingTelemetryProvider telemetry) =>
        telemetry.Metrics.Where(m => m.Name is "cache.peer_refresh.kept" or "cache.peer_refresh.discarded" or "cache.peer_refresh.dropped").Sum(m => m.Value);

    private ValueTask<bool> Publish(Node from, ICacheEvent @event)
    {
        lock (from.Published)
        {
            from.Published.Add(@event);
        }

        from.OnPublish?.Invoke();
        if (from.PublishRefused)
        {
            return new ValueTask<bool>(false);
        }

        if (AutoDeliver)
        {
            foreach (var node in _nodes)
            {
                Deliver(node, @event);
            }
        }

        return new ValueTask<bool>(true);
    }

    internal sealed class SharedStore
    {
        /// <summary>What a key holds when no serializer reads its bytes.</summary>
        public static object Unreadable { get; } = new();

        public AsyncTestSignal Changes { get; } = new();

        public ConcurrentDictionary<string, object?> Values { get; } = new(StringComparer.Ordinal);

        /// <summary>The lifetime a read reports for a key, from the read; an hour for a key not listed.</summary>
        public ConcurrentDictionary<string, TimeSpan> Remaining { get; } = new(StringComparer.Ordinal);

        /// <summary>A write nobody is told of.</summary>
        public void WriteQuietly(string key, object? value) => Values[key] = value;

        /// <summary>A removal nobody is told of.</summary>
        public void RemoveQuietly(string key) => Values.TryRemove(key, out _);
    }

    internal sealed class Node : IDisposable
    {
        private readonly List<(int Count, TaskCompletionSource Done)> _evictionWaiters = [];
        private readonly RecordingTelemetryProvider _telemetry;
        private int _evicted;

        public Node(CachePeers peers, string name, InMemoryRedisCacheOptions options, RecordingTelemetryProvider? telemetry, TimeProvider clock, TimeProvider localClock, ICachePolicyFactory? policies = null)
        {
            Name = name;
            _telemetry = telemetry ?? new RecordingTelemetryProvider();
            var cacheOptions = new CacheOptions { AppShortName = "test", ConnectionMonitorEnabled = true, SourceUri = new Uri($"urn:node:{name}") };
            var inner = DispatchProxy.Create<ICache, NodeInner>();
            Inner = (NodeInner)(object)inner;
            Inner.Store = peers.Store;
            Inner.Clock = clock;
            Inner.Fenced = peers.Fenced;

            var events = new CacheEventFactory(Options.Create(cacheOptions));
            var tokens = new ChangeTokenFactory<byte[]>(Options.Create(cacheOptions), new SystemJsonByteSerializerProxy(), NullLoggerFactory.Instance, Substitute.For<ICachingTelemetryProvider>());
            Topic = new NodeTopic(this, peers);
            var provider = Substitute.For<ITopicProvider>();
            provider.Enabled.Returns(true);
            provider.Create(Arg.Any<TopicKey>()).Returns(Topic);
            var topics = Substitute.For<ITopicFactory>();
            topics.Get(Arg.Any<string?>()).Returns(provider);
            Cache = RunTables.Track(new MultilayerCache(
                KnownCacheProviderNames.InMemoryRedis,
                inner,
                new KeptMemoryCacheFactory(this, localClock),
                tokens,
                topics,
                events,
                _telemetry,
                options,
                options,
                cacheOptions,
                NullDistributedLock.Instance,
                policies ?? NullCachePolicyFactory.Instance,
                clock,
                NullLogger.Instance));
        }

        public string Name { get; }

        public MultilayerCache Cache { get; }

        public NodeInner Inner { get; }

        public KeyedSubject<ICacheEvent> Subject { get; } = new(NullLogger.Instance);

        public List<ICacheEvent> Published { get; } = [];

        /// <summary>When set, the node's topic reports every event as not published.</summary>
        public bool PublishRefused { get; set; }

        /// <summary>Runs inside every event the node publishes, before it is delivered.</summary>
        public Action? OnPublish { get; set; }

        /// <summary>Every observer that subscribed to the node's topic, in order: the change tokens of its local entries.</summary>
        public List<IObserver<ICacheEvent>> Subscribers { get; } = [];

        /// <summary>Every observer that ended its subscription, in order.</summary>
        public List<IObserver<ICacheEvent>> Unsubscribed { get; } = [];

        /// <summary>The node's local tier.</summary>
        public IMemoryCache Local { get; private set; } = null!;

        /// <summary>When set, removing a key from the local tier throws.</summary>
        public bool RemoveThrows { get; set; }

        /// <summary>Runs on the writing thread before an entry of the local tier commits, with its key, after the cache wired its token.</summary>
        public Action<string>? BeforeCommit { get; set; }

        /// <summary>When set, every eviction callback of the local tier waits for it, so a test decides when a leaving is heard.</summary>
        public ManualResetEventSlim? EvictionGate { get; set; }

        /// <summary>Runs on the writing thread right after an entry of the local tier commits, with its key, before the cache keeps its fail-safe value.</summary>
        public Action<string>? AfterCommit { get; set; }

        /// <summary>Runs once, as the next fail-safe value is committed to the local tier.</summary>
        public Action? BeforeKept { get; set; }

        private NodeTopic Topic { get; }

        /// <summary>Completes once the local tier has run the eviction callbacks of <paramref name="count"/> entries: it runs them on the pool, after the entry is replaced or removed.</summary>
        public Task Evicted(int count)
        {
            lock (_evictionWaiters)
            {
                if (_evicted >= count)
                {
                    return Task.CompletedTask;
                }

                var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _evictionWaiters.Add((count, done));
                return done.Task;
            }
        }

        public async Task Settled()
        {
            await BackgroundSettled(Cache);
            await RunTables.RefreshesSettledAsync(Cache);
            Cache.FlushPeerRefreshMetrics();
        }

        /// <summary>Settles once the refresher has reported <paramref name="outcomes"/> keys since the node started.</summary>
        public async Task Settled(int outcomes)
        {
            await Settled();
            await Reported(Cache, _telemetry!, outcomes);
        }

        public void Dispose()
        {
            Cache.Dispose();
            Subject.Dispose();
        }

        private void OnEvicted()
        {
            lock (_evictionWaiters)
            {
                _evicted++;
                _evictionWaiters.RemoveAll(waiter => waiter.Count <= _evicted && waiter.Done.TrySetResult());
            }
        }

        private sealed class KeptMemoryCacheFactory(Node node, TimeProvider clock) : IMemoryCacheFactory
        {
            private readonly MemoryCacheFactory _inner = new(clock, NullLoggerFactory.Instance);

            public IMemoryCache Get(IMemoryCacheOptions memoryOptions) => node.Local = new HookedMemoryCache(node, _inner.Get(memoryOptions));
        }

        private sealed class HookedMemoryCache(Node node, IMemoryCache inner) : IMemoryCache
        {
            public Microsoft.Extensions.Caching.Memory.ICacheEntry CreateEntry(object key) => new HookedEntry(node, inner.CreateEntry(key));

            public void Remove(object key)
            {
                if (node.RemoveThrows)
                {
                    throw new InvalidOperationException("remove failed");
                }

                inner.Remove(key);
            }

            public bool TryGetValue(object key, out object? value) => inner.TryGetValue(key, out value);

            public void Dispose() => inner.Dispose();
        }

        private sealed class HookedEntry(Node node, Microsoft.Extensions.Caching.Memory.ICacheEntry inner) : Microsoft.Extensions.Caching.Memory.ICacheEntry
        {
            public object Key => inner.Key;

            public object? Value
            {
                get => inner.Value;
                set => inner.Value = value;
            }

            public DateTimeOffset? AbsoluteExpiration
            {
                get => inner.AbsoluteExpiration;
                set => inner.AbsoluteExpiration = value;
            }

            public TimeSpan? AbsoluteExpirationRelativeToNow
            {
                get => inner.AbsoluteExpirationRelativeToNow;
                set => inner.AbsoluteExpirationRelativeToNow = value;
            }

            public TimeSpan? SlidingExpiration
            {
                get => inner.SlidingExpiration;
                set => inner.SlidingExpiration = value;
            }

            public IList<IChangeToken> ExpirationTokens => inner.ExpirationTokens;

            public IList<PostEvictionCallbackRegistration> PostEvictionCallbacks => inner.PostEvictionCallbacks;

            public CacheItemPriority Priority
            {
                get => inner.Priority;
                set => inner.Priority = value;
            }

            public long? Size
            {
                get => inner.Size;
                set => inner.Size = value;
            }

            public void Dispose()
            {
                if (inner.Key is string key)
                {
                    node.BeforeCommit?.Invoke(key);
                }
                else if (node.BeforeKept is { } kept)
                {
                    node.BeforeKept = null;
                    kept();
                }

                foreach (var registration in inner.PostEvictionCallbacks)
                {
                    var callback = registration.EvictionCallback;
                    registration.EvictionCallback = (key, value, reason, state) =>
                    {
                        node.EvictionGate?.Wait();
                        callback?.Invoke(key, value, reason, state);
                        node.OnEvicted();
                    };
                }

                inner.Dispose();
                if (inner.Key is string committed)
                {
                    node.AfterCommit?.Invoke(committed);
                }
            }
        }

        private sealed class NodeTopic(Node node, CachePeers peers) : ITopic<ICacheEvent>
        {
            public TopicKey TopicKey { get; } = new("topic");

            public EventHandler? OnDisposed { get; set; }

            public IDisposable Subscribe(IObserver<ICacheEvent> observer)
            {
                lock (node.Subscribers)
                {
                    node.Subscribers.Add(observer);
                }

                return new Recorded(node, observer, node.Subject.Subscribe(observer));
            }

            public ValueTask<bool> PublishAsync(ICacheEvent @event, CancellationToken token = default) => peers.Publish(node, @event);

            public void Dispose()
            {
            }
        }

        private sealed class Recorded(Node node, IObserver<ICacheEvent> observer, IDisposable subscription) : IDisposable
        {
            public void Dispose()
            {
                subscription.Dispose();
                lock (node.Unsubscribed)
                {
                    node.Unsubscribed.Add(observer);
                }
            }
        }
    }

    /// <summary>The inner tier of a node over the shared store: counts its reads, can hold or fail them, and reports a connection state the test sets.</summary>
    internal class NodeInner : DispatchProxy, IConnectionState, ISharedTier, IMigrationCache, IStampedTier, IFillManyTier, IFencedFillTier
    {
        private static readonly MethodInfo ReadOne = typeof(NodeInner).GetMethod(nameof(ReadOneAsync), BindingFlags.Instance | BindingFlags.NonPublic)!;
        private static readonly MethodInfo ReadMany = typeof(NodeInner).GetMethod(nameof(ReadManyAsync), BindingFlags.Instance | BindingFlags.NonPublic)!;
        private static readonly MethodInfo WriteMany = typeof(NodeInner).GetMethod(nameof(WriteAll), BindingFlags.Static | BindingFlags.NonPublic)!;
        private static readonly MethodInfo FillOne = typeof(NodeInner).GetMethod(nameof(FillOneAsync), BindingFlags.Instance | BindingFlags.NonPublic)!;

        private readonly AsyncLocal<bool> _masterDemanded = new();
        private readonly ConcurrentQueue<bool> _readsOnMaster = new();
        private readonly ConcurrentQueue<int> _batchSizes = new();
        private volatile bool _connected = true;
        private int _openScopes;
        private Task? _afterNextRead;
        private int _singleReads;
        private int _batchReads;
        private int _fills;
        private int _fillBatches;

        public event EventHandler? OnConnectionFailed;

        public event EventHandler? OnConnectionRestored;

        public event EventHandler? OnReconnected
        {
            add { /* Expected: this fake never raises the event. */ }
            remove { /* Expected: nothing to detach. */ }
        }

        public SharedStore Store { get; set; } = null!;
        public TimeProvider Clock { get; set; } = null!;

        public bool Fenced { get; set; }

        /// <summary>The generator age each fill that was told one carried, in order.</summary>
        public ConcurrentQueue<TimeSpan> GeneratorAges { get; } = [];

        /// <summary>Whether a fill of the key finds a tombstone written after its generator started.</summary>
        public Func<CacheKey, bool>? RemovedWhileLoading { get; set; }

        bool IFencedFillTier.IsFenced => Fenced;

        public TestSignal Changed { get; } = new();

        public AsyncTestSignal Changes { get; } = new();

        public int SingleReads => Volatile.Read(ref _singleReads);

        public int BatchReads => Volatile.Read(ref _batchReads);

        /// <summary>The keys a fill command reached, whether it wrote them or found them held.</summary>
        public int Fills => Volatile.Read(ref _fills);

        /// <summary>The batch fill commands, each of which reached one or more keys.</summary>
        public int FillBatches => Volatile.Read(ref _fillBatches);

        public int[] BatchSizes => [.. _batchSizes];

        /// <summary>Per read, in order: whether it ran inside <see cref="ISharedTier.DemandMaster"/>.</summary>
        public bool[] ReadsOnMaster => [.. _readsOnMaster];

        /// <summary>When set, a read waits for it before it reads its flags, as a command sent after the read's first await does.</summary>
        public Task? BeforeFlags { get; set; }

        /// <summary>When set, a read waits for it before it reaches the store.</summary>
        public Task? Hold { get; set; }

        /// <summary>When set, a held read does not observe its cancellation, as a Redis command in flight does not.</summary>
        public bool HoldIgnoresCancellation { get; set; }

        public bool ReadThrows { get; set; }

        /// <summary>When set, the next single-key read waits for it after it has read the store, then answers with what it read.</summary>
#pragma warning disable S2292 // The backing field is exchanged through Interlocked.
        public Task? AfterNextRead
        {
            get => _afterNextRead;
            set => _afterNextRead = value;
        }
#pragma warning restore S2292

        /// <summary>What <see cref="IMigrationCache.WriteAsync{T}"/> and <see cref="IMigrationCache.DeleteAsync{T}"/> answer, once they have applied a committed one to the store.</summary>
        public StoreOutcome MigrationOutcome { get; set; } = StoreOutcome.Committed;

        /// <summary>When set, a migration write or delete waits for it before it answers.</summary>
        public Task? MigrationGate { get; set; }

        /// <summary>When set, a removal waits for it before it reaches the store.</summary>
        public Task? RemoveHold { get; set; }

        /// <summary>When set, a conditional write runs it before it compares.</summary>
        public Func<Task>? BeforeStampedWrite { get; set; }

        /// <summary>When set, a single-key write reaches the store and then waits for it before it answers.</summary>
        public Task? AfterWrite { get; set; }

        public bool IsConnected
        {
            get => _connected;
            set
            {
                _connected = value;
                (value ? OnConnectionRestored : OnConnectionFailed)?.Invoke(this, EventArgs.Empty);
            }
        }

        async ValueTask<SnapshotRead<T>> IMigrationCache.ReadSnapshotAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token)
            where T : default
        {
            var entry = await ReadOneAsync<T>(cacheKey, token);
            return entry.Found ? new SnapshotRead<T>(SnapshotOutcome.Found, entry) : new SnapshotRead<T>(SnapshotOutcome.Absent, null);
        }

        ValueTask<FillResult<T>> IFencedFillTier.TryFillAfterAsync<T>(CacheKey cacheKey, T? value, DateTimeOffset expiration, TimeSpan generatorElapsed, CachePolicy? policy, CancellationToken token)
            where T : default
        {
            GeneratorAges.Enqueue(generatorElapsed);
            return RemovedWhileLoading?.Invoke(cacheKey) == true ? new(new FillResult<T>(FillOutcome.Removed, default)) : FillOneAsync(cacheKey, value);
        }

        async ValueTask<(SnapshotRead<T> Snapshot, object? Stamp)> IStampedTier.ReadStampedAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token)
        {
            var (entry, seen) = await ReadOneStampedAsync<T>(cacheKey, token);
            if (ReferenceEquals(seen, SharedStore.Unreadable))
            {
                // What the read saw is what it stamps, whatever lands while its answer is held.
                return (new SnapshotRead<T>(SnapshotOutcome.Unreadable, null), new Seen(seen));
            }

            return entry.Found && Store.Values.TryGetValue(cacheKey.Name, out var held)
                ? (new SnapshotRead<T>(SnapshotOutcome.Found, entry), new Seen(held))
                : (new SnapshotRead<T>(SnapshotOutcome.Absent, null), null);
        }

        async ValueTask<StampedWrite> IStampedTier.WriteIfStampAsync<T>(CacheKey cacheKey, T? value, DateTimeOffset expiration, CachePolicy? policy, object? stamp, CancellationToken token)
            where T : default
        {
            if (BeforeStampedWrite is { } before)
            {
                await before();
            }

            var won = stamp is Seen seen
                ? Store.Values.TryUpdate(cacheKey.Name, value, seen.Value)
                : Store.Values.TryAdd(cacheKey.Name, value);
            Store.Changes.Pulse();
            return won ? StampedWrite.Won : StampedWrite.Lost;
        }

        ValueTask<FillResult<T>[]> IFillManyTier.TryFillManyAsync<T>(KeyValuePair<CacheKey, T?>[] entries, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token, TimeSpan? generatorElapsed)
            where T : default
        {
            Interlocked.Increment(ref _fillBatches);
            if (generatorElapsed is { } age)
            {
                GeneratorAges.Enqueue(age);
            }

            var results = new FillResult<T>[entries.Length];
            for (var i = 0; i < entries.Length; i++)
            {
                results[i] = RemovedWhileLoading?.Invoke(entries[i].Key) == true ? new FillResult<T>(FillOutcome.Removed, default) : FillOneAsync(entries[i].Key, entries[i].Value).AsTask().Result;
            }

            return new(results);
        }

        async ValueTask<StoreOutcome> IMigrationCache.WriteAsync<T>(CacheKey cacheKey, T? value, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token)
            where T : default
        {
            if (MigrationGate is { } gate)
            {
                await gate;
            }

            if (MigrationOutcome == StoreOutcome.Committed)
            {
                Store.Values[cacheKey.Name] = value;
                Store.Changes.Pulse();
            }

            return MigrationOutcome;
        }

        ValueTask<AddOutcome> IMigrationCache.TryCreateAsync<T>(CacheKey cacheKey, T? value, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token)
            where T : default => throw new NotSupportedException();

        async ValueTask<StoreOutcome> IMigrationCache.DeleteAsync<T>(CacheKey cacheKey, CancellationToken token)
        {
            if (MigrationGate is { } gate)
            {
                await gate;
            }

            if (MigrationOutcome == StoreOutcome.Committed)
            {
                Store.Values.TryRemove(cacheKey.Name, out _);
            }

            return MigrationOutcome;
        }

        void ISharedTier.Invalidated(CacheKey key)
        {
        }

        void ISharedTier.InvalidatedAll()
        {
        }

        IDisposable ISharedTier.DemandMaster()
        {
            _masterDemanded.Value = true;
            Interlocked.Increment(ref _openScopes);
            return new MasterScope(this);
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2012:Use ValueTasks correctly", Justification = "DispatchProxy returns the ValueTask boxed to the proxied call, which awaits it once.")]
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            try
            {
                switch (targetMethod!.Name)
                {
                    case "get_Name":
                        return "Redis";
                    case "Dispose":
                        return null;
                    case nameof(ICache.GetCacheEntryAsync):
                        return ReadOne.MakeGenericMethod(targetMethod.GetGenericArguments()).Invoke(this, [args![0], args[2]]);
                    case nameof(ICache.GetCacheEntriesAsync):
                        return ReadMany.MakeGenericMethod(targetMethod.GetGenericArguments()).Invoke(this, [args![0], args[2]]);
                    case nameof(ICache.SetAsync) when args![0] is CacheKey key:
                        Store.Values[key.Name] = args[1];
                        Store.Changes.Pulse();
                        return AfterWrite is { } after ? new ValueTask<bool>(after.ContinueWith(static _ => true, TaskScheduler.Default)) : new ValueTask<bool>(true);
                    case nameof(ICache.TryFillAsync) when targetMethod.ReturnType == typeof(ValueTask<FillResult<string>>):
                        // The common shape without reflection, so a test that measures allocations measures the cache.
                        return FillString((CacheKey)args![0]!, (string?)args[1]);
                    case nameof(ICache.TryFillAsync):
                        return FillOne.MakeGenericMethod(targetMethod.GetGenericArguments()).Invoke(this, [args![0], args[1]]);
                    case nameof(ICache.SetAsync):
                        WriteMany.MakeGenericMethod(targetMethod.GetGenericArguments()).Invoke(null, [Store, args[0]]);
                        return new ValueTask<bool>(true);
                    case nameof(ICache.RemoveAsync) when args![0] is CacheKey key:
                        return RemoveAfter([key]);
                    case nameof(ICache.RemoveAsync):
                        return RemoveAfter((CacheKey[])args[0]!);
                    default:
                        throw new NotSupportedException(targetMethod.Name);
                }
            }
            finally
            {
                OperationSignals.Shared.Completed();
            }
        }

        private static void WriteAll<T>(SharedStore store, KeyValuePair<CacheKey, T?>[] values)
        {
            foreach (var (key, value) in values)
            {
                store.Values[key.Name] = value;
                store.Changes.Pulse();
            }
        }

        private TestCacheEntry<T?> EntryFor<T>(SharedStore store, CacheKey key)
        {
            if (!store.Values.TryGetValue(key.Name, out var value) || ReferenceEquals(value, SharedStore.Unreadable))
            {
                return new TestCacheEntry<T?> { Expiration = DateTimeOffset.MinValue, Found = false };
            }

            var remaining = store.Remaining.TryGetValue(key.Name, out var left) ? left : TimeSpan.FromHours(1);
            return new TestCacheEntry<T?> { Value = (T?)value, Expiration = Clock.ToDateTimeOffset(remaining), Found = true };
        }

        private ValueTask<FillResult<string>> FillString(CacheKey key, string? value)
        {
            Interlocked.Increment(ref _fills);
            if (!Store.Values.TryAdd(key.Name, value))
            {
                if (ReferenceEquals(Store.Values[key.Name], SharedStore.Unreadable))
                {
                    return new(new FillResult<string>(FillOutcome.Unreadable, null));
                }

                var remaining = Store.Remaining.TryGetValue(key.Name, out var left) ? left : TimeSpan.FromHours(1);
                return new(new FillResult<string>(FillOutcome.Existed, (string?)Store.Values[key.Name], Clock.ToDateTimeOffset(remaining)));
            }

            Store.Changes.Pulse();
            return AfterWrite is { } after
                ? new(after.ContinueWith(static _ => new FillResult<string>(FillOutcome.Added, null), TaskScheduler.Default))
                : new(new FillResult<string>(FillOutcome.Added, null));
        }

        private ValueTask<FillResult<T>> FillOneAsync<T>(CacheKey key, T? value)
        {
            Interlocked.Increment(ref _fills);
            // A write when the key is absent, as the store's NX is; one that finds it answers with the stored value.
            if (!Store.Values.TryAdd(key.Name, value))
            {
                if (ReferenceEquals(Store.Values[key.Name], SharedStore.Unreadable))
                {
                    return new(new FillResult<T>(FillOutcome.Unreadable, default));
                }

                var remaining = Store.Remaining.TryGetValue(key.Name, out var left) ? left : TimeSpan.FromHours(1);
                return new(new FillResult<T>(FillOutcome.Existed, (T?)Store.Values[key.Name], Clock.ToDateTimeOffset(remaining)));
            }

            Store.Changes.Pulse();
            var added = new FillResult<T>(FillOutcome.Added, default);
            return AfterWrite is { } after ? new(after.ContinueWith(_ => added, TaskScheduler.Default)) : new(added);
        }

        private ValueTask<bool> RemoveAfter(CacheKey[] keys)
        {
            return RemoveHold is { } hold ? new ValueTask<bool>(RemoveLaterAsync(hold)) : new ValueTask<bool>(Remove());

            async Task<bool> RemoveLaterAsync(Task held)
            {
                await held;
                return Remove();
            }

            bool Remove()
            {
                var removed = false;
                foreach (var each in keys)
                {
                    removed |= Store.Values.TryRemove(each.Name, out _);
                }

                return removed;
            }
        }

        private async ValueTask<ICacheEntry<T?>> ReadOneAsync<T>(CacheKey key, CancellationToken token) => (await ReadOneStampedAsync<T>(key, token)).Entry;

        private async ValueTask<(ICacheEntry<T?> Entry, object? Held)> ReadOneStampedAsync<T>(CacheKey key, CancellationToken token)
        {
            // The hold is chosen before the count a test waits on.
            var (hold, ignoresCancellation) = (Hold, HoldIgnoresCancellation);
            Interlocked.Increment(ref _singleReads);
            Changed.Pulse();
            Changes.Pulse();
            OperationSignals.Shared.Completed();
            await BeforeReadAsync(hold, ignoresCancellation, token);
            var entry = EntryFor<T>(Store, key);
            Store.Values.TryGetValue(key.Name, out var held);
            Changed.Pulse();
            if (Interlocked.Exchange(ref _afterNextRead, null) is { } afterRead)
            {
                Changes.Pulse();
                OperationSignals.Shared.Completed();
                await afterRead;
            }

            return (entry, held);
        }

        private async ValueTask<KeyValuePair<CacheKey, ICacheEntry<T?>>[]> ReadManyAsync<T>(CacheKey[] keys, CancellationToken token)
        {
            // The hold is chosen before the count a test waits on.
            var (hold, ignoresCancellation) = (Hold, HoldIgnoresCancellation);
            Interlocked.Increment(ref _batchReads);
            Changes.Pulse();
            _batchSizes.Enqueue(keys.Length);
            await BeforeReadAsync(hold, ignoresCancellation, token);
            return [.. keys.Select(k => KeyValuePair.Create<CacheKey, ICacheEntry<T?>>(k, EntryFor<T>(Store, k)))];
        }

        private async Task BeforeReadAsync(Task? hold, bool ignoresCancellation, CancellationToken token)
        {
            if (BeforeFlags is { } before)
            {
                await before.WaitAsync(token);
            }

            _readsOnMaster.Enqueue(_masterDemanded.Value && Volatile.Read(ref _openScopes) != 0);
            if (hold is not null)
            {
                await (ignoresCancellation ? hold : hold.WaitAsync(token));
            }

            if (ReadThrows)
            {
                throw new InvalidOperationException("read failed");
            }
        }

        private sealed record Seen(object? Value);

        private sealed class MasterScope(NodeInner inner) : IDisposable
        {
            private int _disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    inner._masterDemanded.Value = false;
                    Interlocked.Decrement(ref inner._openScopes);
                }
            }
        }
    }
}
