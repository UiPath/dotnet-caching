using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using UiPath.Caching.Config;
using UiPath.Caching.Locking;
using UiPath.Caching.Telemetry;
using UiPath.Caching.Tests.Broadcast;

namespace UiPath.Caching.Tests.Fakes;

/// <summary>Real multilayer hash caches, one per node, over one stand-in for Redis that counts a hash's writes, with a bus that carries their events as JSON, as the wire does.</summary>
internal sealed class PeerCluster : IDisposable
{
    private readonly List<Node> _nodes = [];
    private readonly CacheEventFormatter _formatter = new();

    public Store Redis { get; } = new();

    /// <summary>When off, events wait in <see cref="Node.Published"/> for <see cref="Deliver"/>.</summary>
    public bool AutoDeliver { get; set; } = true;

    /// <param name="monitorsConnection">The node follows <see cref="PeerInner.IsConnected"/>, and keeps its local tier when the connection returns.</param>
    public Node Add(string name, bool peerFieldUpdates = true, int maxFields = 16, int maxBytes = 1024, bool monitorsConnection = false, Telemetry.RecordingTelemetryProvider? telemetry = null)
    {
        var node = new Node(this, name, peerFieldUpdates, maxFields, maxBytes, monitorsConnection, telemetry);
        _nodes.Add(node);
        return node;
    }

    public void Deliver(Node to, ICacheEvent @event) =>
        to.Subject.OnNext(_formatter.Decode(_formatter.Encode(@event))!);

    public void Dispose()
    {
        foreach (var node in _nodes)
        {
            node.Dispose();
        }
    }

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

    internal sealed class Store
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, Hash> _hashes = [];

        /// <summary>Whether a read comes with the hash's write count, as Redis returns the version field.</summary>
        public bool ReadsVersions { get; set; } = true;

        public int CountedWrites { get; private set; }

        /// <summary>Off, a counted write that creates the hash counts from one, as a node that draws no epoch does.</summary>
        public bool Epochs { get; set; } = true;

        public Func<long> NextEpoch { get; set; } = HashVersion.NewEpochSeed;

        public IDictionary<string, string?>? Fields()
        {
            lock (_gate)
            {
                return _hashes.Count == 0 ? null : new Dictionary<string, string?>(_hashes.Values.Single().Fields);
            }
        }

        public string Key()
        {
            lock (_gate)
            {
                return _hashes.Keys.Single();
            }
        }

        public long Version()
        {
            lock (_gate)
            {
                return _hashes.Values.Single().Version;
            }
        }

        public DateTimeOffset Expiration()
        {
            lock (_gate)
            {
                return _hashes.Values.Single().Expiration;
            }
        }

        /// <summary>A write nobody is told of: another node whose event this node does not get.</summary>
        public long WriteQuietly(params (string Field, string Value)[] fields)
        {
            lock (_gate)
            {
                var hash = _hashes.Values.Single();
                foreach (var (field, value) in fields)
                {
                    hash.Fields[field] = value;
                }

                return ++hash.Version;
            }
        }

        public VersionedWrite Write(string key, IDictionary<string, string?> values, HashCacheEntryOptions options, PeerFieldLimits limits, bool counted)
        {
            lock (_gate)
            {
                var merge = options.SetOption == HashCacheSetOption.HashReplace;
                if (!_hashes.TryGetValue(key, out var hash) || !merge)
                {
                    hash = _hashes[key] = new Hash();
                }

                foreach (var (field, value) in values)
                {
                    hash.Fields[field] = value;
                }

                hash.Expiration = options.ExpireTime ?? DateTimeOffset.MaxValue;
                if (options.Metadata is not null)
                {
                    hash.Metadata = options.Metadata;
                }

                if (!counted)
                {
                    return new VersionedWrite(true, 0, null);
                }

                CountedWrites++;
                if (hash.Version == 0 && Epochs)
                {
                    hash.Version = NextEpoch();
                }

                hash.Version++;
                var carried = values.ToDictionary(f => f.Key, f => (string?)Convert.ToBase64String(Encoding.UTF8.GetBytes(f.Value ?? string.Empty)));
                var bytes = values.Sum(f => Encoding.UTF8.GetByteCount(f.Key) + Encoding.UTF8.GetByteCount(f.Value ?? string.Empty));
                var fits = merge && values.Count > 0 && values.Count <= limits.MaxFields && bytes <= limits.MaxBytes;
                return new VersionedWrite(true, hash.Version, fits ? carried : null);
            }
        }

