using System.Globalization;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using UiPath.Caching.Config;
using UiPath.Caching.Migration.Tests.Support;
using UiPath.Caching.Redis;

namespace UiPath.Caching.Migration.Tests;

[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class AdvertisementRedisTests(RedisFixture fixture) : IAsyncLifetime
{
    private const string Skip = "Set RUN_REDIS_INTEGRATION_TESTS=1 (Redis at 127.0.0.1:6379) to run.";

    private readonly RedisKey _key = $"adv-test:{Guid.NewGuid():N}";
    private ConnectionMultiplexer? _redis;
    private string? _identity;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IDatabase Db => _redis!.GetDatabase();

    public async ValueTask InitializeAsync()
    {
        if (fixture.Enabled)
        {
            _redis = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_redis is not null)
        {
            await Db.KeyDeleteAsync(_key);
            await _redis.DisposeAsync();
        }
    }

    [Fact]
    public async Task Written_entry_reads_back_with_a_server_age()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        var store = new RouterAdvertisementStore(_ => ValueTask.FromResult(Db), _key);

        await store.WriteAsync("urn:a#router", new RouterAdvertisement("urn:a", "b1", ["byp"]), Ct);
        await Db.HashSetAsync(_key, "urn:lib", "1|lib=1;inst=x;vr=json");

        var entries = await store.ReadAsync(Ct);
        entries.Should().ContainSingle();
        entries[0].Field.Should().Be("urn:a#router");
        entries[0].Advertisement.Has("byp").Should().BeTrue();
        entries[0].Age.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_router_whose_library_never_starts_advertises_bypass()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        var core = new RouterCore(TimeProvider.System, _ => true, NullLogger.Instance);
        core.UseLibrary(token => Task.Delay(Timeout.Infinite, token).ContinueWith<LibraryParts>(_ => throw new OperationCanceledException(), TaskScheduler.Default));
        var store = new RouterAdvertisementStore(_ => ValueTask.FromResult(Db), _key);
        await using var router = new Router(core, Array.Empty<(string, Type, object)>()) { Advertiser = Advertiser(store, core) };
        router.Start();

        (await UntilAsync(async () => (await store.ReadAsync(Ct)).Count == 1)).Should().BeTrue("the entry needs no started library");
        router.Mode.Should().Be(RouterMode.BypassLibrary);
        (await store.ReadAsync(Ct))[0].Advertisement.Has("byp").Should().BeTrue();
    }

    [Fact]
    public async Task A_mode_change_shows_at_once_and_a_stop_writes_the_departing_entry()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        var core = new RouterCore(TimeProvider.System, _ => true, NullLogger.Instance);
        var store = new RouterAdvertisementStore(_ => ValueTask.FromResult(Db), _key);
        await using var router = new Router(core, Array.Empty<(string, Type, object)>()) { Advertiser = Advertiser(store, core) };
        router.Start();
        router.Mode.Should().Be(RouterMode.Library);
        (await UntilAsync(async () => (await store.ReadAsync(Ct)).Count == 1)).Should().BeTrue();
        (await store.ReadAsync(Ct))[0].Advertisement.Has("byp").Should().BeFalse();

        await router.EnterBypassAsync(Ct);
        (await UntilAsync(async () => (await store.ReadAsync(Ct)).Single().Advertisement.Has("byp")))
            .Should().BeTrue("a mode change writes at once, not at the next hourly interval");

        await router.StopAsync(Ct);
        (await UntilAsync(async () => (await store.ReadAsync(Ct)).Single().Advertisement.Left)).Should().BeTrue("a stopping router writes its departing entry");
    }

    [Fact]
    public async Task A_stale_entry_ages_out_and_is_pruned_while_a_fresh_one_stays()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        var store = new RouterAdvertisementStore(_ => ValueTask.FromResult(Db), _key);
        var core = new RouterCore(TimeProvider.System, _ => true, NullLogger.Instance);
        var advertiser = new RouterAdvertiser(store, "urn:live", "b", [new BypassTokens(core)], TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50), TimeProvider.System, NullLogger.Instance);
        var time = await Db.ExecuteAsync("TIME");
        var now = (long.Parse((string)((RedisResult[])time!)[0]!, CultureInfo.InvariantCulture) * 1000) - 60_000;
        await Db.HashSetAsync(_key, "urn:dead#router", $"{now}|kind=router;inst=urn:dead;mig=");

        var dead = (await store.ReadAsync(Ct)).Single(e => e.Field == "urn:dead#router");
        dead.Age.Should().BeGreaterThan(advertiser.Fresh, "a reader no longer counts it");

        advertiser.Start();
        (await UntilAsync(async () => !await Db.HashExistsAsync(_key, "urn:dead#router"))).Should().BeTrue("a router prunes entries past the gone age");
        (await Db.HashExistsAsync(_key, advertiser.Field)).Should().BeTrue();
        await advertiser.StopAsync(TimeSpan.FromSeconds(1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_source_database_reaches_the_stack_node_hash_under_the_stack_identity(bool ownSection)
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        await using var provider = Provider($"adv{Guid.NewGuid():N}"[..16], ownSection: ownSection);
        var router = (Router)provider.GetRequiredService<IRouter>();
        var stackCache = provider.GetRequiredKeyedService<IOptions<CacheOptions>>(RouterServiceCollectionExtensions.StackName).Value;
        var stackRedis = provider.GetRequiredKeyedService<IOptions<RedisCacheOptions>>(RouterServiceCollectionExtensions.StackName).Value;
        var key = RouterServiceCollectionExtensions.NodeListKey(stackCache, stackRedis);
        var field = stackCache.SourceUri!.ToString().ToLowerInvariant() + RouterAdvertisement.FieldSuffix;

        (await UntilAsync(async () => await Db.HashExistsAsync(key, field))).Should().BeTrue("the entry lands where the stack's node list is, under the stack's identity");
        router.Advertiser!.Fresh.Should().Be(ownSection ? TimeSpan.FromSeconds(60) : TimeSpan.FromSeconds(150), "the router's section's LivenessWindow plus two intervals");
        if (ownSection)
        {
            ((string)key!).Should().Be("caching$nodes", "the router's section sets '$', whatever the root says");
        }

        await Db.HashDeleteAsync(key, field);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_library_build_that_hangs_or_throws_does_not_hold_or_disable_the_entry(bool hangs)
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        using var release = new ManualResetEventSlim();
        T Fail<T>()
        {
            if (hangs)
            {
                release.Wait(TimeSpan.FromSeconds(60));
            }

            throw new InvalidOperationException("library build failed");
        }

        await using var provider = Provider($"adv{Guid.NewGuid():N}"[..16], services => services
            .AddKeyedSingleton(RouterServiceCollectionExtensions.StackName, (_, _) => Fail<StackServices>())
            .AddKeyedSingleton(RouterServiceCollectionExtensions.StackName, (_, _) => Fail<IOptions<CacheOptions>>())
            .AddKeyedSingleton(RouterServiceCollectionExtensions.StackName, (_, _) => Fail<IOptions<RedisCacheOptions>>()));
        var created = Task.Run(() => provider.GetRequiredService<IRouter>(), Ct);
        (await Task.WhenAny(created, Task.Delay(TimeSpan.FromSeconds(15), Ct))).Should().BeSameAs(created);
        var router = await created;

        (await UntilAsync(async () => (await Db.HashKeysAsync("caching:nodes")).Any(f => ((string)f!).Contains(_identity!, StringComparison.Ordinal))))
            .Should().BeTrue("the advertisement needs nothing from the library");
        router.Mode.Should().Be(RouterMode.BypassLibrary);
        release.Set();
        foreach (var f in (await Db.HashKeysAsync("caching:nodes")).Where(f => ((string)f!).Contains(_identity!, StringComparison.Ordinal)))
        {
            await Db.HashDeleteAsync("caching:nodes", f);
        }
    }

    [Fact]
    public async Task A_non_positive_interval_leaves_the_router_without_an_advertisement()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        await using var provider = Provider($"adv{Guid.NewGuid():N}"[..16], ownSection: true, interval: "00:00:00");

        ((Router)provider.GetRequiredService<IRouter>()).Advertiser.Should().BeNull();
    }

    [Fact]
    public async Task Without_scripts_the_entry_is_written_with_the_server_stamp_and_nothing_is_pruned()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        var refusing = ScriptRefusingDatabase.Over(Db);
        var store = new RouterAdvertisementStore(_ => ValueTask.FromResult(refusing), _key);

        await store.WriteAsync("urn:a#router", new RouterAdvertisement("urn:a", "b", ["byp"]), Ct);
        var entries = await store.ReadAsync(Ct);
        await store.PruneAsync([("urn:a#router", entries.Single().Stamp)], Ct);

        entries.Single().Advertisement.Has("byp").Should().BeTrue();
        entries.Single().Age.Should().BeLessThan(TimeSpan.FromSeconds(5));
        (await Db.HashExistsAsync(_key, "urn:a#router")).Should().BeTrue("without a script the compare and the delete are not atomic, so nothing is pruned");
    }

    [Fact]
    public async Task A_prune_keeps_an_entry_written_again_after_it_was_read()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        var store = new RouterAdvertisementStore(_ => ValueTask.FromResult(Db), _key);
        await store.WriteAsync("urn:a#router", new RouterAdvertisement("urn:a", "b", []), Ct);
        var read = (await store.ReadAsync(Ct)).Single();
        await Task.Delay(5, Ct);
        await store.WriteAsync("urn:a#router", new RouterAdvertisement("urn:a", "b", []), Ct);

        await store.PruneAsync([(read.Field, read.Stamp)], Ct);

        (await Db.HashExistsAsync(_key, "urn:a#router")).Should().BeTrue("the stamp changed since the read");
        await store.PruneAsync([(read.Field, (await store.ReadAsync(Ct)).Single().Stamp)], Ct);
        (await Db.HashExistsAsync(_key, "urn:a#router")).Should().BeFalse("a matching stamp is pruned");
    }

    private static RouterAdvertiser Advertiser(RouterAdvertisementStore store, RouterCore core) =>
        new(store, "urn:n1", "b", [new BypassTokens(core)], TimeSpan.FromHours(1), TimeSpan.FromSeconds(1), TimeProvider.System, NullLogger.Instance);

    private static async Task<bool> UntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(25, Ct);
        }

        return false;
    }

    private ServiceProvider Provider(string identity, Action<IServiceCollection>? after = null, bool ownSection = false, string? interval = null)
    {
        _identity = identity;
        var section = ownSection ? "RouterCaching" : "Caching";
        var settings = new Dictionary<string, string?>
        {
            ["Caching:AppShortName"] = "advtest",
            ["Caching:DefaultCache"] = KnownCacheProviderNames.Redis,
            ["Caching:Connections:Redis:ConnectionString"] = fixture.ConnectionString,
            ["Caching:Connections:Redis:WarmUpOnStart"] = "false",
            ["Caching:Connections:Redis:PlannedMaintenanceEnabled"] = "false",
            ["RouterCaching:AppShortName"] = "advrouter",
            ["RouterCaching:Separator"] = "$",
            ["RouterCaching:SourceUri"] = "urn:router-section",
            ["RouterCaching:Redis:FormatNegotiation:LivenessWindow"] = "00:00:30",
        };
        if (interval is not null)
        {
            settings["RouterCaching:Redis:FormatNegotiation:AdvertiseInterval"] = interval;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var source = new LegacySource(_redis!, "advc");
        var services = new ServiceCollection().AddLogging();
        services.AddCaching(configuration, builder => builder.AddRedisConnection());
        if (ownSection)
        {
            services.Configure<CacheOptions>(options =>
            {
                options.Separator = '|';
                options.SourceUri = new Uri("urn:root-only");
            });
        }

        services.AddCachingRouter(
            configuration,
            router => router
                .UseLibraryWhen(_ => true)
                .UseEnvelope(new LegacyEnvelopeProxy())
                .UseNodeIdentity(identity)
                .UseSourceConnection(() => Db)
                .Add(new RouterCacheRegistration<List<long>> { Name = "advc", Group = "advg", Source = source, RedisKey = source.RedisKey }),
            section);
        after?.Invoke(services);
        return services.BuildServiceProvider();
    }

    /// <summary>A database that refuses every script, as a server with scripting disabled does, and passes every other command through.</summary>
    public class ScriptRefusingDatabase : DispatchProxy
    {
        private IDatabase _inner = default!;

        public static IDatabase Over(IDatabase inner)
        {
            var proxy = Create<IDatabase, ScriptRefusingDatabase>();
            ((ScriptRefusingDatabase)(object)proxy)._inner = inner;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod!.Name.StartsWith("ScriptEvaluate", StringComparison.Ordinal)
                ? throw new RedisCommandException("NOPERM this user has no permissions to run the 'eval' command")
                : targetMethod.Invoke(_inner, args);
    }
}
