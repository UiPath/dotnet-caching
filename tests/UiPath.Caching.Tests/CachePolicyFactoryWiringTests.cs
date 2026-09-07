using Microsoft.Extensions.DependencyInjection;
using UiPath.Caching.Config;

namespace UiPath.Caching.Tests;

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

        policy.Should().NotBeNull();
        policy!.LocalExpiration.Should().Be(TimeSpan.FromMinutes(1), "from named policy");
        policy.DistributedExpiration.Should().Be(TimeSpan.FromMinutes(10), "inherited from user DefaultCachePolicy");
    }

    [Fact]
    public void Cache_construction_throws_when_named_policy_merged_with_effective_default_lock_is_invalid()
    {
        // Provider-derived effective default has LocalLockEnabled=false; the named policy flips
        // DistributedLockEnabled=true. Cross-validation runs in MultilayerCacheBase.ctor against
        // the cache's effective default lock — surfaces when the cache is first resolved.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(
            builder => builder.AddMemory(opt => opt.LocalLockEnabled = false),
            opt => opt.Policies["named"] = new CachePolicy { Lock = new LockProfile { DistributedLockEnabled = true } });
        using var provider = services.BuildServiceProvider();

        var cacheProvider = provider.GetServices<ICacheProvider>()
            .Single(p => p.Name == KnownCacheProviderNames.InMemory);
        var act = () => cacheProvider.CreateCache();

        act.Should().Throw<OptionsValidationException>()
            .Which.Failures.Should().Contain(f => f.Contains("named") && f.Contains(nameof(LockProfile.LocalLockEnabled)));
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
