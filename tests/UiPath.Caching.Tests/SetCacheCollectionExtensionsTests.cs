using Microsoft.Extensions.DependencyInjection;
using UiPath.Caching.Config;
using UiPath.Caching.Queue.Config;

namespace UiPath.Caching.Tests;

public class SetCacheCollectionExtensionsTests
{
    [Fact]
    public void AddRedisSetCache_with_ResilienceKeyName_propagates_to_RedisSetCacheOptions()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(builder => builder.AddRedisSetCache(o => o.ResilienceKeyName = "set-pop"));
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<RedisSetCacheOptions>>().Value;
        options.ResilienceKeyName.Should().Be("set-pop");
    }

    [Fact]
    public void AddRedisSetCache_without_configuration_leaves_ResilienceKeyName_null()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(builder => builder.AddRedisSetCache());
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<RedisSetCacheOptions>>().Value;
        options.ResilienceKeyName.Should().BeNull();
    }

    [Fact]
    public void AddRedisSetCache_on_service_collection_propagates_ResilienceKeyName()
    {
        var services = new ServiceCollection();
        services.AddRedisSetCache(o => o.ResilienceKeyName = "set-pop");
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<RedisSetCacheOptions>>().Value;
        options.ResilienceKeyName.Should().Be("set-pop");
    }

    [Fact]
    public void AddInMemorySetCache_resolves_in_memory_set_cache_and_factory()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(builder => builder.AddInMemorySetCache());
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
    public void AddInMemorySetCache_disabled_registers_null_set_cache()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(builder => builder.AddInMemorySetCache(o => o.Enabled = false));
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<ISetCache>().Should().BeSameAs(NullSetCache.Instance);
        provider.GetRequiredService<IQueueCacheFactory>().Should().BeSameAs(NullQueueCacheFactory.Instance);
    }

    [Fact]
    public void AddInMemoryRedisSetCache_registers_provider_and_options()
    {
        var services = new ServiceCollection();
        services.AddInMemoryRedisSetCache(o => o.LocalMaxExpiration = TimeSpan.FromSeconds(30));
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IOptions<InMemoryRedisSetCacheOptions>>().Value.LocalMaxExpiration
            .Should().Be(TimeSpan.FromSeconds(30));
        services.Should().Contain(d =>
            d.ServiceType == typeof(ISetCacheProvider) &&
            d.ImplementationType == typeof(InMemoryRedisSetCacheProvider));
    }
}
