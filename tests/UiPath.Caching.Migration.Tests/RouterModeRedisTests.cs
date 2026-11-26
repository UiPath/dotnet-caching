using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using UiPath.Caching.Broadcast;
using UiPath.Caching.Config;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests;

[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class RouterModeRedisTests(RedisFixture fixture) : IAsyncLifetime
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private readonly string _cacheName = $"Rm{Guid.NewGuid():N}"[..12];
    private readonly string _app = $"rm{Guid.NewGuid():N}"[..10];
    private readonly List<ServiceProvider> _nodes = [];
    private DateTimeOffset _serverTime;
    private ConnectionMultiplexer? _redis;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IDatabase Db => _redis!.GetDatabase();

    public async ValueTask InitializeAsync()
    {
        if (fixture.Enabled)
        {
            _redis = await ConnectionMultiplexer.ConnectAsync($"{fixture.ConnectionString},allowAdmin=true");
            _serverTime = await _redis.GetServer(_redis.GetEndPoints()[0]).TimeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var node in _nodes)
        {
            await node.DisposeAsync();
        }

        if (_redis is not null)
        {
            foreach (var key in _redis.GetServer(_redis.GetEndPoints()[0]).Keys(pattern: $"n:{_cacheName},c:*"))
            {
                await Db.KeyDeleteAsync(key);
            }

            await _redis.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(KnownTopicNames.RedisStreams)]
    [InlineData(KnownTopicNames.RedisPubSub)]
    public async Task Two_routers_on_one_host_with_different_process_ids_invalidate_each_other(string transport)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Redis at 127.0.0.1:6379) to run.");
        var readerNode = NewNode("one", transport: transport);
        var writerNode = NewNode("two", transport: transport);
        var reader = Cache(readerNode);
        var writer = Cache(writerNode);
        await Task.WhenAll(ReadyAsync(readerNode), ReadyAsync(writerNode));
        await reader.SetAsync("k", [1], Lifetime, Ct);
        (await reader.GetAsync("k", Ct)).Should().Equal(1);
        await Db.KeyDeleteAsync(RedisKeyOf("k"));
        (await reader.GetAsync("k", Ct)).Should().BeEquivalentTo(new long[] { 1 }, "only the local copy can answer now");

        var writerSource = ((Router)writerNode.GetRequiredService<IRouter>()).Library!.Source;
        await RedisTopicSignals.ObserveChangeAsync(
            Topic(writerNode),
            Topic(readerNode),
            () => writer.SetAsync("k", [2], Lifetime, Ct).AsTask(),
            Ct,
            message => message.Key == "k" && message.Source == writerSource);
        (await reader.GetAsync("k", Ct)).Should().Equal([2L], "the other process on the same host is a peer, so its event evicted the stale copy");
    }

    [Fact]
    public async Task Bypass_serves_from_the_source_and_leaving_it_waits_for_the_real_subscription_and_serves_the_tier_again()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Redis at 127.0.0.1:6379) to run.");
        var node = NewNode("only");
        var router = node.GetRequiredService<IRouter>();
        var cache = Cache(node);
        await ReadyAsync(node);
        await PublishedAsync(node, node);
        await cache.SetAsync("k", [1], Lifetime, Ct);
        (await cache.GetAsync("k", Ct)).Should().Equal(1);
        await Db.KeyDeleteAsync(RedisKeyOf("k"));
        (await cache.GetAsync("k", Ct)).Should().BeEquivalentTo(new long[] { 1 }, "served by the shared tier");

        await router.EnterBypassAsync(Ct);

        router.Mode.Should().Be(RouterMode.BypassLibrary);
        (await cache.GetAsync("k", Ct)).Should().BeNull("bypass reads the source, not the tier");
        await cache.SetAsync("b", [3], Lifetime, Ct);
        (await Db.StringGetAsync(RedisKeyOf("b"))).HasValue.Should().BeTrue();
        await Db.KeyDeleteAsync(RedisKeyOf("b"));
        (await cache.GetAsync("b", Ct)).Should().BeNull("no local copy was filled in bypass");

        await router.LeaveBypassAsync(Ct);

        router.Mode.Should().Be(RouterMode.Library);
        (await cache.GetAsync("k", Ct)).Should().BeNull("the tier was cleared on the way out");
        await cache.SetAsync("k", [4], Lifetime, Ct);
        (await cache.GetAsync("k", Ct)).Should().Equal(4);
        await Db.KeyDeleteAsync(RedisKeyOf("k"));
        (await cache.GetAsync("k", Ct)).Should().BeEquivalentTo(new long[] { 4 }, "the tier serves again");
    }

    [Fact]
    public async Task A_library_that_was_held_back_by_the_flag_starts_in_the_background_and_the_router_leaves_bypass()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Redis at 127.0.0.1:6379) to run.");
        var bypass = true;
        var started = new LibraryStartedSignal();
        var node = NewNode("flagged", bypassFlag: () => bypass, logs: started);
        var router = node.GetRequiredService<IRouter>();
        var cache = Cache(node);
        router.Mode.Should().Be(RouterMode.BypassLibrary);
        router.ModeReason.Should().Be(RouterModeReason.Flag);
        await cache.SetAsync("k", [1], Lifetime, Ct);
        (await Db.StringGetAsync(RedisKeyOf("k"))).HasValue.Should().BeTrue("the source wrote it");

        bypass = false;
        await cache.GetAsync("k", Ct);
        var clock = (SignalingTimeProvider)node.GetRequiredService<TimeProvider>();
        var delay = TimeSpan.FromSeconds(1);
        await clock.WaitForTimerAsync(delay, Ct);
        clock.Advance(delay);
        // The consumer group is created while the library attaches, before the router publishes it, so the group alone does not mean the library is there.
        await started.Task.WaitAsync(Ct);
        await ReadyAsync(node);
        await PublishedAsync(node, node);
        var left = RouterTestBase.WaitForLibraryAsync(router, Ct);
        await clock.AdvanceUntilAsync(left, TimeSpan.FromMilliseconds(100), Ct);
        router.Mode.Should().Be(RouterMode.Library, "the library started and the leave order completed");
        await cache.SetAsync("l", [2], Lifetime, Ct);
        (await cache.GetAsync("l", Ct)).Should().NotBeNull();
        await Db.KeyDeleteAsync(RedisKeyOf("l"));
        (await cache.GetAsync("l", Ct)).Should().BeEquivalentTo(new long[] { 2 }, "the library path fills the shared tier");
    }

    [Fact]
    public async Task A_router_that_stops_removes_its_own_consumer_groups_and_leaves_the_others()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Redis at 127.0.0.1:6379) to run.");
        var stopping = NewNode("stopping");
        var staying = NewNode("staying");
        await Task.WhenAll(ReadyAsync(stopping), ReadyAsync(staying));
        await Cache(stopping).SetAsync("k", [1], Lifetime, Ct);
        var own = GroupOf(stopping);
        var other = GroupOf(staying);
        (await GroupsAsync()).Should().Contain([own, other]);

        await new RouterHostedService(stopping).StopAsync(Ct);

        var after = await GroupsAsync();
        after.Should().NotContain(own, "the stopped router's group is destroyed");
        after.Should().Contain(other, "another router's group is not touched");
    }

    private static string GroupOf(ServiceProvider node) =>
        node.GetRequiredKeyedService<Microsoft.Extensions.Options.IOptions<CacheOptions>>(RouterServiceCollectionExtensions.StackName).Value.SourceUri!.ToString().ToLowerInvariant();

    private static ICache<List<long>> Cache(ServiceProvider node) => node.GetRequiredService<IRouter>().Get<List<long>>("lists");

    private static ITopic<ICacheEvent> Topic(ServiceProvider node) =>
        ((Router)node.GetRequiredService<IRouter>()).Library!.Topics.Create(((IRouterParticipant)Cache(node)).Topic!.Value);

    private static Task PublishedAsync(ServiceProvider publisher, ServiceProvider subscriber) =>
        RedisTopicSignals.BarrierAsync(Topic(publisher), Topic(subscriber), Ct);

    private static Task ReadyAsync(ServiceProvider node) => node.GetRequiredService<RedisTopicSignals>().WaitAsync(Ct);

    private string RedisKeyOf(string key) => $"n:{_cacheName},c:{key}";

    private ServiceProvider NewNode(string name, Func<bool>? bypassFlag = null, string transport = KnownTopicNames.RedisStreams, ILoggerProvider? logs = null)
    {
        var pubSub = transport == KnownTopicNames.RedisPubSub;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Caching:AppShortName"] = _app,
            ["Caching:SourceUri"] = $"urn:router-host-{_app}",
            ["Caching:DefaultCache"] = KnownCacheProviderNames.Redis,
            ["Caching:Connections:Redis:ConnectionString"] = $"{fixture.ConnectionString},name=rm{name}",
            ["Caching:Connections:Redis:WarmUpOnStart"] = "false",
            ["Caching:Connections:Redis:PlannedMaintenanceEnabled"] = "false",
            ["Caching:DefaultTopic"] = transport,
            ["Caching:Broadcast:RedisPubSub:Enabled"] = pubSub ? "true" : "false",
            ["Caching:Broadcast:RedisStreams:Enabled"] = pubSub ? "false" : "true",
        }).Build();
        var source = new LegacySource(_redis!, _cacheName);
        var services = new ServiceCollection().AddLogging(builder =>
        {
            if (logs is not null)
            {
                builder.AddProvider(logs);
            }
        }).AddSingleton<TimeProvider>(new SignalingTimeProvider(_serverTime));
        RedisTopicSignals.Add(services, "router:lists", streams: !pubSub);
        services.AddCaching(configuration, builder => builder.AddRedisConnection());
        services.AddCachingRouter(
            configuration,
            router =>
            {
                router
                    .Add(new RouterCacheRegistration<List<long>>
                    {
                        Name = "lists",
                        Group = "g",
                        Source = source,
                        RedisKey = source.RedisKey,
                        LocalCap = TimeSpan.FromMinutes(5),
                        DefaultExpiry = Lifetime,
                    })
                    .UseLibraryWhen(_ => true)
                    .UseEnvelope(new LegacyEnvelopeProxy());
                if (bypassFlag is not null)
                {
                    router.UseBypassWhen(bypassFlag);
                }
            });
        var provider = services.BuildServiceProvider();
        _nodes.Add(provider);
        _ = provider.GetRequiredService<IRouter>();
        return provider;
    }

    private async Task<List<string>> GroupsAsync()
    {
        var groups = new List<string>();
        foreach (var key in _redis!.GetServer(_redis.GetEndPoints()[0]).Keys(pattern: $"*{_app}*router:lists*"))
        {
            try
            {
                groups.AddRange((await Db.StreamGroupInfoAsync(key)).Select(group => group.Name!));
            }
            catch (RedisServerException)
            {
                // The stream has no groups left, or is gone.
            }
        }

        return groups;
    }

    /// <summary>Completes once the router logs that its library stack started, which it does after the library is published.</summary>
    private sealed class LibraryStartedSignal : ILoggerProvider, ILogger
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Task => _started.Task;

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (formatter(state, exception) == "The library stack started.")
            {
                _started.TrySetResult();
            }
        }

        public void Dispose()
        {
        }
    }
}
