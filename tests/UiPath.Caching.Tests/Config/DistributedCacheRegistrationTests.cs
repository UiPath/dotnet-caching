using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using UiPath.Caching.Config;
using UiPath.Caching.Distributed;
using UiPath.Caching.Redis;

namespace UiPath.Caching.Tests.Config;

public class DistributedCacheRegistrationTests
{
    private static ServiceProvider Build(string providerName, Action<UiPathDistributedCacheOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddCaching(b =>
        {
            b.AddMemory(_ => { });
            b.AddDistributedCache(providerName, configure);
        });
        return services.BuildServiceProvider();
    }

    [Fact]
    public void InMemory_tier_works_without_AddMemory()
    {
        var services = new ServiceCollection();
        services.AddCaching(b => b.AddDistributedCache(KnownCacheProviderNames.InMemory));
        using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<IDistributedCache>();

        cache.Set("k", [1], new DistributedCacheEntryOptions());

        cache.Get("k").Should().Equal(1);
    }

    [Fact]
    public void Null_default_expiration_without_policy_fails_fast()
    {
        var services = new ServiceCollection();
        services.AddCaching(b =>
        {
            b.Services.Configure<InMemoryCacheOptions>(o => o.DefaultExpiration = null);
            b.AddDistributedCache(KnownCacheProviderNames.InMemory);
        });
        using var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<IDistributedCache>();

        act.Should().Throw<InvalidOperationException>().WithMessage("*DefaultExpiration*DistributedExpiration*");
    }

    [Fact]
    public void Null_default_expiration_is_accepted_when_a_policy_bounds_writes()
    {
        var services = new ServiceCollection();
        services.AddCaching(
            b =>
            {
                b.Services.Configure<InMemoryCacheOptions>(o => o.DefaultExpiration = null);
                b.AddDistributedCache(KnownCacheProviderNames.InMemory);
            },
            o => o.DefaultCachePolicy = new CachePolicy { DistributedExpiration = TimeSpan.FromMinutes(5) });
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IDistributedCache>().Should().NotBeNull();
    }

    [Fact]
    public void Redis_tier_without_a_connection_fails_with_guidance()
    {
        var services = new ServiceCollection();
        services.AddCaching(b => b.AddDistributedCache(KnownCacheProviderNames.Redis));
        using var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<IDistributedCache>();

        act.Should().Throw<InvalidOperationException>().WithMessage("*AddRedisConnection*");
    }

    [Fact]
    public void Resolves_IDistributedCache_and_keyed_cache()
    {
        using var provider = Build(KnownCacheProviderNames.InMemory);
        provider.GetRequiredService<IDistributedCache>().Should().BeOfType<UiPathDistributedCache>();
        provider.GetRequiredKeyedService<IHashCache>(DistributedCacheCollectionExtensions.DistributedCacheServiceKey)
            .Should().NotBeNull();
    }

    [Fact]
    public void Keyed_cache_is_a_separate_instance_from_the_apps_cache()
    {
        using var provider = Build(KnownCacheProviderNames.InMemory);
        var appCache = provider.GetRequiredService<ICacheFactory>().CreateHashCache(KnownCacheProviderNames.InMemory);
        var distributedCache = provider.GetRequiredKeyedService<IHashCache>(DistributedCacheCollectionExtensions.DistributedCacheServiceKey);
        distributedCache.Should().NotBeSameAs(appCache);
    }

    [Fact]
    public void Unknown_provider_name_fails_fast_with_supported_names()
    {
        using var provider = Build("NoSuchProvider");
        var act = () => provider.GetRequiredService<IDistributedCache>();
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*NoSuchProvider*").WithMessage("*Redis*InMemoryRedis*InMemory*");
    }

    [Fact]
    public void Round_trips_through_the_real_pipeline()
    {
        using var provider = Build(KnownCacheProviderNames.InMemory);
        var cache = provider.GetRequiredService<IDistributedCache>();

        cache.Set("AbC", [1, 2, 3], new DistributedCacheEntryOptions());

        cache.Get("AbC").Should().Equal(1, 2, 3);
        cache.Get("abc").Should().BeNull();
    }

    [Fact]
    public void Non_positive_default_entry_expiration_fails_fast()
    {
        using var provider = Build(KnownCacheProviderNames.InMemory, o => o.DefaultEntryExpiration = TimeSpan.Zero);

        var act = () => provider.GetRequiredService<IDistributedCache>();

        act.Should().Throw<InvalidOperationException>().WithMessage("*DefaultEntryExpiration must be positive*");
    }

