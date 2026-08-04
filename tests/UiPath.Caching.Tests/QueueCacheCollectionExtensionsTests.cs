using Microsoft.Extensions.DependencyInjection;
using UiPath.Caching.Config;
using UiPath.Caching.Queue.Config;

namespace UiPath.Caching.Tests;

public class QueueCacheCollectionExtensionsTests
{
    [Fact]
    public void AddQueueRedis_with_ResilienceKeyName_propagates_to_RedisSetCacheOptions()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(builder => builder.AddQueueRedis(o => o.ResilienceKeyName = "set-pop"));
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<RedisSetCacheOptions>>().Value;
        options.ResilienceKeyName.Should().Be("set-pop");
    }

    [Fact]
    public void AddQueueRedis_without_configuration_leaves_ResilienceKeyName_null()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(builder => builder.AddQueueRedis());
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<RedisSetCacheOptions>>().Value;
        options.ResilienceKeyName.Should().BeNull();
    }

    [Fact]
    public void AddQueueMemory_resolves_in_memory_set_cache_and_factory()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // The factory resolves the default provider from CacheOptions.DefaultCache (InMemoryRedis
        // when unset), exactly like CacheFactory — point it at the InMemory provider.
        services.AddCaching(
            builder => builder.AddQueueMemory(static _ => { }),
            opt => opt.DefaultCache = KnownCacheProviderNames.InMemory);
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<ISetCache>().Should().BeOfType<MultilayerSetCache>();

        var factory = provider.GetRequiredService<IQueueCacheFactory>();
        var setCache = factory.CreateSetCache();
        setCache.Should().BeOfType<MultilayerSetCache>();
        setCache.Name.Should().Be(KnownCacheProviderNames.InMemory);
        factory.ProviderNames.Should().Contain(KnownCacheProviderNames.InMemory);

        provider.GetRequiredService<ISetCache<string>>().Should().BeOfType<SetCache<string>>();
    }

    [Fact]
    public void AddQueueMemory_without_default_cache_configured_resolves_null_set_cache()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(builder => builder.AddQueueMemory(static _ => { }));
        using var provider = services.BuildServiceProvider();

        // Mirrors CacheFactory: an unregistered default (InMemoryRedis when unset) resolves to the
        // null cache — there is no fallback to the sole registered provider.
        provider.GetRequiredService<ISetCache>().Should().BeSameAs(NullSetCache.Instance);
        provider.GetRequiredService<IQueueCacheFactory>()
            .CreateSetCache(KnownCacheProviderNames.InMemory).Should().BeOfType<MultilayerSetCache>();
    }

    [Fact]
    public void AddQueueMemory_disabled_registers_null_set_cache()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(builder => builder.AddQueueMemory(o => o.Enabled = false));
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<ISetCache>().Should().BeSameAs(NullSetCache.Instance);
        provider.GetRequiredService<IQueueCacheFactory>().Should().BeSameAs(NullQueueCacheFactory.Instance);
    }

    [Fact]
    public void AddQueueInMemoryRedis_registers_providers_and_options()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(builder => builder.AddQueueInMemoryRedis(o => o.LocalMaxExpiration = TimeSpan.FromSeconds(30)));
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IOptions<InMemoryRedisQueueCacheOptions>>().Value.LocalMaxExpiration
            .Should().Be(TimeSpan.FromSeconds(30));
        services.Should().Contain(d =>
            d.ServiceType == typeof(IQueueCacheProvider) &&
            d.ImplementationType == typeof(InMemoryRedisQueueCacheProvider));
        // The multilayer provider resolves its Redis L2 through the factory, so the Redis backing
        // is co-registered.
        services.Should().Contain(d =>
            d.ServiceType == typeof(IQueueCacheProvider) &&
            d.ImplementationType == typeof(RedisQueueCacheProvider));
    }
}