        public void WriteUncounted(string key, IDictionary<string, string?> values, HashCacheEntryOptions options)
        {
            lock (_gate)
            {
                var keepVersion = options.SetOption == HashCacheSetOption.HashReplace && _hashes.TryGetValue(key, out var held) ? held.Version : 0;
                Write(key, values, options, default, counted: false);
                _hashes[key].Version = keepVersion;
            }
        }

        public void RemoveFields(string key, string[] fields, bool counted)
        {
            lock (_gate)
            {
                if (!_hashes.TryGetValue(key, out var hash))
                {
                    return;
                }

                if (counted)
                {
                    CountedWrites++;
                    if (hash.Version == 0 && Epochs)
                    {
                        hash.Version = NextEpoch();
                    }

                    hash.Version++;
                }

                foreach (var field in fields)
                {
                    hash.Fields.Remove(field);
                }

                // Redis deletes a hash whose last field goes, and the count with it.
                if (hash.Fields.Count == 0)
                {
                    _hashes.Remove(key);
                }
            }
        }

        public void SetMetadata(IDictionary<string, string?> metadata)
        {
            lock (_gate)
            {
                _hashes.Values.Single().Metadata = metadata;
            }
        }

        public bool Remove(string key)
        {
            lock (_gate)
            {
                return _hashes.Remove(key);
            }
        }

        public ICacheEntry<IDictionary<string, string?>> Read(string key)
        {
            lock (_gate)
            {
                if (!_hashes.TryGetValue(key, out var hash))
                {
                    return new TestCacheEntry<IDictionary<string, string?>>();
                }

                var entry = new TestCacheEntry<IDictionary<string, string?>>
                {
                    Value = new Dictionary<string, string?>(hash.Fields),
                    Expiration = hash.Expiration,
                    Metadata = hash.Metadata,
                };
                if (ReadsVersions && hash.Version > 0)
                {
                    HashVersions.Stamp<string>(entry, hash.Version, null);
                }

                return entry;
            }
        }

        private sealed class Hash
        {
            public Dictionary<string, string?> Fields { get; } = [];

            public long Version { get; set; }

            public DateTimeOffset Expiration { get; set; } = DateTimeOffset.MaxValue;