    [Fact]
    public void Unbounded_entries_and_a_default_expiration_together_fail_fast()
    {
        using var provider = Build(KnownCacheProviderNames.InMemory, o =>
        {
            o.AllowUnboundedEntries = true;
            o.DefaultEntryExpiration = TimeSpan.FromHours(1);
        });

        var act = () => provider.GetRequiredService<IDistributedCache>();

        act.Should().Throw<InvalidOperationException>().WithMessage("*set one or the other*");
    }


    [Fact]
    public void Custom_cache_key_strategy_is_honored_end_to_end()
    {
        using var provider = Build(KnownCacheProviderNames.InMemory,
            o => o.CacheKeyStrategy = new PrefixCacheKeyStrategy("mine"));
        var cache = provider.GetRequiredService<IDistributedCache>();

        cache.Set("k", [1], new DistributedCacheEntryOptions());

        cache.Get("k").Should().Equal(1);
    }

    [Fact]
    public void Blank_redis_key_differentiator_fails_fast()
    {
        var act = () => Build(KnownCacheProviderNames.InMemory, o => o.RedisKeyDifferentiator = "  ");

        act.Should().Throw<InvalidOperationException>().WithMessage("*RedisKeyDifferentiator must be a non-empty value*");
    }

    [Theory]
    [InlineData(RedisTypePrefixes.String)]
    [InlineData(RedisTypePrefixes.Hash)]
    [InlineData(RedisTypePrefixes.PubSub)]
    [InlineData(RedisTypePrefixes.Streams)]
    [InlineData("H")]
    [InlineData("ST")]
    public void Redis_key_differentiator_colliding_with_an_application_prefix_fails_fast(string differentiator)
    {
        var act = () => Build(KnownCacheProviderNames.InMemory, o => o.RedisKeyDifferentiator = differentiator);

        act.Should().Throw<InvalidOperationException>().WithMessage("*same Redis keyspace*");
    }

    [Fact]
    public void Custom_redis_key_strategy_factory_is_used_over_the_applications()
    {
        var services = new ServiceCollection();
        var factory = new RecordingRedisKeyStrategyFactory();
        services.AddCaching(
            b =>
            {
                b.AddRedisConnection(o => o.ConnectionString = "localhost:6379,abortConnect=false");
                b.AddRedis(_ => { });
                b.AddDistributedCache(KnownCacheProviderNames.Redis, o => o.RedisKeyStrategyFactory = factory);
            },
            o => o.AppShortName = "app");
        using var provider = services.BuildServiceProvider();

        _ = provider.GetRequiredService<IDistributedCache>();

        factory.Differentiators.Should().Contain(UiPathDistributedCacheOptions.DefaultRedisKeyDifferentiator);
    }

    /// <summary>
    /// A distributed-only override is probed against the application's own factory, not against itself:
    /// otherwise an override landing on a real application key passes by returning something else from its own
    /// type overloads.
    /// </summary>
    [Fact]
    public void Distributed_only_factory_landing_on_an_application_key_fails_fast()
    {
        var services = new ServiceCollection();
        services.AddCaching(
            b =>
            {
                b.AddRedisConnection(o => o.ConnectionString = "localhost:6379,abortConnect=false");
                b.AddRedis(_ => { });
                b.AddDistributedCache(KnownCacheProviderNames.Redis,
                    o => o.RedisKeyStrategyFactory = new ApplicationHashImpersonatingFactory());
            },
            o => o.AppShortName = "app");
        using var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<IDistributedCache>();

        act.Should().Throw<InvalidOperationException>().WithMessage("*share one keyspace*");
    }

    /// <summary>
    /// Composes the application's hash keyspace whatever differentiator it is handed, while its own type
    /// overloads answer differently — so only comparing against the application's factory catches it.
    /// </summary>
    private sealed class ApplicationHashImpersonatingFactory : IRedisKeyStrategyFactory
    {
        private readonly DefaultRedisKeyStrategyFactory _inner = new();

        public IRedisKeyStrategy Create(CacheOptions options, Type cacheType) =>
            _inner.Create(options, "elsewhere");

        public IRedisKeyStrategy Create(CacheOptions options, string differentiator) =>
            _inner.Create(options, RedisTypePrefixes.Hash);
    }

