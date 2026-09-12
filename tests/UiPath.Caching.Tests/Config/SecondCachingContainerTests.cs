using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UiPath.Caching.Config;
using UiPath.Caching.Locking;
using UiPath.Caching.Queue.Config;

namespace UiPath.Caching.Tests.Config;

/// <summary>
/// The shape docs/recipes/second-redis-connection.md relies on: a second <c>AddCaching</c> in a child container over
/// the same <c>Caching</c> section with only the connection replaced, exposed to the application container as keyed
/// services. Nothing connects: <see cref="RedisConnector"/> is lazy.
/// </summary>
public class SecondCachingContainerTests
{
    private const string Key = "secondary";

    [Fact]
    public void Nothing_that_touches_redis_is_shared_between_the_two_stacks()
    {
        using var root = Build();
        var child = root.GetRequiredKeyedService<IServiceProvider>(Key);

        root.GetRequiredKeyedService<IRedisConnector>(Key).Should().NotBeSameAs(root.GetRequiredService<IRedisConnector>());
        root.GetRequiredKeyedService<ICacheFactory>(Key).Should().NotBeSameAs(root.GetRequiredService<ICacheFactory>());
        root.GetRequiredKeyedService<IQueueCacheFactory>(Key).Should().NotBeSameAs(root.GetRequiredService<IQueueCacheFactory>());
        root.GetRequiredKeyedService<IDistributedLock>(Key).Should().NotBeSameAs(root.GetRequiredService<IDistributedLock>());
        root.GetRequiredKeyedService<ICache>(Key).Should().NotBeSameAs(root.GetRequiredService<ICacheFactory>().CreateCache());
        root.GetRequiredKeyedService<IHashCache>(Key).Should().NotBeSameAs(root.GetRequiredService<ICacheFactory>().CreateHashCache());
        root.GetRequiredKeyedService<ISetCache>(Key).Should().NotBeSameAs(root.GetRequiredService<ISetCache>());

        root.GetRequiredService<IOptions<RedisConnectionOptions>>().Value.ConnectionString.Should().Be("primary:6379");
        child.GetRequiredService<IOptions<RedisConnectionOptions>>().Value.ConnectionString.Should().Be("secondary:6380");
    }

    [Fact]
    public void The_keyed_caches_are_the_childs_own()
    {
        using var root = Build();
        var childFactory = root.GetRequiredKeyedService<ICacheFactory>(Key);

        root.GetRequiredKeyedService<ICache>(Key).Should().BeSameAs(childFactory.CreateCache());
        root.GetRequiredKeyedService<IHashCache>(Key).Should().BeSameAs(childFactory.CreateHashCache());
        root.GetRequiredKeyedService<ICache>(Key).Should().BeAssignableTo<RedisCacheBase>();
    }

    [Fact]
    public void Everything_but_the_connection_is_the_same_configuration()
    {
        using var root = Build(settings: [("Caching:Redis:KeyPrefix", "shared"), ("Caching:Connections:Redis:ConnectionStringExtraParams", "syncTimeout=1234")]);
        var child = root.GetRequiredKeyedService<IServiceProvider>(Key);

        child.GetRequiredService<IOptions<CacheOptions>>().Value.AppShortName.Should().Be("app");
        child.GetRequiredService<IOptions<RedisCacheOptions>>().Value.KeyPrefix.Should().Be("shared");
        // A connection key the secondary section does not override is inherited from the primary connection.
        child.GetRequiredService<IOptions<RedisConnectionOptions>>().Value.ConnectionStringExtraParams.Should().Be("syncTimeout=1234");
    }

    [Fact]
    public void A_secondary_connection_key_overrides_only_that_key()
    {
        using var root = Build(settings: [("Caching:Connections:SecondaryRedis:ConnectionStringExtraParams", "syncTimeout=9")]);
        var child = root.GetRequiredKeyedService<IServiceProvider>(Key);

        child.GetRequiredService<IOptions<RedisConnectionOptions>>().Value.ConnectionStringExtraParams.Should().Be("syncTimeout=9");
        root.GetRequiredService<IOptions<RedisConnectionOptions>>().Value.ConnectionStringExtraParams.Should().BeNull();
    }

