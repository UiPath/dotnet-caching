using Microsoft.Extensions.DependencyInjection;
using UiPath.Platform.Caching.Config;

namespace UiPath.Platform.Caching.Tests;

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
    public void QueueCacheFactory_hands_out_the_registered_set_cache()
    {
        var inner = Substitute.For<ISetCache>();

        var sut = new QueueCacheFactory(inner);

        sut.CreateSetCache().Should().BeSameAs(inner);
    }

    [Fact]
    public void QueueCacheFactory_throws_when_set_cache_is_null()
    {
        var act = () => new QueueCacheFactory(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void AddRedisSetCache_registers_IQueueCacheFactory()
    {
        var services = new ServiceCollection();

        services.AddRedisSetCache();

        services.Should().ContainSingle(d =>
            d.ServiceType == typeof(IQueueCacheFactory) && d.ImplementationType == typeof(QueueCacheFactory));
    }

    public class MyService { }
}
