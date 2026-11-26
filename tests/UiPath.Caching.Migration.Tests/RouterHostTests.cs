using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UiPath.Caching.Broadcast;
using UiPath.Caching.Config;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests;

public sealed class RouterHostTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_stack_that_cannot_be_built_does_not_fail_the_host_start_and_the_router_serves_from_the_source()
    {
        var logs = new CapturedLogs();
        await using var provider = Build(connector: false, identity: null, logs: logs);

        await new RouterHostedService(provider).StartAsync(Ct);

        var router = provider.GetRequiredService<IRouter>();
        router.Mode.Should().Be(RouterMode.BypassLibrary);
        router.ModeReason.Should().Be(RouterModeReason.LibraryUnavailable);
        var cache = router.Get<Item>("items");
        var value = await cache.GetOrAddAsync("k", _ => Task.FromResult<Item?>(new Item("made")), Ct);
        value.Should().Be(new Item("made"));
        logs.Entries.Should().Contain(entry => entry.Level == LogLevel.Error && entry.Message.Contains("library stack could not start", StringComparison.Ordinal));
        logs.Entries.Should().Contain(entry => entry.Level == LogLevel.Information && entry.Message.Contains("Caching router settings", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_real_host_starts_when_the_library_stack_cannot_be_built_and_the_router_is_in_bypass()
    {
        using var host = new HostBuilder().ConfigureServices(services => Copy(Register(connector: false, identity: null), services)).Build();

        await host.StartAsync(Ct);

        var router = host.Services.GetRequiredService<IRouter>();
        router.Mode.Should().Be(RouterMode.BypassLibrary);
        router.ModeReason.Should().Be(RouterModeReason.LibraryUnavailable);
        (await router.Get<Item>("items").GetOrAddAsync("k", _ => Task.FromResult<Item?>(new Item("made")), Ct)).Should().Be(new Item("made"));
        await host.StopAsync(Ct);
    }

    [Fact]
    public async Task A_started_service_that_ignores_its_stop_does_not_hold_up_the_rollback_of_a_failed_start()
    {
        var bound = TimeSpan.FromMilliseconds(300);
        var clock = new SignalingTimeProvider(DateTimeOffset.UnixEpoch);
        var logs = new CapturedLogs();
        var services = Register(connector: true, identity: null, logs: logs, cleanupBound: bound);
        services.AddSingleton<TimeProvider>(clock);
        services.AddKeyedSingleton(RouterServiceCollectionExtensions.StackName, new StackServices([new HangingHostedService(), new FailingHostedService()]));
        await using var provider = services.BuildServiceProvider();

        var creating = Task.Run(provider.GetRequiredService<IRouter>, Ct);
        await clock.WaitForTimerAsync(bound, Ct);
        creating.IsCompleted.Should().BeFalse("the rollback waits for the cleanup bound");
        clock.Advance(bound);
        var router = await creating.WaitAsync(Ct);

        logs.Entries.Should().Contain(entry => entry.Message.Contains("library stack could not start", StringComparison.Ordinal), "the first attempt ended inside the start bound");
        router.Mode.Should().Be(RouterMode.BypassLibrary);
        await provider.DisposeAsync().AsTask().WaitAsync(Ct);
    }

    [Fact]
    public async Task A_direct_library_cache_and_a_routed_cache_live_in_one_container_and_the_router_touches_only_its_own()
    {
        await using var provider = Build(connector: false, identity: null);
        await new RouterHostedService(provider).StartAsync(Ct);
        var router = provider.GetRequiredService<IRouter>();

        var routed = router.Get<Item>("items");
        var direct = provider.GetRequiredService<ICache<Item>>();

        routed.Should().BeOfType<RoutedCache<Item>>();
        direct.Should().NotBeOfType<RoutedCache<Item>>();
        direct.Name.Should().NotBe(routed.Name);
        (await routed.GetOrAddAsync("k", _ => Task.FromResult<Item?>(new Item("made")), Ct)).Should().Be(new Item("made"));
        var unregistered = () => router.Get<Item>("direct-only");
        unregistered.Should().Throw<KeyNotFoundException>("a cache the app uses directly is not the router's");
        provider.GetRequiredService<ICache<Item>>().Should().NotBeSameAs(routed);
    }

    [Fact]
    public async Task A_real_host_starts_when_the_library_stack_has_no_server_to_reach()
    {
        using var host = new HostBuilder().ConfigureServices(services => Copy(Register(connector: true, identity: null), services)).Build();

        await host.StartAsync(Ct);

        host.Services.GetRequiredService<IRouter>().Should().NotBeNull();
        await host.StopAsync(Ct);
    }

    [Fact]
    public async Task Every_router_gets_its_own_identity_on_one_host_and_the_configured_source_stays_the_base()
    {
        await using var first = Build(connector: true, identity: null);
        await using var second = Build(connector: true, identity: null);

        var one = SourceOf(first);
        var two = SourceOf(second);

        one.Should().NotBe(two);
        one.AbsoluteUri.Should().StartWith("urn:identity-test-").And.Contain($"-p{Environment.ProcessId}-").And.EndWith("?stack=UiPath.Caching.Migration");
        two.AbsoluteUri.Split('-')[..3].Should().Equal(one.AbsoluteUri.Split('-')[..3], "the host part is shared, only the process part differs");
    }

    [Fact]
    public async Task A_named_identity_is_used_as_given()
    {
        await using var provider = Build(connector: true, identity: "recycle-2");

        SourceOf(provider).AbsoluteUri.Should().EndWith("-precycle-2?stack=UiPath.Caching.Migration");
    }

    [Theory]
    [InlineData("urn:host", "a", "urn:host-pa")]
    [InlineData("urn:host?stack=s", "a", "urn:host-pa?stack=s")]
    public void The_process_part_goes_into_the_host_part_of_the_source(string source, string id, string expected)
    {
        RouterServiceCollectionExtensions.WithProcess(new Uri(source), id).AbsoluteUri.Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("slash/")]
    public void An_identity_with_unsafe_characters_is_refused(string identity)
    {
        var create = () => new RouterBuilder().UseNodeIdentity(identity);

        create.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Two_sources_that_differ_only_in_the_process_part_do_not_count_as_one_node()
    {
        var one = RouterServiceCollectionExtensions.WithProcess(new Uri("urn:host?stack=s"), "1");
        var two = RouterServiceCollectionExtensions.WithProcess(new Uri("urn:host?stack=s"), "2");
        var evicted = new List<string>();
        var observer = new PeerObserver(one, evicted.Add, () => evicted.Add("clear"));
        var events = new CacheEventFactory(Options.Create(new CacheOptions { SourceUri = two }));

        observer.OnNext(events.Create("c", KnownEventTypes.CacheRemoved, new CacheEventData(CacheKey.Verbatim("k"))));

        evicted.Should().Equal("k");
    }

    private static void Copy(IServiceCollection from, IServiceCollection to)
    {
        foreach (var descriptor in from)
        {
            to.Add(descriptor);
        }
    }

    private static Uri SourceOf(ServiceProvider provider) =>
        provider.GetRequiredKeyedService<IOptions<CacheOptions>>(RouterServiceCollectionExtensions.StackName).Value.SourceUri!;

    private static ServiceProvider Build(bool connector, string? identity, CapturedLogs? logs = null) => Register(connector, identity, logs).BuildServiceProvider();

    private static IServiceCollection Register(bool connector, string? identity, CapturedLogs? logs = null, TimeSpan? cleanupBound = null)
    {
        var app = $"id{Guid.NewGuid():N}"[..10];
        var settings = new Dictionary<string, string?>
        {
            ["Caching:AppShortName"] = app,
            ["Caching:SourceUri"] = "urn:identity-test-host",
            ["Caching:DefaultCache"] = KnownCacheProviderNames.Redis,
            ["Caching:Connections:Redis:ConnectionString"] = "127.0.0.1:1",
            ["Caching:Connections:Redis:WarmUpOnStart"] = "false",
            ["Caching:Connections:Redis:PlannedMaintenanceEnabled"] = "false",
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var source = new FakeBackend([], new ManualTimeProvider(DateTimeOffset.UnixEpoch));
        var services = new ServiceCollection().AddLogging(builder =>
        {
            if (logs is not null)
            {
                builder.AddProvider(logs);
            }
        });
        services.AddCaching(configuration, builder =>
        {
            if (connector)
            {
                builder.AddRedisConnection();
            }
        });
        services.AddCachingRouter(
            configuration,
            router =>
            {
                router
                    .Add(new RouterCacheRegistration<Item> { Name = "items", Group = "g", Source = source, RedisKey = key => key })
                    .UseLibraryWhen(_ => true)
                    .UseEnvelope(new LegacyEnvelopeProxy());
                if (identity is not null)
                {
                    router.UseNodeIdentity(identity);
                }

                if (cleanupBound is { } bound)
                {
                    router.CleanupBound = bound;
                }
            });
        return services;
    }

    private sealed class CapturedLogs : ILoggerProvider
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        // Logging continues on background threads while a test reads, so a read takes a copy under the same lock the writes use.
        public IReadOnlyList<(LogLevel Level, string Message)> Entries
        {
            get
            {
                lock (_entries)
                {
                    return [.. _entries];
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new Capture(this);

        public void Dispose()
        {
        }

        private void Add(LogLevel level, string message)
        {
            lock (_entries)
            {
                _entries.Add((level, message));
            }
        }

        private sealed class Capture(CapturedLogs owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                owner.Add(logLevel, formatter(state, exception));
            }
        }
    }
}