    /// <summary>
    /// The application's own factory is inherited when the distributed cache does not override it, so one that
    /// composes the same key whatever differentiator it is handed puts both caches on one keyspace.
    /// </summary>
    [Fact]
    public void Inherited_factory_that_ignores_the_differentiator_fails_fast()
    {
        var services = new ServiceCollection();
        services.AddCaching(
            b =>
            {
                b.AddRedisConnection(o => o.ConnectionString = "localhost:6379,abortConnect=false");
                b.AddRedis(o => o.RedisKeyStrategyFactory = new FixedRedisKeyStrategyFactory());
                b.AddDistributedCache(KnownCacheProviderNames.Redis);
            },
            o => o.AppShortName = "app");
        using var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<IDistributedCache>();

        act.Should().Throw<InvalidOperationException>().WithMessage("*share one keyspace*");
    }

    /// <summary>
    /// The guard is about collision, not about honoring the argument: a distributed-only factory may ignore the
    /// differentiator as long as the keyspace it produces is disjoint from the application's.
    /// </summary>
    [Fact]
    public void Distributed_only_factory_on_a_disjoint_keyspace_is_accepted()
    {
        var services = new ServiceCollection();
        services.AddCaching(
            b =>
            {
                b.AddRedisConnection(o => o.ConnectionString = "localhost:6379,abortConnect=false");
                b.AddRedis(_ => { });
                b.AddDistributedCache(KnownCacheProviderNames.Redis,
                    o => o.RedisKeyStrategyFactory = new FixedRedisKeyStrategyFactory());
            },
            o => o.AppShortName = "app");
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IDistributedCache>().Should().NotBeNull();
    }

    private sealed class RecordingRedisKeyStrategyFactory : IRedisKeyStrategyFactory
    {
        private readonly DefaultRedisKeyStrategyFactory _inner = new();

        public List<string> Differentiators { get; } = [];

        public IRedisKeyStrategy Create(CacheOptions options, Type cacheType) => _inner.Create(options, cacheType);

        public IRedisKeyStrategy Create(CacheOptions options, string differentiator)
        {
            Differentiators.Add(differentiator);
            return _inner.Create(options, differentiator);
        }
    }

    private sealed class FixedRedisKeyStrategyFactory : IRedisKeyStrategyFactory
    {
        public IRedisKeyStrategy Create(CacheOptions options, Type cacheType) => Create(options, "ignored");

        public IRedisKeyStrategy Create(CacheOptions options, string differentiator) =>
            new PrefixRedisKeyStrategy("fixed", options.Separator);
    }

    /// <summary>
    /// The prerequisite check must not depend on call order: AddInMemoryRedis installs the broadcast wiring
    /// from its own completion callback, and callbacks run in registration order.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InMemoryRedis_tier_accepts_AddInMemoryRedis_in_either_order(bool distributedFirst)
    {
        var services = new ServiceCollection();
        services.AddCaching(
            b =>
            {
                b.AddRedisConnection(o => o.ConnectionString = "localhost:6379,abortConnect=false");
                if (distributedFirst)
                {
                    b.AddDistributedCache(KnownCacheProviderNames.InMemoryRedis);
                    b.AddInMemoryRedis(_ => { });
                }
                else
                {
                    b.AddInMemoryRedis(_ => { });
                    b.AddDistributedCache(KnownCacheProviderNames.InMemoryRedis);
                }
            },
            o => o.AppShortName = "app");
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IDistributedCache>().Should().NotBeNull();
    }

    /// <summary>
    /// Broadcast can be present but inert: the provider swaps in null factories when its own BroadcastEnable is
    /// false, and TopicFactory hands out a null provider when the app-wide switch is off. Either way the local
    /// layer is never invalidated across nodes, so the tier is refused rather than serving stale entries.
    /// </summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void InMemoryRedis_tier_with_inert_broadcast_fails_fast(bool providerBroadcast, bool appBroadcast)
    {
        var services = new ServiceCollection();
        services.AddCaching(
            b =>
            {
                b.AddRedisConnection(o => o.ConnectionString = "localhost:6379,abortConnect=false");
                b.AddInMemoryRedis(o => o.BroadcastEnable = providerBroadcast);
                b.AddDistributedCache(KnownCacheProviderNames.InMemoryRedis);
            },
            o =>
            {
                o.AppShortName = "app";
                o.BroadcastEnabled = appBroadcast;
            });
        using var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<IDistributedCache>();

        act.Should().Throw<InvalidOperationException>().WithMessage("*requires broadcast*");
    }