            public IDictionary<string, string?>? Metadata { get; set; }
        }
    }

    internal sealed class Node : IDisposable
    {
        public Node(PeerCluster cluster, string name, bool peerFieldUpdates, int maxFields, int maxBytes, bool monitorsConnection, Telemetry.RecordingTelemetryProvider? telemetry)
        {
            Name = name;
            var options = new InMemoryRedisCacheOptions { PeerFieldUpdates = peerFieldUpdates, PeerFieldUpdateMaxFields = maxFields, PeerFieldUpdateMaxBytes = maxBytes, ClearLocalOnReconnect = monitorsConnection ? false : null };
            var cacheOptions = new CacheOptions { AppShortName = "test", ConnectionMonitorEnabled = monitorsConnection, SourceUri = new Uri($"urn:node:{name}") };
            var inner = DispatchProxy.Create<IHashCache, PeerInner>();
            Inner = (PeerInner)(object)inner;
            Inner.Store = cluster.Redis;
            Inner.Limits = new PeerFieldLimits(maxFields, maxBytes);

            var events = new CacheEventFactory(Options.Create(cacheOptions));
            var tokens = new ChangeTokenFactory<byte[]>(Options.Create(cacheOptions), new SystemJsonByteSerializerProxy(), NullLoggerFactory.Instance, Substitute.For<ICachingTelemetryProvider>());
            Topic = new NodeTopic(this, cluster);
            var provider = Substitute.For<ITopicProvider>();
            provider.Enabled.Returns(true);
            provider.Create(Arg.Any<TopicKey>()).Returns(Topic);
            var topics = Substitute.For<ITopicFactory>();
            topics.Get(Arg.Any<string?>()).Returns(provider);
            Cache = RunTables.Track(new MultilayerHashCache(
                KnownCacheProviderNames.InMemoryRedis,
                inner,
                new KeptMemoryCacheFactory(this),
                tokens,
                topics,
                events,
                (ICachingTelemetryProvider?)telemetry ?? Substitute.For<ICachingTelemetryProvider>(),
                options,
                options,
                cacheOptions,
                NullDistributedLock.Instance,
                NullCachePolicyFactory.Instance,
                TimeProvider.System,
                NullLogger.Instance));
        }

        public string Name { get; }

        public MultilayerHashCache Cache { get; }

        public PeerInner Inner { get; }

        public KeyedSubject<ICacheEvent> Subject { get; } = new(NullLogger.Instance);

        public List<ICacheEvent> Published { get; } = [];

        public int InnerReads => Inner.Reads;

        /// <summary>The node's local tier.</summary>
        public IMemoryCache Local { get; private set; } = null!;

        private NodeTopic Topic { get; }

        public async Task Settled()
        {
            var until = DateTime.UtcNow.AddSeconds(10);
            while (Cache.RunsInFlight != 0 && DateTime.UtcNow < until)
            {
                await Task.Delay(5);
            }
        }

        public void Dispose()
        {
            Cache.Dispose();
            Subject.Dispose();
        }

        private sealed class KeptMemoryCacheFactory(Node node) : IMemoryCacheFactory
        {
            private readonly MemoryCacheFactory _inner = new(TimeProvider.System, NullLoggerFactory.Instance);

            public IMemoryCache Get(IMemoryCacheOptions memoryOptions) => node.Local = _inner.Get(memoryOptions);
        }

        private sealed class NodeTopic(Node node, PeerCluster cluster) : ITopic<ICacheEvent>
        {
            public TopicKey TopicKey { get; } = new("topic");

            public EventHandler? OnDisposed { get; set; }

            public IDisposable Subscribe(IObserver<ICacheEvent> observer) => node.Subject.Subscribe(observer);

            public ValueTask<bool> PublishAsync(ICacheEvent @event, CancellationToken token = default) => cluster.Publish(node, @event);

            public void Dispose()
            {
            }
        }
    }

    /// <summary>The inner tier of a node: a stand-in for the Redis hash cache, which proxies cannot be made to look like because its counting interface is internal.</summary>
    internal class PeerInner : DispatchProxy, IVersionedHashCache, ISpanKeyHashCache, IConnectionState
    {
        private volatile bool _connected = true;
        private int _reads;
        private int _countedWrites;
        private int _plainWrites;

        public event EventHandler? OnConnectionFailed;

        public event EventHandler? OnConnectionRestored;

        public event EventHandler? OnReconnected
        {
            add { }
            remove { }
        }

        public Store Store { get; set; } = null!;

        public PeerFieldLimits Limits { get; set; }

        public int Reads => Volatile.Read(ref _reads);

        public int CountedWrites => Volatile.Read(ref _countedWrites);

        public int PlainWrites => Volatile.Read(ref _plainWrites);

        /// <summary>When set, a counted write waits for it before it reaches the store.</summary>
        public Task? Hold { get; set; }

        /// <summary>When set, a field removal waits for it before it reaches the store.</summary>
        public Task? RemoveHold { get; set; }

        /// <summary>When set, a read is counted at once and reaches the store once it completes.</summary>
        public Task? ReadHold { get; set; }

        /// <summary>When set, a held read takes the store's state at once and answers it once the hold completes, as a reply in flight does.</summary>
        public bool HeldReadSnapshots { get; set; }

        /// <summary>Read only by a node that monitors its connection.</summary>
        public bool IsConnected
        {
            get => _connected;
            set
            {
                _connected = value;
                (value ? OnConnectionRestored : OnConnectionFailed)?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>When set, a counted write waits for the task it returns, if any, after it reached the store and before it answers.</summary>
        public Func<IDictionary<string, string?>, Task?>? AfterWrite { get; set; }

        /// <summary>When set, a counted write does not reach the store and answers that it was not written.</summary>
        public bool Refuses { get; set; }

        /// <summary>When set, a counted write reaches the store and is counted, yet answers that it was not written.</summary>
        public bool ReportsUnwritten { get; set; }

        /// <summary>When set, maps the version a counted write took to the one it answers with.</summary>
        public Func<long, long>? ReportsVersion { get; set; }

        /// <summary>When set, the fields an event carries do not decode.</summary>
        public bool FailsDecode { get; set; }

        public int CountedRemovals { get; private set; }

        public async ValueTask<VersionedWrite> SetVersionedAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, HashCacheEntryOptions options, CachePolicy? policy, PeerFieldLimits limits, CancellationToken token = default)
        {
            Interlocked.Increment(ref _countedWrites);
            if (Hold is { } hold)
            {
                await hold.WaitAsync(token);
            }

            if (Refuses)
            {
                return default;
            }

            var written = Store.Write(cacheKey.Name, (IDictionary<string, string?>)values, options, limits, counted: true);
            if (AfterWrite?.Invoke((IDictionary<string, string?>)values) is { } after)
            {
                await after.WaitAsync(token);
            }

            if (ReportsVersion is { } report)
            {
                written = written with { Version = report(written.Version) };
            }

            return ReportsUnwritten ? written with { Written = false } : written;
        }

        public ValueTask<bool> RemoveFieldsVersionedAsync<T>(CacheKey cacheKey, string[] fields, CancellationToken token = default)
        {
            CountedRemovals++;
            return RemoveFieldsAfter(cacheKey.Name, fields, counted: true);
        }

        ValueTask<bool> ISpanKeyHashCache.RemoveFieldsAsync<T>(CacheKey cacheKey, string[] fields, CancellationToken token) =>
            RemoveFieldsAfter(cacheKey.Name, fields, counted: false);

        public bool TryDecodeFields<T>(IDictionary<string, string?> encoded, [MaybeNullWhen(false)] out Dictionary<string, T?> values)
        {
            if (FailsDecode)
            {
                values = null;
                return false;
            }

            values = (Dictionary<string, T?>)(object)encoded.ToDictionary(f => f.Key, f => (string?)Encoding.UTF8.GetString(Convert.FromBase64String(f.Value!)));
            return true;
        }

        // The multilayer cache removes fields only through a span-key cache; the versioned removal above is the one it calls.
        ValueTask<T?> ISpanKeyHashCache.GetItemAsync<T>(Span<char> cacheKey, string field, CachePolicy? policy, CancellationToken token) where T : default => throw new NotSupportedException();

        ValueTask<IDictionary<string, T?>> ISpanKeyHashCache.GetAsync<T>(Span<char> cacheKey, CachePolicy? policy, CancellationToken token) where T : default => throw new NotSupportedException();

        ValueTask<ICacheEntry<IDictionary<string, T?>>> ISpanKeyHashCache.GetCacheEntryAsync<T>(Span<char> cacheKey, CachePolicy? policy, CancellationToken token) where T : default => throw new NotSupportedException();

        ValueTask<IDictionary<string, T?>> ISpanKeyHashCache.GetOrAddAsync<T>(Span<char> cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, CachePolicy? policy, CancellationToken token) where T : default => throw new NotSupportedException();

        ValueTask<IDictionary<string, T?>> ISpanKeyHashCache.GetOrAddAsync<T>(Span<char> cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token) where T : default => throw new NotSupportedException();

        ValueTask<IDictionary<string, T?>> ISpanKeyHashCache.GetOrAddAsync<T>(Span<char> cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token) where T : default => throw new NotSupportedException();

        ValueTask<IDictionary<string, T?>> ISpanKeyHashCache.GetOrAddAsync<T>(Span<char> cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, HashCacheSetOption? setOption, CachePolicy? policy, CancellationToken token) where T : default => throw new NotSupportedException();

        ValueTask<bool> ISpanKeyHashCache.ContainsAsync<T>(Span<char> cacheKey, CancellationToken token) => throw new NotSupportedException();

        ValueTask<bool> ISpanKeyHashCache.SetAsync<T>(Span<char> cacheKey, IDictionary<string, T?> values, CachePolicy? policy, CancellationToken token) where T : default => throw new NotSupportedException();

        ValueTask<bool> ISpanKeyHashCache.SetAsync<T>(Span<char> cacheKey, IDictionary<string, T?> values, TimeSpan expiration, CachePolicy? policy, CancellationToken token) where T : default => throw new NotSupportedException();

        ValueTask<bool> ISpanKeyHashCache.SetAsync<T>(Span<char> cacheKey, IDictionary<string, T?> values, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token) where T : default => throw new NotSupportedException();

        ValueTask<bool> ISpanKeyHashCache.SetAsync<T>(Span<char> cacheKey, IDictionary<string, T?> values, HashCacheEntryOptions options, CachePolicy? policy, CancellationToken token) where T : default => throw new NotSupportedException();

        ValueTask<IDictionary<string, T?>> ISpanKeyHashCache.GetAsync<T>(Span<char> cacheKey, string[] fields, CachePolicy? policy, CancellationToken token) where T : default => throw new NotSupportedException();

        ValueTask<IDictionary<string, T?>> ISpanKeyHashCache.GetOrAddAsync<T, TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, CachePolicy? policy, CancellationToken token) where T : default => throw new NotSupportedException();

        ValueTask<IDictionary<string, T?>> ISpanKeyHashCache.GetOrAddAsync<T, TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token) where T : default => throw new NotSupportedException();

        ValueTask<IDictionary<string, T?>> ISpanKeyHashCache.GetOrAddAsync<T, TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token) where T : default => throw new NotSupportedException();

        ValueTask<IDictionary<string, T?>> ISpanKeyHashCache.GetOrAddAsync<T, TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, HashCacheSetOption? setOption, CachePolicy? policy, CancellationToken token) where T : default => throw new NotSupportedException();

        ValueTask<TimeSpan?> ISpanKeyHashCache.TimeToLiveAsync<T>(Span<char> cacheKey, CancellationToken token) => throw new NotSupportedException();

        ValueTask<DateTimeOffset?> ISpanKeyHashCache.ExpireTimeAsync<T>(Span<char> cacheKey, CancellationToken token) => throw new NotSupportedException();

        ValueTask<IDictionary<string, string?>?> ISpanKeyHashCache.GetMetadataAsync<T>(Span<char> cacheKey, CancellationToken token) => throw new NotSupportedException();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod!.Name)
            {
                case "get_Name":
                    return "Redis";
                case "Dispose":
                    return null;
                case nameof(IHashCache.GetCacheEntryAsync):
                    Interlocked.Increment(ref _reads);
                    var key = ((CacheKey)args![0]!).Name;
                    return ReadHold is { } hold
                        ? new ValueTask<ICacheEntry<IDictionary<string, string?>>>(ReadAfterAsync(hold, key))
                        : new ValueTask<ICacheEntry<IDictionary<string, string?>>>(Store.Read(key));
                case nameof(IHashCache.SetMetadataAsync):
                    Store.SetMetadata((IDictionary<string, string?>)args![1]!);
                    return new ValueTask<bool>(true);
                case nameof(IHashCache.ExpireTimeAsync):
                    return new ValueTask<DateTimeOffset?>(Store.Expiration());
                case nameof(IHashCache.RemoveAsync):
                    return new ValueTask<bool>(Store.Remove(((CacheKey)args![0]!).Name));
                case nameof(IHashCache.SetAsync):
                    Interlocked.Increment(ref _plainWrites);
                    Store.WriteUncounted(((CacheKey)args![0]!).Name, (IDictionary<string, string?>)args[1]!, args.OfType<HashCacheEntryOptions>().Single());
                    return new ValueTask<bool>(true);
                default:
                    throw new NotSupportedException(targetMethod.Name);
            }
        }

        private ValueTask<bool> RemoveFieldsAfter(string key, string[] fields, bool counted)
        {
            if (RemoveHold is not { } hold)
            {
                Store.RemoveFields(key, fields, counted);
                return new ValueTask<bool>(true);
            }

            return new ValueTask<bool>(LaterAsync());

            async Task<bool> LaterAsync()
            {
                await hold;
                Store.RemoveFields(key, fields, counted);
                return true;
            }
        }

        private async Task<ICacheEntry<IDictionary<string, string?>>> ReadAfterAsync(Task hold, string key)
        {
            var snapshot = HeldReadSnapshots ? Store.Read(key) : null;
            await hold;
            return snapshot ?? Store.Read(key);
        }
    }
}
