using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UiPath.Caching.Config;
using UiPath.Caching.Locking;
using UiPath.Caching.Queue.Config;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Tests.Config;

/// <summary>Nothing connects: <see cref="RedisConnector"/> is lazy, warm-up and planned maintenance are off.</summary>
public class NamedCachingTests
{
    private const string Name = "SecondaryRedis";

    [Fact]
    public void Nothing_that_touches_redis_or_local_memory_is_shared_between_the_stacks()
    {
        using var root = Build();

        root.GetRequiredKeyedService<IRedisConnector>(Name).Should().NotBeSameAs(root.GetRequiredService<IRedisConnector>());
        root.GetRequiredKeyedService<ICacheFactory>(Name).Should().NotBeSameAs(root.GetRequiredService<ICacheFactory>());
        root.GetRequiredKeyedService<IQueueCacheFactory>(Name).Should().NotBeSameAs(root.GetRequiredService<IQueueCacheFactory>());
        root.GetRequiredKeyedService<IDistributedLock>(Name).Should().NotBeSameAs(root.GetRequiredService<IDistributedLock>());
        root.GetRequiredKeyedService<ILocalLock>(Name).Should().NotBeSameAs(root.GetRequiredService<ILocalLock>());
        root.GetRequiredKeyedService<ICache>(Name).Should().NotBeSameAs(root.GetRequiredService<ICacheFactory>().CreateCache());
        root.GetRequiredKeyedService<IHashCache>(Name).Should().NotBeSameAs(root.GetRequiredService<ICacheFactory>().CreateHashCache());
        root.GetRequiredKeyedService<ISetCache>(Name).Should().NotBeSameAs(root.GetRequiredService<ISetCache>());
        // The multilayer caches' L1 comes from the memory cache factory, so a separate factory means separate L1s.
        Child(root).GetRequiredService<IMemoryCacheFactory>().Should().NotBeSameAs(root.GetRequiredService<IMemoryCacheFactory>());

        root.GetRequiredService<IOptions<RedisConnectionOptions>>().Value.ConnectionString.Should().Be("primary:6379");
        Child(root).GetRequiredService<IOptions<RedisConnectionOptions>>().Value.ConnectionString.Should().Be("secondary:6380");
    }

    [Fact]
    public async Task Local_memory_is_per_stack()
    {
        using var root = Build();
        var primary = root.GetRequiredService<ICacheFactory>().CreateCache(KnownCacheProviderNames.InMemory);
        var secondary = root.GetRequiredKeyedService<ICacheFactory>(Name).CreateCache(KnownCacheProviderNames.InMemory);

        var token = TestContext.Current.CancellationToken;
        await primary.SetAsync("k", "v", policy: null, token: token);

        (await primary.GetAsync<string>("k", policy: null, token: token)).Should().Be("v");
        (await secondary.GetAsync<string>("k", policy: null, token: token)).Should().BeNull();
    }

    [Fact]
    public void A_stack_on_the_primarys_server_is_refused_without_naming_the_connection_string()
    {
        using var root = Build(("Caching:Connections:SecondaryRedis:ConnectionString", "PRIMARY:6379,password=hunter2"),
            ("Caching:Connections:Redis:ConnectionString", "primary:6379,password=hunter2"));

        var act = () => root.GetRequiredKeyedService<ICacheFactory>(Name);

        var message = act.Should().Throw<InvalidOperationException>()
            .WithMessage("*same ConnectionString as the application's primary*").Which.Message;
        // A connection string carries credentials and this lands in startup logs.
        message.Should().NotContain("hunter2").And.NotContain("6379");
    }

    [Fact]
    public void A_stack_matching_a_primary_connection_configured_in_code_is_refused()
    {
        using var root = Build(register: (services, configuration) =>
        {
            services.AddCaching(configuration, b => b.AddRedisConnection(o => o.ConnectionString = "code:1").AddRedis());
            services.AddNamedCaching(Name, configuration, NamedChain, configureConnection: o => o.ConnectionString = "code:1");
        });

        var act = () => root.GetRequiredKeyedService<ICacheFactory>(Name);

        act.Should().Throw<InvalidOperationException>().WithMessage("*same ConnectionString as the application's primary*");
    }