    /// <summary>Without AddInMemoryRedis the local layer is never invalidated across nodes, so the tier is refused.</summary>
    [Fact]
    public void InMemoryRedis_tier_without_broadcast_wiring_fails_fast()
    {
        var services = new ServiceCollection();
        services.AddCaching(
            b =>
            {
                b.AddRedisConnection(o => o.ConnectionString = "localhost:6379,abortConnect=false");
                b.AddDistributedCache(KnownCacheProviderNames.InMemoryRedis);
            },
            o => o.AppShortName = "app");
        using var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<IDistributedCache>();

        act.Should().Throw<InvalidOperationException>().WithMessage("*AddInMemoryRedis*");
    }

    /// <summary>
    /// A dropped sliding refresh silently shortens a session and fire-and-forget cannot report one, so this
    /// provider awaits — without changing the application's own caches.
    /// </summary>
    [Fact]
    public void Distributed_provider_awaits_refresh_while_the_application_does_not()
    {
        var services = new ServiceCollection();
        services.AddCaching(
            b =>
            {
                b.AddRedisConnection(o => o.ConnectionString = "localhost:6379,abortConnect=false");
                b.AddRedis(_ => { });
                b.AddDistributedCache(KnownCacheProviderNames.Redis);
            },
            o => o.AppShortName = "app");
        using var provider = services.BuildServiceProvider();

        var distributed = (RedisCacheBase)provider.GetRequiredKeyedService<IHashCache>(
            DistributedCacheCollectionExtensions.DistributedCacheServiceKey);
        var application = (RedisCacheBase)provider.GetRequiredService<ICacheFactory>()
            .CreateHashCache(KnownCacheProviderNames.Redis);

        distributed.RefreshFlags.Should().Be(CommandFlags.DemandMaster);
        application.RefreshFlags.Should().Be(CommandFlags.DemandMaster | CommandFlags.FireAndForget);
        provider.GetRequiredService<IOptions<RedisCacheOptions>>().Value.AwaitRefresh
            .Should().BeFalse("the DI singleton is left untouched");
    }

    /// <summary>Both Redis caches must honor the option; one of them ignoring it is the defect this guards.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Await_refresh_option_reaches_both_redis_caches(bool awaitRefresh)
    {
        var expected = awaitRefresh
            ? CommandFlags.DemandMaster
            : CommandFlags.DemandMaster | CommandFlags.FireAndForget;
        var services = new ServiceCollection();
        services.AddCaching(
            b =>
            {
                b.AddRedisConnection(o => o.ConnectionString = "localhost:6379,abortConnect=false");
                b.AddRedis(o => o.AwaitRefresh = awaitRefresh);
            },
            o => o.AppShortName = "app");
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<ICacheFactory>();

        ((RedisCacheBase)factory.CreateCache(KnownCacheProviderNames.Redis)).RefreshFlags.Should().Be(expected);
        ((RedisCacheBase)factory.CreateHashCache(KnownCacheProviderNames.Redis)).RefreshFlags.Should().Be(expected);
    }

    /// <summary>A disabled tier yields a null backing cache rather than first demanding that tier's infrastructure.</summary>
    [Fact]
    public void Disabled_redis_tier_does_not_require_a_connection()
    {
        var services = new ServiceCollection();
        services.AddCaching(b =>
        {
            b.Services.Configure<RedisCacheOptions>(o => o.Enabled = false);
            b.AddDistributedCache(KnownCacheProviderNames.Redis);
        });
        using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<IDistributedCache>();

        cache.Set("k", [1], new DistributedCacheEntryOptions());

        cache.Get("k").Should().BeNull();
    }

    /// <summary>
    /// The backing cache lets a policy expiration override the tier default, so a healthy tier default must not
    /// mask a non-positive policy value.
    /// </summary>
    [Fact]
    public void Non_positive_policy_expiration_is_rejected_despite_a_healthy_tier_default()
    {
        var services = new ServiceCollection();
        services.AddCaching(
            b =>
            {
                b.Services.Configure<InMemoryCacheOptions>(o => o.DefaultExpiration = TimeSpan.FromHours(1));
                b.AddDistributedCache(KnownCacheProviderNames.InMemory, o => o.PolicyName = "vanishing");
            },
            o => o.Policies = new Dictionary<string, CachePolicy>
            {
                ["vanishing"] = new() { DistributedExpiration = TimeSpan.Zero },
            });
        using var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<IDistributedCache>();

        act.Should().Throw<InvalidOperationException>().WithMessage("*would expire immediately*");
    }

