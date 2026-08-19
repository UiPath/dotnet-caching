using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using UiPath.Caching.Config;
using UiPath.Caching.Distributed;

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
        provider.GetRequiredKeyedService<ICache>(DistributedCacheCollectionExtensions.DistributedCacheServiceKey)
            .Should().NotBeNull();
    }

    [Fact]
    public void Keyed_cache_is_a_separate_instance_from_the_apps_cache()
    {
        using var provider = Build(KnownCacheProviderNames.InMemory);
        var appCache = provider.GetRequiredService<ICacheFactory>().CreateCache(KnownCacheProviderNames.InMemory);
        var distributedCache = provider.GetRequiredKeyedService<ICache>(DistributedCacheCollectionExtensions.DistributedCacheServiceKey);
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
}
