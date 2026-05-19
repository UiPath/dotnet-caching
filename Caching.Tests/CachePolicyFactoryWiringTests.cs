using Microsoft.Extensions.DependencyInjection;
using UiPath.Platform.Caching.Config;

namespace UiPath.Platform.Caching.Tests;

public class CachePolicyFactoryWiringTests
{
    [Fact]
    public void AddCaching_resolves_ICachePolicyFactory_from_DI()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(builder => builder.AddMemory(_ => { }));
        using var provider = services.BuildServiceProvider();

        provider.GetService<ICachePolicyFactory>().Should().NotBeNull();
    }

    [Fact]
    public void Factory_returns_user_DefaultCachePolicy_for_unregistered_name()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(
            builder => builder.AddMemory(_ => { }),
            opt => opt.DefaultCachePolicy = new CachePolicy { LocalExpiration = TimeSpan.FromMinutes(5) });
        using var provider = services.BuildServiceProvider();

        var factory = provider.GetRequiredService<ICachePolicyFactory>();
        var policy = factory.Resolve("unregistered-name");

        policy.LocalExpiration.Should().Be(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void AddMemory_factory_default_does_not_bleed_InMemoryCacheOptions_lock_fields()
    {
        // factory.Default must not carry the InMemory provider's lock fields — those are applied
        // at call time by each provider's impl reading `policyLock?.X ?? _localLockEnabled`. Bleeding
        // them through the process-wide factory would silently flow them into Cache<T> instances
        // backed by a DIFFERENT provider in mixed-provider setups.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(
            builder => builder.AddMemory(opt =>
            {
                opt.LocalLockEnabled = false;
                opt.DistributedLockTimeout = TimeSpan.FromSeconds(3);
            }),
            opt => opt.DefaultCache = KnownCacheProviderNames.InMemory);
        using var provider = services.BuildServiceProvider();

        var factory = provider.GetRequiredService<ICachePolicyFactory>();
        var policy = factory.Resolve("anything");

        policy.Lock.Should().BeNull(
            "factory.Default must not carry builder-derived lock fields — the provider impl applies them at call time");
    }

    [Fact]
    public void AddInMemoryRedis_factory_default_does_not_bleed_InMemoryRedisCacheOptions_lock_fields()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(builder => builder.AddInMemoryRedis(opt =>
        {
            opt.LocalLockEnabled = false;
            opt.DistributedLockExpiry = TimeSpan.FromSeconds(15);
        }));
        using var provider = services.BuildServiceProvider();

        var factory = provider.GetRequiredService<ICachePolicyFactory>();
        var policy = factory.Resolve("anything");

        policy.Lock.Should().BeNull(
            "factory.Default must not carry builder-derived lock fields — the provider impl applies them at call time");
    }

    [Fact]
    public void Factory_returns_named_policy_merged_with_default()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(
            builder => builder.AddMemory(_ => { }),
            opt =>
            {
                opt.Policies["clients-cache"] = new CachePolicy { LocalExpiration = TimeSpan.FromMinutes(1) };
                opt.DefaultCachePolicy = new CachePolicy { DistributedExpiration = TimeSpan.FromMinutes(10) };
            });
        using var provider = services.BuildServiceProvider();

        var factory = provider.GetRequiredService<ICachePolicyFactory>();
        var policy = factory.Resolve("clients-cache");

        policy.LocalExpiration.Should().Be(TimeSpan.FromMinutes(1), "from named policy");
        policy.DistributedExpiration.Should().Be(TimeSpan.FromMinutes(10), "inherited from user DefaultCachePolicy");
    }

    [Fact]
    public void Multiple_providers_factory_default_does_not_bleed_any_provider_options()
    {
        // With multiple multilayer providers registered, factory.Default must NOT carry any
        // builder-derived defaults — a Cache<T> wrapping CreateCache("InMemory") would otherwise
        // silently receive InMemoryRedis's LocalMaxExpiration / lock fields just because
        // CacheOptions.DefaultCache points at InMemoryRedis. Provider-specific defaults are
        // applied at call time inside each impl, not via the factory.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(
            builder => builder
                .AddMemory(opt =>
                {
                    opt.LocalMaxExpiration = TimeSpan.FromMinutes(1);
                    opt.LocalLockEnabled = false;
                })
                .AddInMemoryRedis(opt =>
                {
                    opt.LocalMaxExpiration = TimeSpan.FromMinutes(7);
                    opt.LocalLockEnabled = true;
                }),
            opt => opt.DefaultCache = KnownCacheProviderNames.InMemoryRedis);
        using var provider = services.BuildServiceProvider();

        var factory = provider.GetRequiredService<ICachePolicyFactory>();
        var policy = factory.Resolve("anything");

        policy.LocalExpiration.Should().BeNull("factory.Default must not bleed any provider's LocalMaxExpiration");
        policy.Lock.Should().BeNull("factory.Default must not bleed any provider's lock fields");
    }

    [Fact]
    public void No_provider_default_when_DefaultCache_has_no_matching_builder()
    {
        // Register an InMemoryRedis builder but point DefaultCache at "Redis", which has no
        // ICachePolicyDefaultBuilder. The factory must NOT silently fall back to the first
        // registered builder — that would map InMemoryRedis's LocalMaxExpiration / lock
        // settings onto Cache<T> instances backed by a different provider.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(
            builder => builder.AddInMemoryRedis(opt =>
            {
                opt.LocalMaxExpiration = TimeSpan.FromMinutes(7);
                opt.LocalLockEnabled = false;
            }),
            opt => opt.DefaultCache = KnownCacheProviderNames.Redis);
        using var provider = services.BuildServiceProvider();

        var factory = provider.GetRequiredService<ICachePolicyFactory>();
        var policy = factory.Resolve("anything");

        policy.LocalExpiration.Should().BeNull("no builder matches Redis, so no provider-derived default");
        policy.Lock.Should().BeNull("provider-derived lock must not leak from an unrelated builder");
    }

    [Fact]
    public void User_DefaultCachePolicy_wins_over_provider_fallback()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(
            builder => builder.AddMemory(opt => opt.LocalLockEnabled = false),
            opt => opt.DefaultCachePolicy = new CachePolicy { LocalExpiration = TimeSpan.FromMinutes(5) });
        using var provider = services.BuildServiceProvider();

        var factory = provider.GetRequiredService<ICachePolicyFactory>();
        var policy = factory.Resolve("anything");

        policy.LocalExpiration.Should().Be(TimeSpan.FromMinutes(5));
        policy.Lock.Should().BeNull("user-supplied default wins; provider fallback isn't consulted");
    }

    [Fact]
    public void Factory_throws_when_merged_with_builder_default_lock_produces_invalid_combo()
    {
        // Builder-derived default has LocalLockEnabled=false; named policy flips
        // DistributedLockEnabled=true. The IValidateOptions pipeline can't see the builder's
        // contribution (would form a DI cycle), so the validation runs at factory construction.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(
            builder => builder.AddInMemoryRedis(opt => opt.LocalLockEnabled = false),
            opt => opt.Policies["named"] = new CachePolicy { Lock = new LockProfile { DistributedLockEnabled = true } });
        using var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<ICachePolicyFactory>();

        act.Should().Throw<OptionsValidationException>()
            .Which.Failures.Should().Contain(f => f.Contains("named") && f.Contains(nameof(LockProfile.LocalLockEnabled)));
    }

    [Fact]
    public void Obsolete_PrimaryMax_aliases_still_forward_into_LocalMax_fields()
    {
        // Backcompat: master-branch consumers who set the old property names (in code or via
        // appsettings binding) should see those values land in the new LocalMax* properties.
        var services = new ServiceCollection();
        services.AddLogging();
#pragma warning disable CS0618 // testing the obsolete alias
        services.AddCaching(builder => builder.AddInMemoryRedis(opt =>
        {
            opt.PrimaryMaxExpiration = TimeSpan.FromMinutes(4);
            opt.PrimaryMaxExpirationDisconnected = TimeSpan.FromSeconds(45);
            opt.UsePrimaryOnlyWhenDisconnected = true;
        }));
#pragma warning restore CS0618
        using var provider = services.BuildServiceProvider();

        var opts = provider.GetRequiredService<IOptions<InMemoryRedisCacheOptions>>().Value;
        opts.LocalMaxExpiration.Should().Be(TimeSpan.FromMinutes(4));
        opts.LocalMaxExpirationDisconnected.Should().Be(TimeSpan.FromSeconds(45));
        opts.UseLocalOnlyWhenDisconnected.Should().Be(true);
    }

    [Fact]
    public void CacheOptions_Policies_defaults_to_empty_dictionary()
    {
        new CacheOptions().Policies.Should().BeEmpty();
    }

    [Fact]
    public void CacheOptions_DefaultCachePolicy_defaults_to_null()
    {
        new CacheOptions().DefaultCachePolicy.Should().BeNull();
    }
}