    [Fact]
    public void The_keyed_caches_survive_a_disabled_stack()
    {
        using var root = Build(("Caching:Enabled", "false"));

        root.GetRequiredKeyedService<ICache<string>>(Name).Should().NotBeNull();
        root.GetRequiredKeyedService<IHashCache<string>>(Name).Should().NotBeNull();
        root.GetRequiredKeyedService<ISetCache<string>>(Name).Should().NotBeNull();
        // Nothing registers one when caching is off; the typed caches fall back to the factory's policy.
        root.GetKeyedService<ICachePolicyFactory>(Name).Should().BeNull();
    }

    /// <summary>A strategy that throws proves it was used: dropped from a constructor, the default silently takes over.</summary>
    [Fact]
    public async Task A_key_strategy_registered_in_the_stack_reaches_each_of_its_typed_caches()
    {
        using var root = Build(register: (services, configuration) =>
        {
            services.AddCaching(configuration, RootChain);
            services
                .AddNamedCaching(
                    Name,
                    configuration,
                    NamedChain,
                    configureServices: (child, _) => child.AddSingleton<ICacheKeyStrategy>(new ThrowingCacheKeyStrategy()))
                .ExposeQueueCaches();
        });
        var token = TestContext.Current.CancellationToken;

        root.GetRequiredKeyedService<ICacheKeyStrategy>(Name).Should().BeOfType<ThrowingCacheKeyStrategy>();

        var cache = async () => await root.GetRequiredKeyedService<ICache<string>>(Name).GetAsync("k", token);
        var hashCache = async () => await root.GetRequiredKeyedService<IHashCache<string>>(Name).GetAsync("h", token);
        var setCache = async () => await root.GetRequiredKeyedService<ISetCache<string>>(Name).MembersAsync("s", token);

        (await cache.Should().ThrowAsync<NotSupportedException>()).WithMessage("*k*");
        (await hashCache.Should().ThrowAsync<NotSupportedException>()).WithMessage("*h*");
        (await setCache.Should().ThrowAsync<NotSupportedException>()).WithMessage("*s*");
    }

    [Fact]
    public void A_stack_without_caching_on_the_application_container_is_refused()
    {
        using var root = Build(register: (services, configuration) => services.AddNamedCaching(Name, configuration, NamedChain));

        var act = () => root.GetRequiredKeyedService<ICacheFactory>(Name);

        act.Should().Throw<InvalidOperationException>().WithMessage("*no caching on the application's container*");
    }

    [Fact]
    public void A_service_the_stack_does_not_register_reads_as_absent()
    {
        // Planned maintenance is off in the base settings, so neither container registers one.
        using var root = Build();

        root.GetKeyedService<IRedisPlannedMaintenance>(Name).Should().BeNull();
        root.GetService<IRedisPlannedMaintenance>().Should().BeNull();
    }

    [Fact]
    public void The_keyed_caches_are_the_stacks_own_and_typed_ones_resolve()
    {
        using var root = Build();
        var factory = root.GetRequiredKeyedService<ICacheFactory>(Name);

        root.GetRequiredKeyedService<ICache>(Name).Should().BeSameAs(factory.CreateCache());
        root.GetRequiredKeyedService<IHashCache>(Name).Should().BeSameAs(factory.CreateHashCache());
        root.GetRequiredKeyedService<ICache<string>>(Name).Name.Should().Be(root.GetRequiredKeyedService<ICache>(Name).Name);
        root.GetRequiredKeyedService<IHashCache<string>>(Name).Should().NotBeNull();
        root.GetRequiredKeyedService<ISetCache<string>>(Name).Should().NotBeNull();
    }

    [Fact]
    public void Everything_but_the_connection_is_the_same_configuration()
    {
        using var root = Build(("Caching:Redis:KeyPrefix", "shared"), ("Caching:Connections:Redis:ConnectionStringExtraParams", "syncTimeout=1234"));
        var child = Child(root);

        child.GetRequiredService<IOptions<CacheOptions>>().Value.AppShortName.Should().Be("app");
        child.GetRequiredService<IOptions<RedisCacheOptions>>().Value.KeyPrefix.Should().Be("shared");
        // A connection key the stack's section leaves unset keeps the primary connection's value.
        child.GetRequiredService<IOptions<RedisConnectionOptions>>().Value.ConnectionStringExtraParams.Should().Be("syncTimeout=1234");
    }

