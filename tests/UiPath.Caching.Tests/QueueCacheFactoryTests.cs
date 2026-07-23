using Microsoft.Extensions.DependencyInjection;
using UiPath.Caching.Config;
using UiPath.Caching.Queue.Config;

namespace UiPath.Caching.Tests;

public class QueueCacheFactoryTests
{
    [Fact]
    public void CreateSetCache_returns_the_factory_set_cache()
    {
        var inner = Substitute.For<ISetCache>();
        var factory = Substitute.For<IQueueCacheFactory>();
        factory.CreateSetCache().Returns(inner);

        factory.CreateSetCache().Should().BeSameAs(inner);
    }

    [Fact]
    public void CreateSetCache_of_T_wraps_the_factory_set_cache()
    {
        var inner = Substitute.For<ISetCache>();
        inner.Name.Returns("redis");
        var factory = Substitute.For<IQueueCacheFactory>();
        factory.CreateSetCache().Returns(inner);

        var typed = factory.CreateSetCache<MyService>();

        typed.Should().BeOfType<SetCache<MyService>>();
        typed.Name.Should().Be("redis");
    }

    [Fact]
    public void AddQueueRedis_registers_IQueueCacheFactory()
    {
        var services = new ServiceCollection();

        services.AddCaching(builder => builder.AddQueueRedis());

        // The factory is registered from the set of IQueueCacheProvider registrations, so it is wired
        // via a factory delegate rather than an implementation type.
        services.Should().ContainSingle(d => d.ServiceType == typeof(IQueueCacheFactory));
        services.Should().ContainSingle(d =>
            d.ServiceType == typeof(IQueueCacheProvider) && d.ImplementationType == typeof(RedisQueueCacheProvider));
    }

    [Fact]
    public void NullQueueCacheFactory_CreateSetCache_returns_NullSetCache()
    {
        NullQueueCacheFactory.Instance.CreateSetCache().Should().BeSameAs(NullSetCache.Instance);
    }

    [Fact]
    public void AddQueueRedis_when_caching_disabled_registers_NullQueueCacheFactory()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(
            builder => builder.AddQueueRedis(),
            opt => opt.Enabled = false);
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IQueueCacheFactory>().Should().BeOfType<NullQueueCacheFactory>();
    }

    [Fact]
    public void AddQueueRedis_when_caching_disabled_still_allows_dependent_service_to_be_constructed()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(
            builder => builder.AddQueueRedis(),
            opt => opt.Enabled = false);
        services.AddSingleton<DependsOnQueueCacheFactory>();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });

        var sut = provider.GetRequiredService<DependsOnQueueCacheFactory>();
        sut.SetCache.Should().BeSameAs(NullSetCache.Instance);
    }

    public class MyService { }

    private sealed class DependsOnQueueCacheFactory
    {
        public DependsOnQueueCacheFactory(IQueueCacheFactory factory) => SetCache = factory.CreateSetCache();

        public ISetCache SetCache { get; }
    }
}