    /// <summary>
    /// With no named policy the cache builds its default from the tier default as the primary, so a positive
    /// application default policy must not mask a zero tier default — the real write still uses zero.
    /// </summary>
    [Fact]
    public void Zero_tier_default_is_rejected_even_behind_a_positive_application_default_policy()
    {
        var services = new ServiceCollection();
        services.AddCaching(
            b =>
            {
                b.Services.Configure<InMemoryCacheOptions>(o => o.DefaultExpiration = TimeSpan.Zero);
                b.AddDistributedCache(KnownCacheProviderNames.InMemory);
            },
            o => o.DefaultCachePolicy = new CachePolicy { DistributedExpiration = TimeSpan.FromMinutes(5) });
        using var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<IDistributedCache>();

        act.Should().Throw<InvalidOperationException>().WithMessage("*would expire immediately*");
    }

    /// <summary>A non-positive tier default would make writes without a caller expiration expire immediately.</summary>
    [Fact]
    public void Non_positive_tier_default_expiration_fails_fast()
    {
        var services = new ServiceCollection();
        services.AddCaching(b =>
        {
            b.Services.Configure<InMemoryCacheOptions>(o => o.DefaultExpiration = TimeSpan.Zero);
            b.AddDistributedCache(KnownCacheProviderNames.InMemory);
        });
        using var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<IDistributedCache>();

        act.Should().Throw<InvalidOperationException>().WithMessage("*would expire immediately*");
    }

    /// <summary>A non-positive policy expiration is rejected the same way when it is the resolved fallback.</summary>
    [Fact]
    public void Non_positive_policy_distributed_expiration_fails_fast()
    {
        var services = new ServiceCollection();
        services.AddCaching(
            b =>
            {
                b.Services.Configure<InMemoryCacheOptions>(o => o.DefaultExpiration = null);
                b.AddDistributedCache(KnownCacheProviderNames.InMemory);
            },
            o => o.DefaultCachePolicy = new CachePolicy { DistributedExpiration = TimeSpan.FromSeconds(-1) });
        using var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<IDistributedCache>();

        act.Should().Throw<InvalidOperationException>().WithMessage("*would expire immediately*");
    }

    /// <summary>
    /// The multilayer tiers apply ICacheOptions.CacheKeyStrategy on top of the key the adapter composed, so an
    /// application strategy configured on the tier must not transform these keys a second time.
    /// </summary>
    [Theory]
    [InlineData(KnownCacheProviderNames.InMemory)]
    [InlineData(KnownCacheProviderNames.InMemoryRedis)]
    public void Application_tier_key_strategy_does_not_reach_the_distributed_keys(string providerName)
    {
        var services = new ServiceCollection();
        services.AddCaching(
            b =>
            {
                b.AddRedisConnection(o => o.ConnectionString = "localhost:6379,abortConnect=false");
                b.AddInMemoryRedis(o => o.CacheKeyStrategy = new LowercasingCacheKeyStrategy());
                b.AddMemory(o => o.CacheKeyStrategy = new LowercasingCacheKeyStrategy());
                b.AddDistributedCache(providerName);
            },
            o => o.AppShortName = "app");
        using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<IDistributedCache>();

        cache.Set("AbC", [1], new DistributedCacheEntryOptions());

        // A strategy that lowercased these keys would make the two spellings one entry.
        cache.Get("abc").Should().BeNull();
    }

    private sealed class LowercasingCacheKeyStrategy : ICacheKeyStrategy
    {
        public CacheKey GetCacheKey<T>(CacheKey key) => new(key.Name, CacheKeyCasing.Insensitive);
    }

    /// <summary>Registration-time, not resolve-time: a typo must not survive until the first cache hit.</summary>
    [Fact]
    public void Redis_key_differentiator_is_validated_before_the_container_is_built()
    {
        var services = new ServiceCollection();

        var act = () => services.AddCaching(b => b.AddDistributedCache(
            KnownCacheProviderNames.Redis, o => o.RedisKeyDifferentiator = RedisTypePrefixes.Hash));

        act.Should().Throw<InvalidOperationException>();
    }
}
