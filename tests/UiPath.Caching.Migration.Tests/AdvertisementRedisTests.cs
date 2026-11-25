using System.Globalization;
using System.Reflection;
using System.Threading.Channels;
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

    private readonly SignalingTimeProvider _clock = new(DateTimeOffset.UnixEpoch);
    private readonly Channel<RouterAdvertisement> _writes = Channel.CreateUnbounded<RouterAdvertisement>();
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
        var bound = TimeSpan.FromMilliseconds(375);
        var core = new RouterCore(_clock, _ => true, NullLogger.Instance) { StartupClock = _clock, StartBound = bound };
        core.UseLibrary(token => new TaskCompletionSource<LibraryParts>(TaskCreationOptions.RunContinuationsAsynchronously).Task.WaitAsync(token));
        var store = new RouterAdvertisementStore(_ => ValueTask.FromResult(ObserveWrites(Db)), _key);
        await using var router = new Router(core, Array.Empty<(string, Type, object)>()) { Advertiser = Advertiser(store, core) };
        var start = Task.Run(router.Start, Ct);
        await _clock.WaitForTimerWhileRunningAsync(bound, start, Ct);
        start.IsCompleted.Should().BeFalse();
        _clock.Advance(bound);
        await start.WaitAsync(Ct);

        await _writes.Reader.ReadAsync(Ct);
        (await store.ReadAsync(Ct)).Should().ContainSingle("the entry needs no started library");
        router.Mode.Should().Be(RouterMode.BypassLibrary);
        (await store.ReadAsync(Ct))[0].Advertisement.Has("byp").Should().BeTrue();
    }

    [Fact]
    public async Task A_mode_change_shows_at_once_and_a_stop_writes_the_departing_entry()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        var core = new RouterCore(_clock, _ => true, NullLogger.Instance);
        var store = new RouterAdvertisementStore(_ => ValueTask.FromResult(ObserveWrites(Db)), _key);
        await using var router = new Router(core, Array.Empty<(string, Type, object)>()) { Advertiser = Advertiser(store, core) };
        router.Start();
        router.Mode.Should().Be(RouterMode.Library);
        await _writes.Reader.ReadAsync(Ct);
        (await store.ReadAsync(Ct)).Should().ContainSingle();
        (await store.ReadAsync(Ct))[0].Advertisement.Has("byp").Should().BeFalse();

        await router.EnterBypassAsync(Ct);
        (await _writes.Reader.ReadAsync(Ct)).Has("byp").Should().BeTrue("a mode change writes at once, before the next hourly interval");
        (await store.ReadAsync(Ct)).Single().Advertisement.Has("byp").Should().BeTrue();

        await router.StopAsync(Ct);
        (await store.ReadAsync(Ct)).Single().Advertisement.Left.Should().BeTrue("a stopping router writes its departing entry");
    }

    [Fact]
    public async Task A_stale_entry_ages_out_and_is_pruned_while_a_fresh_one_stays()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        var store = new RouterAdvertisementStore(_ => ValueTask.FromResult(Db), _key);
        var core = new RouterCore(_clock, _ => true, NullLogger.Instance);
        var advertiser = new RouterAdvertiser(store, "urn:live", "b", [new BypassTokens(core)], TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50), _clock, NullLogger.Instance);
        var time = await Db.ExecuteAsync("TIME");
        var now = (long.Parse((string)((RedisResult[])time!)[0]!, CultureInfo.InvariantCulture) * 1000) - 60_000;
        await Db.HashSetAsync(_key, "urn:dead#router", $"{now}|kind=router;inst=urn:dead;mig=");

        var (_, _, age, _) = (await store.ReadAsync(Ct)).Single(e => e.Field == "urn:dead#router");
        age.Should().BeGreaterThan(advertiser.Fresh, "a reader no longer counts it");

        advertiser.Start();
        var interval = TimeSpan.FromMilliseconds(50);
        for (var write = 1; write < 10; write++)
        {
            await _clock.WaitForTimerAsync(interval, Ct);
            _clock.Advance(interval);
        }

        await _clock.WaitForTimerAsync(interval, Ct);
        (await Db.HashExistsAsync(_key, "urn:dead#router")).Should().BeFalse("a router prunes entries past the gone age");
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

        await _writes.Reader.ReadAsync(Ct);
        (await Db.HashExistsAsync(key, field)).Should().BeTrue("the entry lands where the stack's node list is, under the stack's identity");
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
                release.Wait(Ct);
            }

            throw new InvalidOperationException("library build failed");
        }

        var identity = $"adv{Guid.NewGuid():N}"[..16];
        await using var provider = Provider(identity, services => services
            .AddKeyedSingleton(RouterServiceCollectionExtensions.StackName, (_, _) => Fail<StackServices>())
            .AddKeyedSingleton(RouterServiceCollectionExtensions.StackName, (_, _) => Fail<IOptions<CacheOptions>>())
            .AddKeyedSingleton(RouterServiceCollectionExtensions.StackName, (_, _) => Fail<IOptions<RedisCacheOptions>>()));
        try
        {
            var router = await Task.Run(() => provider.GetRequiredService<IRouter>(), Ct).WaitAsync(Ct);
            await _writes.Reader.ReadAsync(Ct);
            var fields = (await Db.HashKeysAsync("caching:nodes")).Select(field => (string?)field);
            fields.Should().Contain(field => field != null && field.Contains(identity, StringComparison.Ordinal), "the advertisement needs nothing from the library");
            router.Mode.Should().Be(RouterMode.BypassLibrary);
        }
        finally
        {
            release.Set();
        }

        foreach (var f in (await Db.HashKeysAsync("caching:nodes")).Where(f => ((string?)f)?.Contains(identity, StringComparison.Ordinal) == true))
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
        var old = new RouterAdvertisement("urn:a", "b", []);
        await Db.HashSetAsync(_key, "urn:a#router", $"1|{old.Encode()}");
        var (field, _, _, stamp) = (await store.ReadAsync(Ct)).Single();
        await store.WriteAsync("urn:a#router", new RouterAdvertisement("urn:a", "b", []), Ct);

        await store.PruneAsync([(field, stamp)], Ct);

        (await Db.HashExistsAsync(_key, "urn:a#router")).Should().BeTrue("the stamp changed since the read");
        await store.PruneAsync([(field, (await store.ReadAsync(Ct)).Single().Stamp)], Ct);
        (await Db.HashExistsAsync(_key, "urn:a#router")).Should().BeFalse("a matching stamp is pruned");
    }

    private static RouterAdvertiser Advertiser(RouterAdvertisementStore store, RouterCore core) =>
        new(store, "urn:n1", "b", [new BypassTokens(core)], TimeSpan.FromHours(1), TimeSpan.FromSeconds(1), core.Clock, NullLogger.Instance);

    private IDatabase ObserveWrites(IDatabase database) => InterceptingProxy.Wrap(database, (method, args) =>
        method.Name == nameof(IDatabase.ScriptEvaluateAsync) && args is [string script, RedisKey[] keys, RedisValue[] { Length: 2 } values, CommandFlags flags]
            && (string?)values[1] is { } text && RouterAdvertisement.TryParse(text, out var advertisement)
            ? WriteAsync(script, keys, values, flags, advertisement)
            : InterceptingProxy.Proceed);

    private async Task<RedisResult> WriteAsync(string script, RedisKey[] keys, RedisValue[] values, CommandFlags flags, RouterAdvertisement advertisement)
    {
        var result = await Db.ScriptEvaluateAsync(script, keys, values, flags);
        _writes.Writer.TryWrite(advertisement);
        return result;
    }

    private ServiceProvider Provider(string identity, Action<IServiceCollection>? after = null, bool ownSection = false, string? interval = null)
    {
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
        var services = new ServiceCollection().AddLogging().AddSingleton<TimeProvider>(_clock);
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
                .UseSourceConnection(() => ObserveWrites(Db))
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
