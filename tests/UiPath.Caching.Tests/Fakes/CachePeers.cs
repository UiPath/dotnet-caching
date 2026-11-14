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
internal sealed class CachePeers : IDisposable
{
    private static readonly FieldInfo BackgroundField = typeof(MultilayerCacheBase).GetField("_background", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private readonly List<Node> _nodes = [];
    private readonly CacheEventFormatter _formatter = new();

    public SharedStore Store { get; } = new();

    /// <summary>When off, events wait in <see cref="Node.Published"/> for <see cref="Deliver"/>.</summary>
    public bool AutoDeliver { get; set; } = true;

    public Node Add(string name, InMemoryRedisCacheOptions? options = null, RecordingTelemetryProvider? telemetry = null, TimeProvider? clock = null)
    {
        var node = new Node(this, name, options ?? new InMemoryRedisCacheOptions(), telemetry, clock ?? TimeProvider.System);
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
        var until = DateTime.UtcNow.AddSeconds(10);
        while (Outcomes(telemetry) < outcomes && DateTime.UtcNow < until)
        {
            await Task.Delay(5);
            cache.FlushPeerRefreshMetrics();
        }
    }

    /// <summary>Whether a background write of <paramref name="key"/> is in flight, as a peer refresh's keep checks it: false once the write promoted its entry and left its turn.</summary>
    internal static bool WriteInFlight(MultilayerCacheBase cache, string key) =>
        ((BackgroundWrites)BackgroundField.GetValue(cache)!).WriteInFlight(key);

    /// <summary>Polls <paramref name="condition"/> until it holds, for up to 10 s, and fails if it never does.</summary>
    internal static async Task Until(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < until)
        {
            await Task.Delay(5);
        }

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
        private readonly RecordingTelemetryProvider? _telemetry;
        private int _evicted;

        public Node(CachePeers peers, string name, InMemoryRedisCacheOptions options, RecordingTelemetryProvider? telemetry, TimeProvider clock)
        {
            Name = name;
            _telemetry = telemetry;
            var cacheOptions = new CacheOptions { AppShortName = "test", ConnectionMonitorEnabled = true, SourceUri = new Uri($"urn:node:{name}") };
            var inner = DispatchProxy.Create<ICache, NodeInner>();
            Inner = (NodeInner)(object)inner;
            Inner.Store = peers.Store;

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
                new KeptMemoryCacheFactory(this),
                tokens,
                topics,
                events,
                (ICachingTelemetryProvider?)telemetry ?? NullTelemetryProvider.Instance,
                options,
                options,
                cacheOptions,
                NullDistributedLock.Instance,
                NullCachePolicyFactory.Instance,
                clock,
                NullLogger.Instance));
        }

        public string Name { get; }

        public MultilayerCache Cache { get; }

        public NodeInner Inner { get; }

        public KeyedSubject<ICacheEvent> Subject { get; } = new(NullLogger.Instance);

        public List<ICacheEvent> Published { get; } = [];

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
            var until = DateTime.UtcNow.AddSeconds(10);
            while (Cache.RunsInFlight != 0 && DateTime.UtcNow < until)
            {
                await Task.Delay(5);
            }

            // The refresher reports a batch's outcomes as the batch ends, just after its keys settle.
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

        private sealed class KeptMemoryCacheFactory(Node node) : IMemoryCacheFactory
        {
            private readonly MemoryCacheFactory _inner = new(TimeProvider.System, NullLoggerFactory.Instance);

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
                node.BeforeCommit?.Invoke((string)inner.Key);
                foreach (var registration in inner.PostEvictionCallbacks)
                {
                    var callback = registration.EvictionCallback;
                    registration.EvictionCallback = (key, value, reason, state) =>
                    {
                        callback?.Invoke(key, value, reason, state);
                        node.OnEvicted();
                    };
                }

                inner.Dispose();
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
    internal class NodeInner : DispatchProxy, IConnectionState, IMasterReadScope, IAdoptionReadCache
    {
        private static readonly MethodInfo ReadOne = typeof(NodeInner).GetMethod(nameof(ReadOneAsync), BindingFlags.Instance | BindingFlags.NonPublic)!;
        private static readonly MethodInfo ReadMany = typeof(NodeInner).GetMethod(nameof(ReadManyAsync), BindingFlags.Instance | BindingFlags.NonPublic)!;
        private static readonly MethodInfo WriteMany = typeof(NodeInner).GetMethod(nameof(WriteAll), BindingFlags.Static | BindingFlags.NonPublic)!;

        private readonly AsyncLocal<bool> _masterDemanded = new();
        private readonly ConcurrentQueue<bool> _readsOnMaster = new();
        private readonly ConcurrentQueue<int> _batchSizes = new();
        private volatile bool _connected = true;
        private int _openScopes;
        private Task? _afterNextRead;
        private int _singleReads;
        private int _batchReads;

        public event EventHandler? OnConnectionFailed;

        public event EventHandler? OnConnectionRestored;

        public event EventHandler? OnReconnected
        {
            add { }
            remove { }
        }

        public SharedStore Store { get; set; } = null!;

        public int SingleReads => Volatile.Read(ref _singleReads);

        public int BatchReads => Volatile.Read(ref _batchReads);

        public int[] BatchSizes => [.. _batchSizes];

        /// <summary>Per read, in order: whether it ran inside <see cref="IMasterReadScope.DemandMaster"/>, gated as the Redis tier gates it: the flow's flag and a scope still open.</summary>
        public bool[] ReadsOnMaster => [.. _readsOnMaster];

        /// <summary>When set, a read waits for it before it reads its flags, as a command sent after the read's first await does.</summary>
        public Task? BeforeFlags { get; set; }

        /// <summary>When set, a read waits for it before it reaches the store.</summary>
        public Task? Hold { get; set; }

        /// <summary>When set, a held read does not observe its cancellation, as a Redis command in flight does not.</summary>
        public bool HoldIgnoresCancellation { get; set; }

        public bool ReadThrows { get; set; }

        /// <summary>When set, the next single-key read waits for it after it has read the store, then answers with what it read.</summary>
        public Task? AfterNextRead
        {
            get => _afterNextRead;
            set => _afterNextRead = value;
        }

        /// <summary>When set, a removal waits for it before it reaches the store.</summary>
        public Task? RemoveHold { get; set; }

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

        ValueTask<ICacheEntry<T?>> IAdoptionReadCache.ReadForAdoptionAsync<T>(CacheKey cacheKey, CancellationToken token)
            where T : default => ReadOneAsync<T>(cacheKey, token);

        IDisposable IMasterReadScope.DemandMaster()
        {
            _masterDemanded.Value = true;
            Interlocked.Increment(ref _openScopes);
            return new MasterScope(this);
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2012:Use ValueTasks correctly", Justification = "DispatchProxy returns the ValueTask boxed to the proxied call, which awaits it once.")]
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
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
                    return AfterWrite is { } after ? new ValueTask<bool>(after.ContinueWith(static _ => true, TaskScheduler.Default)) : new ValueTask<bool>(true);
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

        private static void WriteAll<T>(SharedStore store, KeyValuePair<CacheKey, T?>[] values)
        {
            foreach (var (key, value) in values)
            {
                store.Values[key.Name] = value;
            }
        }

        private static TestCacheEntry<T?> EntryFor<T>(SharedStore store, CacheKey key) =>
            store.Values.TryGetValue(key.Name, out var value)
                ? new TestCacheEntry<T?> { Value = (T?)value, Expiration = DateTimeOffset.UtcNow + (store.Remaining.TryGetValue(key.Name, out var remaining) ? remaining : TimeSpan.FromHours(1)), Found = true }
                : new TestCacheEntry<T?> { Expiration = DateTimeOffset.MinValue, Found = false };

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

        private async ValueTask<ICacheEntry<T?>> ReadOneAsync<T>(CacheKey key, CancellationToken token)
        {
            // The hold is chosen before the count a test waits on.
            var (hold, ignoresCancellation) = (Hold, HoldIgnoresCancellation);
            Interlocked.Increment(ref _singleReads);
            await BeforeReadAsync(hold, ignoresCancellation, token);
            var entry = EntryFor<T>(Store, key);
            if (Interlocked.Exchange(ref _afterNextRead, null) is { } afterRead)
            {
                await afterRead;
            }

            return entry;
        }

        private async ValueTask<KeyValuePair<CacheKey, ICacheEntry<T?>>[]> ReadManyAsync<T>(CacheKey[] keys, CancellationToken token)
        {
            // The hold is chosen before the count a test waits on.
            var (hold, ignoresCancellation) = (Hold, HoldIgnoresCancellation);
            Interlocked.Increment(ref _batchReads);
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
