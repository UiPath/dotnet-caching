using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;
using StackExchange.Redis.Profiling;
using UiPath.Caching.Broadcast;
using UiPath.Caching.Config;
using UiPath.Caching.Migration.Tests.Support;
using UiPath.Caching.PluginContract.Tests.Acme;
using UiPath.Caching.Redis;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.PluginContract.Tests.Support;

/// <summary>Builds nodes of one app on the shared Redis and deletes every key the app left behind.</summary>
public abstract class AcmeNodeTests(RedisFixture fixture) : IDisposable
{
    protected const string Policy = "acme";
    protected const string Plain = "plain";
    protected const string Narrow = "narrow";
    protected const string Migrating = "migrating";
    protected const string ToBeta = "tobeta";
    protected const string Beta = "beta";
    protected const int BetaId = 0x110;

    private static readonly TimeSpan ViewInterval = TimeSpan.FromMilliseconds(1373);

    private readonly List<ServiceProvider> _nodes = [];
    private readonly List<ServiceProvider> _started = [];

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected RedisFixture Fixture => fixture;

    protected string App { get; } = $"ac{Guid.NewGuid():N}"[..10];

    protected IReadOnlyList<ServiceProvider> Nodes => _nodes;

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected static string Unique() => $"it-{Guid.NewGuid():N}";

    protected static ICache Cache(ServiceProvider node, string provider = KnownCacheProviderNames.Redis) =>
        node.GetRequiredService<ICacheFactory>().CreateCache(provider);

    protected static CachePolicy PolicyOf(ServiceProvider node, string name) => node.GetRequiredService<ICachePolicyFactory>().Resolve(name)!;

    protected static CachePolicy Of(string write, string[]? read, bool? migrate) =>
        new()
        {
            DistributedExpiration = TimeSpan.FromMinutes(5),
            Serializer = new SerializerPolicy { Write = write, Read = read, MigrateOnRead = migrate },
        };

    protected static async Task StartAsync(ServiceProvider node)
    {
        node.GetRequiredService<ITopicFactory>().Get(KnownTopicNames.RedisStreams).Create(new TopicKey("string"));
        foreach (var service in node.GetServices<IHostedService>())
        {
            await service.StartAsync(Ct);
        }
    }

