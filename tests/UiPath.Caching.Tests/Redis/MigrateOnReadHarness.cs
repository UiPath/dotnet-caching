using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using UiPath.Caching.Config;
using UiPath.Caching.Negotiation;
using UiPath.Caching.Redis;
using UiPath.Caching.Serialization;
using UiPath.Caching.Telemetry;
using UiPath.Caching.Tests.Fakes;
using UiPath.Caching.Tests.Telemetry;

namespace UiPath.Caching.Tests.Redis;

/// <summary>An app with two registered serializers (<c>fast</c> 0x10, <c>other</c> 0x12) and a policy for each case migrate-on-read has to tell apart.</summary>
internal sealed class MigrateOnReadHarness : IDisposable
{
    public const string Migrating = "mig";
    public const string MigratingOpen = "open";
    public const string Narrow = "narrow";
    public const string NotMigrating = "plain";
    public const string ToJson = "tojson";
    public const string Unset = "unset";

    private readonly ServiceProvider _provider;

    private MigrateOnReadHarness(ServiceProvider provider, RecordingTelemetryProvider telemetry, FormatNegotiator? negotiator, ICacheValueSerializer fast)
    {
        _provider = provider;
        Telemetry = telemetry;
        Negotiator = negotiator;
    }

    public RecordingTelemetryProvider Telemetry { get; }

    public FormatNegotiator? Negotiator { get; }

    public IServiceProvider Services => _provider;

    public FormatMigrator Migrator => _provider.GetRequiredService<ValueMigration>().Migrator;

    /// <summary>Force writes what the policy says; Auto asks the node list, which here holds the format back until <see cref="Switch"/>.</summary>
    public static MigrateOnReadHarness Build(
        string connectionString,
        FormatNegotiationMode mode,
        string app,
        bool layered = false,
        bool scripts = false,
        bool cacheNulls = false,
        Action<ICachingBuilder>? configure = null,
        ICacheValueSerializer? other = null,
        ICacheValueSerializer? fast = null,
        int hashBatch = 1000)
    {
        var telemetry = new RecordingTelemetryProvider();
        fast ??= new PassthroughValueSerializer(0x10);
        var services = new ServiceCollection().AddLogging().AddSingleton<ICachingTelemetryProvider>(telemetry);
        FormatNegotiator? negotiator = null;
        if (mode == FormatNegotiationMode.Auto)
        {
            negotiator = new FormatNegotiator(
                [new DesiredWrite(fast, ["10"], [Migrating, MigratingOpen, Narrow, NotMigrating], [])],
                new FormatNegotiationOptions { UpgradeHold = TimeSpan.Zero },
                TimeProvider.System);
            services.AddSingleton(new FormatNegotiation { Negotiator = negotiator });
        }

        services.AddCaching(
            b =>
            {
                b.AddRedisConnection(o => o.ConnectionString = connectionString);
                b.AddRedis(o =>
                {
                    o.FormatNegotiation.Mode = mode;
                    o.ReadScriptsEnabled = scripts;
                    o.CacheNullValues = cacheNulls;
                    o.HashFieldBatchSize = hashBatch;
                });
                var target = fast;
                b.AddValueSerializer("fast", _ => target);
                b.AddValueSerializer("other", _ => other ?? new PassthroughValueSerializer(0x12));
                if (layered)
                {
                    b.AddInMemoryRedis(_ => { });
                }

                configure?.Invoke(b);
            },
            o =>
            {
                o.AppShortName = app;
                o.Policies[Migrating] = Policy("fast", ["json", "fast", "other"], migrate: true);
                o.Policies[MigratingOpen] = Policy("fast", null, migrate: true);
                o.Policies[Narrow] = Policy("fast", ["json", "fast"], migrate: true);
                o.Policies[NotMigrating] = Policy("fast", ["json", "fast", "other"], migrate: false);
                o.Policies[ToJson] = Policy("json", ["json", "fast", "other"], migrate: true);
                o.Policies[Unset] = Policy("fast", ["json", "fast", "other"], migrate: null);
            });
        var harness = new MigrateOnReadHarness(services.BuildServiceProvider(), telemetry, negotiator, fast);
        return harness;
    }

    public static CachePolicy Policy(string write, string[]? read, bool? migrate) =>
        new()
        {
            DistributedExpiration = TimeSpan.FromMinutes(5),
            Serializer = new SerializerPolicy { Write = write, Read = read, MigrateOnRead = migrate },
        };

    public ICache Cache(bool layered = false) =>
        _provider.GetRequiredService<ICacheFactory>().CreateCache(layered ? KnownCacheProviderNames.InMemoryRedis : KnownCacheProviderNames.Redis);

    public IHashCache Hashes(bool layered = false) =>
        _provider.GetRequiredService<ICacheFactory>().CreateHashCache(layered ? KnownCacheProviderNames.InMemoryRedis : KnownCacheProviderNames.Redis);

    public CachePolicy Resolve(string name) => _provider.GetRequiredService<ICachePolicyFactory>().Resolve(name)!;

    /// <summary>The node list now says every node reads <c>fast</c>, so Auto writes it.</summary>
    public void Switch(bool on = true)
    {
        // Off: a node has joined that reads JSON and nothing else.
        var ads = new List<StoredAdvertisement> { new("me", new NodeAdvertisement("3.2.0", "i", on ? ["json", "10"] : ["json"], new Dictionary<string, IReadOnlyList<string>>()), TimeSpan.Zero) };
        Negotiator!.Observe(new NodeListView(ads, [], Complete: true));
    }

    public int Outcomes(string outcome) =>
        (int)Telemetry.Metrics.Where(m => m.Name == Metrics.SerializerRewrite && m.Properties!["outcome"] == outcome).Sum(m => m.Value);

    public void Dispose() => _provider.Dispose();
}
