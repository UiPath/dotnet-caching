using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using UiPath.Caching.Config;
using UiPath.Caching.Migration.Tests.Support;
using UiPath.Caching.Redis;

namespace UiPath.Caching.Migration.Tests;

public sealed class RegistrationTests
{
    /// <summary>
    /// Every option and default of the library that the router's stack inherits. A new option or a changed default fails here: decide whether
    /// <see cref="RegistrationValidator"/> needs a rule for it, then update this list.
    /// </summary>
    private static readonly string[] Defaults =
    [
        "Redis.AwaitRefresh=false", "Redis.CacheKeyStrategy=none", "Redis.CacheNullValues=false", "Redis.ConnectionMonitorEnabled=none",
        "Redis.DefaultExpiration=01:00:00", "Redis.Enabled=true", "Redis.EntryFactory=none", "Redis.FormatNegotiation=FormatNegotiationOptions",
        "Redis.HashFieldBatchSize=1000", "Redis.KeyFormat=none", "Redis.KeyPrefix=", "Redis.KeyReadTelemetryEnabled=false", "Redis.MasterReadWindow=00:00:01",
        "Redis.ReadFrom=Master", "Redis.ReadScriptsEnabled=false", "Redis.RedisKeyStrategyFactory=none", "Redis.RemoveTombstone=none", "Redis.RemoveUnreadable=true", "Redis.Timeout=00:00:01",
        "Redis.FormatNegotiation.AdvertiseInterval=00:00:15", "Redis.FormatNegotiation.AssumeAllNodesCanRead=false", "Redis.FormatNegotiation.LivenessWindow=00:02:00",
        "Redis.FormatNegotiation.MaxViewAge=00:02:00", "Redis.FormatNegotiation.Mode=Auto", "Redis.FormatNegotiation.UpgradeHold=01:00:00",
        "Redis.FormatNegotiation.ViewInterval=00:00:15",
        "Caching.AppShortName=none", "Caching.AuditEnabled=true", "Caching.BroadcastEnabled=true", "Caching.ConnectionMonitorEnabled=true",
        "Caching.DefaultCache=InMemoryRedis", "Caching.DefaultCachePolicy=none", "Caching.DefaultTopic=RedisStreams",
        "Caching.DistributedLockMaxPollInterval=00:00:00.5000000", "Caching.DistributedLockPollInterval=00:00:00.0500000", "Caching.Enabled=true",
        "Caching.KeyCasing=Insensitive", "Caching.LargeValueThreshold=20000", "Caching.Policies=0 entries", "Caching.Separator=:", "Caching.ShardKeyEnabled=false",
        "Caching.TelemetryEnabled=true",
        "Policy.BackgroundDistributedWrite=none", "Policy.DistributedExpiration=none", "Policy.FactoryTimeout=none", "Policy.FailSafeCooldown=none",
        "Policy.FailSafeEnabled=none", "Policy.FailSafeMaxStaleAge=none", "Policy.FailSafeTimeout=none", "Policy.JitterMaxDuration=none", "Policy.LocalExpiration=none",
        "Policy.LocalExpirationDisconnected=none", "Policy.Lock=none", "Policy.RefreshLocalOnPeerWrite=none", "Policy.RefreshLocalRetryWindow=none",
        "Policy.Rehydrate=none", "Policy.RehydrateEnabled=none", "Policy.Serializer=none",
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Every_library_option_and_default_is_pinned()
    {
        var actual = RegistrationValidator.Values("Redis", new RedisCacheOptions())
            .Concat(RegistrationValidator.Values("Redis.FormatNegotiation", new FormatNegotiationOptions()))
            .Concat(RegistrationValidator.Values("Caching", new CacheOptions()).Where(value => !value.StartsWith("Caching.SourceUri=", StringComparison.Ordinal)))
            .Concat(RegistrationValidator.Values("Policy", new CachePolicy()));

        actual.Should().Equal(Defaults, "a library option was added or a default changed: decide whether RegistrationValidator needs a rule, then update this list");
    }

    [Fact]
    public void The_router_stack_as_built_breaks_no_rule()
    {
        var stack = new RedisCacheOptions();
        RouterServiceCollectionExtensions.ConfigureStackRedis(stack);
        RegistrationValidator.Violations(new CacheOptions(), stack).Should().BeEmpty("a library default that breaks a rule would show here");
    }

    [Fact]
    public void Each_setting_that_changes_what_the_source_sees_is_named()
    {
        var stack = new RedisCacheOptions
        {
            RedisKeyStrategyFactory = new DefaultRedisKeyStrategyFactory(),
            CacheKeyStrategy = new PrefixCacheKeyStrategy("x"),
            EntryFactory = new CacheEntryFactory(),
            RemoveUnreadable = true,
            ReadFrom = RedisReadFrom.Replica,
            KeyFormat = RedisKeyFormat.Dollar,
        };
        var cache = new CacheOptions
        {
            DefaultCachePolicy = new CachePolicy { Serializer = new SerializerPolicy() },
        };

        var violations = RegistrationValidator.Violations(cache, stack);

        violations.Should().HaveCount(7);
        string[] named =
        [
            nameof(RedisCacheOptions.RedisKeyStrategyFactory), nameof(RedisCacheOptions.CacheKeyStrategy), nameof(RedisCacheOptions.EntryFactory),
            nameof(RedisCacheOptions.RemoveUnreadable), nameof(RedisCacheOptions.ReadFrom), nameof(CachePolicy.Serializer), nameof(RedisCacheOptions.KeyFormat),
        ];
        foreach (var name in named)
        {
            violations.Should().ContainSingle(v => v.Contains(name, StringComparison.Ordinal), name);
        }
    }

    [Fact]
    public async Task An_inherited_setting_that_breaks_a_rule_fails_router_creation()
    {
        await using var provider = Build(("Caching:DefaultCachePolicy:Serializer:Write", "msgpack"));

        var act = () => provider.GetRequiredService<IRouter>();

        act.Should().Throw<InvalidOperationException>().WithMessage("*Serializer*envelope*");
    }

    [Fact]
    public async Task A_host_whose_policy_only_affects_layered_caches_still_builds_the_router()
    {
        await using var provider = Build(
            ("Caching:DefaultCachePolicy:FailSafeEnabled", "true"),
            ("Caching:DefaultCachePolicy:JitterMaxDuration", "00:00:05"),
            ("Caching:DefaultCachePolicy:BackgroundDistributedWrite", "true"));

        provider.GetRequiredService<IRouter>().Should().NotBeNull();
    }

    [Fact]
    public void The_version_is_read_once_and_that_value_names_the_events()
    {
        var calls = 0;
        var registration = new RouterCacheRegistration<Item>
        {
            Name = "items",
            Group = "g",
            Source = new FakeBackend([], new ManualTimeProvider(DateTimeOffset.UnixEpoch)),
            RedisKey = key => key,
            Version = () => ++calls == 1 ? "v1" : "v2",
        };
        var builder = new RouterBuilder().Add(registration);
        var core = new RouterCore(TimeProvider.System, _ => true, NullLogger.Instance);

        var (_, _, cache) = builder.Registrations.Single()(core);

        calls.Should().Be(1);
        ((RoutedCache<Item>)cache).EventName.Should().Be("items@v1");
    }

    [Fact]
    public void An_excluded_cache_is_named_and_asking_for_it_fails_with_the_reason()
    {
        var builder = new RouterBuilder().Exclude("locks", "distributed locks differ between the two clients");
        RouterBanner.Describe(builder, "p").Should().Contain("excluded=locks (distributed locks differ between the two clients)");

        var router = new Router(new RouterCore(TimeProvider.System, _ => true, NullLogger.Instance), Array.Empty<(string, Type, object)>(), builder.Excluded);
        var act = () => router.Get<Item>("locks");

        act.Should().Throw<InvalidOperationException>().WithMessage("*'locks' is excluded*distributed locks differ*");
    }

    [Fact]
    public void A_cache_is_either_registered_or_excluded()
    {
        var source = new FakeBackend([], new ManualTimeProvider(DateTimeOffset.UnixEpoch));
        var registration = new RouterCacheRegistration<Item> { Name = "items", Group = "g", Source = source, RedisKey = key => key };

        var registerExcluded = () => new RouterBuilder().Exclude("items", "r").Add(registration);
        registerExcluded.Should().Throw<ArgumentException>().WithMessage("*excluded*");
        var excludeRegistered = () => new RouterBuilder().Add(registration).Exclude("items", "r");
        excludeRegistered.Should().Throw<ArgumentException>().WithMessage("*registered*");
        var twice = () => new RouterBuilder().Exclude("a", "r").Exclude("a", "r");
        twice.Should().Throw<ArgumentException>();
    }

    private static ServiceProvider Build(params (string Key, string Value)[] extra)
    {
        var app = $"rg{Guid.NewGuid():N}"[..10];
        var settings = new Dictionary<string, string?>
        {
            ["Caching:AppShortName"] = app,
            ["Caching:DefaultCache"] = KnownCacheProviderNames.Redis,
            ["Caching:Connections:Redis:ConnectionString"] = "127.0.0.1:1,abortConnect=false,connectTimeout=100",
            ["Caching:Connections:Redis:WarmUpOnStart"] = "false",
            ["Caching:Connections:Redis:PlannedMaintenanceEnabled"] = "false",
        };
        foreach (var (key, value) in extra)
        {
            settings[key] = value;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var source = new FakeBackend([], new ManualTimeProvider(DateTimeOffset.UnixEpoch));
        var services = new ServiceCollection().AddLogging();
        services.AddCaching(configuration, builder => builder.AddRedisConnection());
        services.AddCachingRouter(
            configuration,
            router => router
                .Add(new RouterCacheRegistration<Item> { Name = "items", Group = "g", Source = source, RedisKey = key => key })
                .UseLibraryWhen(_ => false)
                .UseEnvelope(new LegacyEnvelopeProxy())
                .UseLibrarySettleBound(TimeSpan.FromSeconds(5)));
        _ = Ct;
        return services.BuildServiceProvider();
    }
}
