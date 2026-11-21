using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using UiPath.Caching.Config;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests;

[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class AdvertisementRedisTests(RedisFixture fixture) : IAsyncLifetime
{
    private const string Skip = "Set RUN_REDIS_INTEGRATION_TESTS=1 (Redis at 127.0.0.1:6379) to run.";

    private readonly RedisKey _key = $"adv-test:{Guid.NewGuid():N}";
    private ConnectionMultiplexer? _redis;

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
        var store = new RouterAdvertisementStore(() => Db, _key);

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
        var store = new RouterAdvertisementStore(() => Db, _key);
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
        var store = new RouterAdvertisementStore(() => Db, _key);
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
        var store = new RouterAdvertisementStore(() => Db, _key);
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

    [Fact]
    public async Task The_router_from_DI_writes_its_entry_over_the_source_connection_into_the_stack_node_list()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        var identity = $"adv{Guid.NewGuid():N}"[..16];
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Caching:AppShortName"] = "advtest",
            ["Caching:DefaultCache"] = KnownCacheProviderNames.Redis,
            ["Caching:Connections:Redis:ConnectionString"] = fixture.ConnectionString,
            ["Caching:Connections:Redis:WarmUpOnStart"] = "false",
            ["Caching:Connections:Redis:PlannedMaintenanceEnabled"] = "false",
        }).Build();
        var source = new LegacySource(_redis!, "advc");
        var services = new ServiceCollection().AddLogging();
        services.AddCaching(configuration, builder => builder.AddRedisConnection());
        services.AddCachingRouter(configuration, router => router
            .UseLibraryWhen(_ => true)
            .UseEnvelope(new LegacyEnvelopeProxy())
            .UseNodeIdentity(identity)
            .UseSourceConnection(() => Db)
            .Add(new RouterCacheRegistration<List<long>> { Name = "advc", Group = "advg", Source = source, RedisKey = source.RedisKey }));
        await using var provider = services.BuildServiceProvider();
        var router = provider.GetRequiredService<IRouter>();
        router.Should().NotBeNull();
        var key = (RedisKey)"caching:nodes";

        (await UntilAsync(async () => (await Db.HashKeysAsync(key)).Any(f => ((string)f!).Contains(identity, StringComparison.Ordinal) && ((string)f!).EndsWith(RouterAdvertisement.FieldSuffix, StringComparison.Ordinal))))
            .Should().BeTrue();
        foreach (var field in (await Db.HashKeysAsync(key)).Where(f => ((string)f!).Contains(identity, StringComparison.Ordinal)))
        {
            await Db.HashDeleteAsync(key, field);
        }
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
}