    [Fact]
    public void An_empty_secondary_connection_section_is_refused_at_registration()
    {
        var act = () => Build(settings: [("Caching:Connections:SecondaryRedis:ConnectionString", null)]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Caching:Connections:SecondaryRedis*");
    }

    [Fact]
    public void Logging_and_the_clock_are_forwarded_not_duplicated()
    {
        using var root = Build();
        var child = root.GetRequiredKeyedService<IServiceProvider>(Key);

        child.GetRequiredService<TimeProvider>().Should().BeSameAs(root.GetRequiredService<TimeProvider>());
        child.GetRequiredService<ILoggerFactory>().Should().BeSameAs(root.GetRequiredService<ILoggerFactory>());
        child.GetRequiredService<ILogger<SecondCachingContainerTests>>().Should().NotBeNull();
    }

    [Fact]
    public async Task The_childs_hosted_services_are_started_and_stopped_by_the_root_host()
    {
        using var root = Build(child => child.AddSingleton<IHostedService, HostedProbe>());
        var probe = root.GetRequiredKeyedService<IServiceProvider>(Key).GetServices<IHostedService>().OfType<HostedProbe>().Single();
        var bridge = root.GetServices<IHostedService>().OfType<ChildHostedServices>().Single();

        await bridge.StartAsync(CancellationToken.None);
        probe.Started.Should().BeTrue();
        probe.Stopped.Should().BeFalse();

        await bridge.StopAsync(CancellationToken.None);
        probe.Stopped.Should().BeTrue();
    }

    [Fact]
    public void Disposing_the_root_disposes_the_child()
    {
        var root = Build(child => child.AddSingleton<DisposableProbe>());
        var probe = root.GetRequiredKeyedService<IServiceProvider>(Key).GetRequiredService<DisposableProbe>();

        root.Dispose();

        probe.Disposed.Should().BeTrue();
    }

    private static ServiceProvider Build(Action<IServiceCollection>? configureChild = null, params (string Path, string? Value)[] settings)
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
        services.AddCaching(configuration, Chain);

        var childConfiguration = WithSecondaryConnection(configuration);
        services.AddKeyedSingleton<IServiceProvider>(Key, (root, _) =>
        {
            var child = new ServiceCollection();
            child.AddSingleton(root.GetRequiredService<ILoggerFactory>());
            child.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
            child.AddSingleton(root.GetRequiredService<TimeProvider>());
            child.AddCaching(childConfiguration, Chain);
            configureChild?.Invoke(child);
            return child.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        });

        services.AddKeyedSingleton<ICacheFactory>(Key, (sp, key) => Child(sp, key).GetRequiredService<ICacheFactory>());
        services.AddKeyedSingleton<IQueueCacheFactory>(Key, (sp, key) => Child(sp, key).GetRequiredService<IQueueCacheFactory>());
        services.AddKeyedSingleton<IDistributedLock>(Key, (sp, key) => Child(sp, key).GetRequiredService<IDistributedLock>());
        services.AddKeyedSingleton<IRedisConnector>(Key, (sp, key) => Child(sp, key).GetRequiredService<IRedisConnector>());
        services.AddKeyedSingleton<ICache>(Key, (sp, key) => Child(sp, key).GetRequiredService<ICacheFactory>().CreateCache());
        services.AddKeyedSingleton<IHashCache>(Key, (sp, key) => Child(sp, key).GetRequiredService<ICacheFactory>().CreateHashCache());
        services.AddKeyedSingleton<ISetCache>(Key, (sp, key) => Child(sp, key).GetRequiredService<ISetCache>());
        services.AddSingleton<IHostedService>(sp => new ChildHostedServices(Child(sp, Key)));

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        static void Chain(ICachingBuilder b) =>
            b.AddRedisConnection().AddRedis().AddRedisDistributedLock().AddQueueRedis();

        static IServiceProvider Child(IServiceProvider root, object? key) =>
            root.GetRequiredKeyedService<IServiceProvider>(key);
    }

    /// <summary>Same as the recipe: the app configuration with <c>Connections:SecondaryRedis</c> laid over <c>Connections:Redis</c>.</summary>
    private static IConfiguration WithSecondaryConnection(IConfiguration configuration)
    {
        var overrides = configuration.GetSection("Caching:Connections:SecondaryRedis")
            .AsEnumerable(makePathsRelative: true)
            .Where(kv => kv.Value is not null)
            .ToDictionary(kv => $"Caching:Connections:Redis:{kv.Key}", kv => kv.Value);

        if (overrides.Count == 0)
        {
            throw new InvalidOperationException("'Caching:Connections:SecondaryRedis' is empty; the secondary caching stack needs at least a ConnectionString there.");
        }

        return new ConfigurationBuilder().AddConfiguration(configuration).AddInMemoryCollection(overrides).Build();
    }

    private sealed class ChildHostedServices(IServiceProvider child) : IHostedService
    {
        private readonly IHostedService[] _services = [.. child.GetServices<IHostedService>()];

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            foreach (var service in _services)
            {
                await service.StartAsync(cancellationToken);
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            foreach (var service in _services.Reverse())
            {
                await service.StopAsync(cancellationToken);
            }
        }
    }

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

    private sealed class DisposableProbe : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }
}