    [Fact]
    public void A_connection_key_of_the_stack_overrides_only_that_key()
    {
        using var root = Build(("Caching:Connections:SecondaryRedis:ConnectionStringExtraParams", "syncTimeout=9"));

        Child(root).GetRequiredService<IOptions<RedisConnectionOptions>>().Value.ConnectionStringExtraParams.Should().Be("syncTimeout=9");
        root.GetRequiredService<IOptions<RedisConnectionOptions>>().Value.ConnectionStringExtraParams.Should().BeNull();
    }

    [Fact]
    public void ConfigureConnection_runs_after_both_sections()
    {
        using var root = Build(register: (services, configuration) =>
        {
            services.AddCaching(configuration, RootChain);
            services.AddNamedCaching(Name, configuration, NamedChain, configureConnection: o => o.ConnectionString = "code:1");
        });

        Child(root).GetRequiredService<IOptions<RedisConnectionOptions>>().Value.ConnectionString.Should().Be("code:1");
    }

    [Fact]
    public void A_separate_section_is_bound_when_asked_for()
    {
        using var root = Build(
            register: (services, configuration) =>
            {
                services.AddCaching(configuration, RootChain);
                services.AddNamedCaching("cold", configuration, NamedChain, sectionName: "CachingCold");
            },
            ("CachingCold:AppShortName", "cold"),
            ("CachingCold:DefaultCache", KnownCacheProviderNames.Redis),
            ("CachingCold:Connections:cold:ConnectionString", "cold:6381"));
        var child = Child(root, "cold");

        child.GetRequiredService<IOptions<CacheOptions>>().Value.AppShortName.Should().Be("cold");
        child.GetRequiredService<IOptions<RedisConnectionOptions>>().Value.ConnectionString.Should().Be("cold:6381");
    }

    [Fact]
    public void A_missing_connection_section_is_refused_at_registration()
    {
        var act = () => Build(("Caching:Connections:SecondaryRedis:ConnectionString", null));

        act.Should().Throw<InvalidOperationException>().WithMessage("*no ConnectionString*Caching:Connections:SecondaryRedis*");
    }

    [Fact]
    public void The_primary_connections_name_is_refused()
    {
        var act = () => Build(register: (services, configuration) => services.AddNamedCaching("redis", configuration, NamedChain));

        act.Should().Throw<ArgumentException>().WithMessage("*primary connection*");
    }

    [Fact]
    public void The_same_name_twice_is_refused()
    {
        var act = () => Build(register: (services, configuration) =>
        {
            services.AddNamedCaching(Name, configuration, NamedChain);
            services.AddNamedCaching(Name, configuration, NamedChain);
        });

        act.Should().Throw<InvalidOperationException>().WithMessage("*already*");
    }

    [Fact]
    public void A_chain_that_adds_its_own_connection_is_refused()
    {
        using var root = Build(register: (services, configuration) =>
        {
            services.AddCaching(configuration, RootChain);
            services.AddNamedCaching(Name, configuration, b => b.AddRedisConnection().AddRedis());
        });

        var act = () => root.GetRequiredKeyedService<ICacheFactory>(Name);

        act.Should().Throw<InvalidOperationException>().WithMessage("*remove AddRedisConnection*");
    }

    [Fact]
    public void An_empty_connection_string_is_refused_at_registration()
    {
        var act = () => Build(("Caching:Connections:SecondaryRedis:ConnectionString", "   "));

        act.Should().Throw<InvalidOperationException>().WithMessage("*no ConnectionString*Caching:Connections:SecondaryRedis*");
    }