    protected static async Task StopAsync(ServiceProvider node)
    {
        foreach (var service in node.GetServices<IHostedService>())
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    protected static ITopic<ICacheEvent> Topic<T>(ServiceProvider node) =>
        node.GetRequiredService<ITopicFactory>().Get().Create(new DefaultTopicKeyStrategy().GetTopicKey(typeof(T)));

    protected static Task ReadyAsync(ServiceProvider node) => RedisTopicSignals.ReadyAsync(node, Ct);

    protected static Task BarrierAsync<T>(ServiceProvider writer, ServiceProvider peer) =>
        RedisTopicSignals.BarrierAsync(Topic<T>(writer), Topic<T>(peer), Ct);

    protected static async Task AdvanceViewAsync(ServiceProvider node, Task view)
    {
        var time = (SignalingTimeProvider)node.GetRequiredService<TimeProvider>();
        if (!view.IsCompleted)
        {
            var earliest = (ViewInterval * 0.8) - TimeSpan.FromMilliseconds(1);
            var latest = ViewInterval * 1.2;
            await time.WaitForTimerAsync(delay => delay >= earliest && delay <= latest, Ct);
            time.Advance(latest);
        }

        await view.WaitAsync(Ct);
    }

    protected static RedisKey FindKey(ConnectionMultiplexer redis, string key) =>
        redis.GetServer(redis.GetEndPoints()[0]).Keys(pattern: $"*{key}*").Single();

    protected static string Field(string advertisement, string name) =>
        advertisement.Split(';').Single(f => f.StartsWith(name + "=", StringComparison.Ordinal))[(name.Length + 1)..];

    protected virtual void Dispose(bool disposing)
    {
        // Stopped before the providers go: a hosted service left running retries against disposed dependencies.
        foreach (var node in _started)
        {
            StopAsync(node).GetAwaiter().GetResult();
        }

        foreach (var node in _nodes)
        {
            node.Dispose();
        }

        if (!fixture.Enabled)
        {
            return;
        }

        using var redis = ConnectionMultiplexer.Connect(fixture.ConnectionString);
        var db = redis.GetDatabase();
        foreach (var key in redis.GetServer(redis.GetEndPoints()[0]).Keys(pattern: $"{App}:*"))
        {
            db.KeyDelete(key);
        }
    }

    protected async Task<byte[]?> Raw(string key)
    {
        await using var redis = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        return (byte[]?)await redis.GetDatabase().StringGetAsync(FindKey(redis, key));
    }

    protected async Task<byte[]?> RawField(string key, string field)
    {
        await using var redis = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        return await redis.GetDatabase().HashGetAsync(FindKey(redis, key), field);
    }

    /// <summary>The text of each node's advertisement, from <c>lib=</c> on.</summary>
    protected async Task<List<string>> Advertisements()
    {
        await using var redis = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var texts = new List<string>();
        foreach (var key in redis.GetServer(redis.GetEndPoints()[0]).Keys(pattern: $"{App}:*nodes*"))
        {
            foreach (var entry in await redis.GetDatabase().HashGetAllAsync(key))
            {
                var value = (string)entry.Value!;
                texts.Add(value[value.IndexOf("lib=", StringComparison.Ordinal)..]);
            }
        }

        return texts;
    }

    protected async Task Start(ServiceProvider node)
    {
        _started.Add(node);
        await StartAsync(node);
    }

    protected async Task Stop(ServiceProvider node)
    {
        _started.Remove(node);
        await StopAsync(node);
    }

    protected ServiceProvider Track(ServiceProvider provider)
    {
        _nodes.Add(provider);
        return provider;
    }

    protected ServiceProvider Node(NodeSpec spec)
    {
        using var redis = ConnectionMultiplexer.Connect(fixture.ConnectionString);
        var server = redis.GetServer(redis.GetEndPoints()[0]);
        var services = new ServiceCollection().AddLogging().AddSingleton<TimeProvider>(new SignalingTimeProvider(server.Time()));
        if (spec.ObservedTopic is { } topic)
        {
            RedisTopicSignals.Add(services, topic);
        }

        if (spec.Telemetry is not null)
        {
            services.AddSingleton(spec.Telemetry);
        }

        var provider = services
            .AddCaching(
                b =>
                {
                    b.AddRedisConnection(o =>
                    {
                        o.ConnectionString = fixture.ConnectionString;
                        if (spec.Profile is not null)
                        {
                            o.ProfilerEnabled = true;
                            o.ProfilingSessionFactory = () => spec.Profile;
                        }
                    });
                    b.AddRedis(o =>
                    {
                        o.FormatNegotiation.Mode = spec.Mode;
                        o.FormatNegotiation.AdvertiseInterval = ViewInterval;
                        o.FormatNegotiation.ViewInterval = ViewInterval;
                        o.FormatNegotiation.LivenessWindow = TimeSpan.FromSeconds(3);
                        o.FormatNegotiation.UpgradeHold = TimeSpan.Zero;
                        o.FormatNegotiation.MaxViewAge = TimeSpan.FromSeconds(10);
                    });
                    if (spec.Streams)
                    {
                        b.AddRedisStreams(o => { o.Enabled = true; o.MaintainerEnabled = false; });
                    }

                    b.AddBroadcast();
                    b.AddInMemoryRedis(o => o.PeerFieldUpdates = spec.PeerFieldUpdates);
                    if (spec.WithBeta)
                    {
                        b.AddValueSerializer(Beta, [BetaId], _ => new AcmeValueSerializer(ids: [BetaId]));
                    }

                    if (spec.Acme)
                    {
                        b.AddValueSerializer(AcmeFormats.Name, AcmeFormats.ValueIds, _ => spec.Serializer ?? new AcmeValueSerializer(spec.Deterministic));
                        b.AddEventFormat(AcmeFormats.Name, AcmeFormats.Event, _ => spec.Events ?? new AcmeEventFormat(), write: true);
                    }
                },
                o =>
                {
                    o.AppShortName = App;
                    o.SourceUri = new Uri($"urn:{spec.Source}-{App}");
                    if (!spec.Acme)
                    {
                        return;
                    }

                    o.Policies[Policy] = Of(write: AcmeFormats.Name, read: null, migrate: null);
                    o.Policies[Plain] = Of(write: "json", read: null, migrate: true);
                    o.Policies[Narrow] = Of(write: "json", read: ["json"], migrate: null);
                    o.Policies[Migrating] = Of(write: AcmeFormats.Name, read: ["json", AcmeFormats.Name], migrate: true);
                    if (spec.WithBeta)
                    {
                        o.Policies[ToBeta] = Of(write: Beta, read: ["json", AcmeFormats.Name, Beta], migrate: true);
                    }

                    if (spec.DefaultPolicy)
                    {
                        o.DefaultCachePolicy = o.Policies[Policy];
                    }
                })
            .BuildServiceProvider();
        _nodes.Add(provider);
        return provider;
    }

    protected sealed record NodeSpec(string Source)
    {
        public FormatNegotiationMode Mode { get; init; } = FormatNegotiationMode.Force;

        public bool Acme { get; init; } = true;

        public bool WithBeta { get; init; }

        public bool Deterministic { get; init; } = true;

        public bool Streams { get; init; }

        public bool PeerFieldUpdates { get; init; }

        public bool DefaultPolicy { get; init; }

        public AcmeEventFormat? Events { get; init; }

        public AcmeValueSerializer? Serializer { get; init; }

        public ICachingTelemetryProvider? Telemetry { get; init; }

        public ProfilingSession? Profile { get; init; }

        public string? ObservedTopic { get; init; }
    }
}