    [Fact]
    public void A_connection_section_carrying_no_connection_string_is_refused_at_registration()
    {
        var act = () => Build(
            ("Caching:Connections:SecondaryRedis:ConnectionString", null),
            ("Caching:Connections:SecondaryRedis:WarmUpOnStart", "true"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*no ConnectionString*");
    }

    [Fact]
    public void A_connection_supplied_only_from_code_is_accepted()
    {
        using var root = Build(
            register: (services, configuration) =>
            {
                services.AddCaching(configuration, RootChain);
                services.AddNamedCaching(Name, configuration, NamedChain, configureConnection: o => o.ConnectionString = "code:1");
            },
            ("Caching:Connections:SecondaryRedis:ConnectionString", null));

        Child(root).GetRequiredService<IOptions<RedisConnectionOptions>>().Value.ConnectionString.Should().Be("code:1");
    }

    [Fact]
    public void A_connection_that_code_leaves_empty_is_refused()
    {
        using var root = Build(register: (services, configuration) =>
        {
            services.AddCaching(configuration, RootChain);
            services.AddNamedCaching(Name, configuration, NamedChain, configureConnection: o => o.ConnectionString = " ");
        });

        var act = () => root.GetRequiredKeyedService<ICacheFactory>(Name);

        act.Should().Throw<InvalidOperationException>().WithMessage("*empty ConnectionString*");
    }

    [Fact]
    public void Logging_clock_and_telemetry_are_forwarded_not_duplicated()
    {
        var telemetry = Substitute.For<ICachingTelemetryProvider>();
        using var root = Build(register: (services, configuration) =>
        {
            services.AddSingleton(telemetry);
            services.AddCaching(configuration, RootChain);
            services.AddNamedCaching(Name, configuration, NamedChain);
        });
        var child = Child(root);

        child.GetRequiredService<TimeProvider>().Should().BeSameAs(root.GetRequiredService<TimeProvider>());
        child.GetRequiredService<ILoggerFactory>().Should().BeSameAs(root.GetRequiredService<ILoggerFactory>());
        child.GetRequiredService<ICachingTelemetryProvider>().Should().BeSameAs(telemetry);
        child.GetRequiredService<ILogger<NamedCachingTests>>().Should().NotBeNull();
    }

    [Fact]
    public void Expose_reaches_anything_the_stack_registers_and_reports_what_it_does_not()
    {
        using var root = Build(register: (services, configuration) =>
        {
            services.AddCaching(configuration, RootChain);
            services.AddNamedCaching(Name, configuration, NamedChain, configureServices: (child, _) => child.AddSingleton<Marker>())
                .Expose<Marker>()
                .Expose<Missing>()
                .Expose(stack => new Exposed(stack.GetRequiredService<IOptions<CacheOptions>>().Value.AppShortName!));
        });

        root.GetRequiredKeyedService<Marker>(Name).Should().NotBeNull();
        root.GetRequiredKeyedService<Exposed>(Name).AppShortName.Should().Be("app");
        root.GetKeyedService<Missing>(Name).Should().BeNull();
        var act = () => root.GetRequiredKeyedService<Missing>(Name);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task The_stacks_hosted_services_are_started_and_stopped_with_the_host()
    {
        using var root = Build(register: (services, configuration) =>
        {
            services.AddCaching(configuration, RootChain);
            services.AddNamedCaching(Name, configuration, NamedChain, configureServices: (child, _) => child.AddSingleton<IHostedService, HostedProbe>());
        });
        var bridge = root.GetServices<IHostedService>().OfType<NamedCachingHostedService>().Single();

        await bridge.StartAsync(CancellationToken.None);
        var probe = Child(root).GetServices<IHostedService>().OfType<HostedProbe>().Single();
        probe.Started.Should().BeTrue();
        probe.Stopped.Should().BeFalse();

        await bridge.StopAsync(CancellationToken.None);
        probe.Stopped.Should().BeTrue();
    }

    [Fact]
    public async Task Every_hosted_service_is_stopped_even_when_one_throws()
    {
        using var root = Build(register: (services, configuration) =>
        {
            services.AddCaching(configuration, RootChain);
            services.AddNamedCaching(Name, configuration, NamedChain, configureServices: (child, _) =>
            {
                child.AddSingleton<IHostedService, HostedProbe>();
                child.AddSingleton<IHostedService, ThrowingHostedProbe>();
            });
        });
        var bridge = root.GetServices<IHostedService>().OfType<NamedCachingHostedService>().Single();
        var probe = Child(root).GetServices<IHostedService>().OfType<HostedProbe>().Single();
        await bridge.StartAsync(CancellationToken.None);

        var act = async () => await bridge.StopAsync(CancellationToken.None);

        // The thrower is stopped first, in reverse registration order, so the probe behind it proves the loop continued.
        (await act.Should().ThrowAsync<AggregateException>()).Which.InnerExceptions.Should().ContainSingle();
        probe.Stopped.Should().BeTrue();
    }

    [Fact]
    public void Disposing_the_application_container_disposes_the_stack()
    {
        var root = Build(register: (services, configuration) =>
        {
            services.AddCaching(configuration, RootChain);
            services.AddNamedCaching(Name, configuration, NamedChain, configureServices: (child, _) => child.AddSingleton<DisposableProbe>());
        });
        var probe = Child(root).GetRequiredService<DisposableProbe>();

        root.Dispose();

        probe.Disposed.Should().BeTrue();
    }

    /// <summary>The stack and the application's container both dispose an exposed service, which <see cref="IDisposable"/> allows.</summary>
    [Fact]
    public void Disposal_reaching_every_exposed_service_twice_is_safe()
    {
        var root = Build();
        object[] exposed =
        [
            root.GetRequiredKeyedService<ICacheFactory>(Name),
            root.GetRequiredKeyedService<IQueueCacheFactory>(Name),
            root.GetRequiredKeyedService<IRedisConnector>(Name),
            root.GetRequiredKeyedService<IDistributedLock>(Name),
            root.GetRequiredKeyedService<ILocalLock>(Name),
            root.GetRequiredKeyedService<ITopicFactory>(Name),
            root.GetRequiredKeyedService<ICache>(Name),
            root.GetRequiredKeyedService<IHashCache>(Name),
            root.GetRequiredKeyedService<ISetCache>(Name),
        ];
        exposed.Should().AllSatisfy(service => service.Should().NotBeNull());

        var act = root.Dispose;

        act.Should().NotThrow();
    }

    private static IServiceProvider Child(IServiceProvider root, string name = Name) =>
        NamedCachingContainer.Of(root, name).Provider;

    private static void RootChain(ICachingBuilder b) =>
        b.AddRedisConnection().AddRedis().AddMemory().AddRedisDistributedLock().AddLocalLock().AddQueueRedis();

    private static void NamedChain(ICachingBuilder b) =>
        b.AddRedis().AddMemory().AddRedisDistributedLock().AddLocalLock().AddQueueRedis();

    private static ServiceProvider Build(params (string Path, string? Value)[] settings) =>
        Build(register: null, settings);

    private static ServiceProvider Build(Action<IServiceCollection, IConfiguration>? register, params (string Path, string? Value)[] settings)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Caching:AppShortName"] = "app",
            ["Caching:DefaultCache"] = KnownCacheProviderNames.Redis,
            ["Caching:Connections:Redis:ConnectionString"] = "primary:6379",
            ["Caching:Connections:Redis:WarmUpOnStart"] = "false",
            ["Caching:Connections:Redis:PlannedMaintenanceEnabled"] = "false",
            ["Caching:Connections:SecondaryRedis:ConnectionString"] = "secondary:6380",
        };
        foreach (var (path, value) in settings)
        {
            values[path] = value;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        if (register is null)
        {
            services.AddCaching(configuration, RootChain);
            services.AddNamedCaching(Name, configuration, NamedChain).ExposeQueueCaches();
        }
        else
        {
            register(services, configuration);
        }

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private sealed class Marker;

    private sealed class Missing;

    private sealed record Exposed(string AppShortName);

    private sealed class HostedProbe : IHostedService
    {
        public bool Started { get; private set; }

        public bool Stopped { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            Started = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            Stopped = true;
            return Task.CompletedTask;
        }
    }

    /// <summary>Throws where a real strategy would compose the key, before the cache reaches Redis.</summary>
    private sealed class ThrowingCacheKeyStrategy : ICacheKeyStrategy
    {
        public CacheKey GetCacheKey<T>(CacheKey key) => throw new NotSupportedException($"strategy reached for '{key}'");
    }

    private sealed class ThrowingHostedProbe : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("stop failed");
    }

    private sealed class DisposableProbe : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }
}
