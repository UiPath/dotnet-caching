using Microsoft.Extensions.Logging.Abstractions;

namespace UiPath.Caching.Tests;

public class InMemorySetCacheProviderTests
{
    private static InMemorySetCacheProvider CreateSut(InMemorySetCacheOptions? options = null) =>
        new(new MemoryCacheFactory(null, NullLoggerFactory.Instance),
            new SystemJsonSerializerProxy(),
            Options.Create(options ?? new InMemorySetCacheOptions()));

    [Fact]
    public void Creates_in_memory_set_cache()
    {
        var sut = CreateSut();

        sut.Name.Should().Be("InMemory");
        sut.Enabled.Should().BeTrue();
        sut.CreateSetCache().Should().BeOfType<MultilayerSetCache>();
        sut.CreateSetCache().Name.Should().Be("InMemory");
        sut.CreateSetCache().Should().BeSameAs(sut.CreateSetCache());
    }

    [Fact]
    public void Enabled_reflects_options()
    {
        CreateSut(new InMemorySetCacheOptions { Enabled = false }).Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task Provider_cache_stores_and_reads()
    {
        var ct = TestContext.Current.CancellationToken;
        var cache = CreateSut().CreateSetCache();

        (await cache.AddAsync("k", "a", token: ct)).Should().BeTrue();
        (await cache.AddAsync("k", "b", token: ct)).Should().BeTrue();

        (await cache.MembersAsync<string>("k", token: ct)).Should().BeEquivalentTo(new[] { "a", "b" });
        (await cache.CountAsync<string>("k", ct)).Should().Be(2);

        var popped = await cache.PopAsync<string>("k", token: ct);
        new[] { "a", "b" }.Should().Contain(popped!);
        (await cache.CountAsync<string>("k", ct)).Should().Be(1);
    }

    [Fact]
    public void Dispose_can_be_called()
    {
        var sut = CreateSut();
        _ = sut.CreateSetCache();
        var act = () => sut.Dispose();
        act.Should().NotThrow();
    }
}

public class InMemoryRedisSetCacheProviderTests
{
    [Fact]
    public async Task Resolves_its_L2_from_the_factory_Redis_provider()
    {
        var ct = TestContext.Current.CancellationToken;
        var redisL2 = Substitute.For<ISetCache>();
        redisL2.AddAsync<string>(default, default(string)!, default, ct).ReturnsForAnyArgs(_ => new ValueTask<bool>(true));

        var factory = Substitute.For<IQueueCacheFactory>();
        factory.CreateSetCache(KnownCacheProviderNames.Redis).Returns(redisL2);

        var provider = new InMemoryRedisSetCacheProvider(
            () => factory,
            new MemoryCacheFactory(null, NullLoggerFactory.Instance),
            new SystemJsonSerializerProxy(),
            Options.Create(new InMemoryRedisSetCacheOptions()));

        var cache = provider.CreateSetCache();
        cache.Should().BeOfType<MultilayerSetCache>();
        cache.Name.Should().Be(KnownCacheProviderNames.InMemoryRedis);

        await cache.AddAsync("k", "a", (CachePolicy?)null, ct);

        factory.Received(1).CreateSetCache(KnownCacheProviderNames.Redis);
        await redisL2.ReceivedWithAnyArgs(1).AddAsync<string>(default, default(string)!, default, ct);
    }
}

public class QueueCacheFactoryProviderSelectionTests
{
    private static ISetCacheProvider Provider(string name, ISetCache cache, bool enabled = true)
    {
        var provider = Substitute.For<ISetCacheProvider>();
        provider.Name.Returns(name);
        provider.Enabled.Returns(enabled);
        provider.CreateSetCache().Returns(cache);
        return provider;
    }

    [Fact]
    public void Selects_default_provider_then_by_name()
    {
        var inMem = Substitute.For<ISetCache>();
        var redis = Substitute.For<ISetCache>();
        var factory = new QueueCacheFactory(
            Options.Create(new CacheOptions { DefaultCache = KnownCacheProviderNames.Redis }),
            new[] { Provider(KnownCacheProviderNames.InMemory, inMem), Provider(KnownCacheProviderNames.Redis, redis) });

        factory.CreateSetCache().Should().BeSameAs(redis);
        factory.CreateSetCache(KnownCacheProviderNames.InMemory).Should().BeSameAs(inMem);
        factory.ProviderNames.Should().BeEquivalentTo(KnownCacheProviderNames.InMemory, KnownCacheProviderNames.Redis);
    }

    [Fact]
    public void Unknown_provider_name_falls_back_to_the_default_provider()
    {
        var inMem = Substitute.For<ISetCache>();
        var redis = Substitute.For<ISetCache>();
        var factory = new QueueCacheFactory(
            Options.Create(new CacheOptions { DefaultCache = KnownCacheProviderNames.Redis }),
            new[] { Provider(KnownCacheProviderNames.InMemory, inMem), Provider(KnownCacheProviderNames.Redis, redis) });

        factory.CreateSetCache("does-not-exist").Should().BeSameAs(redis);
    }

    [Fact]
    public void Unknown_default_returns_null_cache_even_with_single_provider()
    {
        var inMem = Substitute.For<ISetCache>();
        var factory = new QueueCacheFactory(
            Options.Create(new CacheOptions { DefaultCache = KnownCacheProviderNames.InMemoryRedis }),
            new[] { Provider(KnownCacheProviderNames.InMemory, inMem) });

        // Mirrors CacheFactory: no fallback to the sole registered provider — the default must be
        // registered (or requested by name).
        factory.CreateSetCache().Should().BeSameAs(NullSetCache.Instance);
        factory.CreateSetCache(KnownCacheProviderNames.InMemory).Should().BeSameAs(inMem);
    }

    [Fact]
    public void Unknown_provider_with_multiple_registered_returns_null_cache()
    {
        var factory = new QueueCacheFactory(
            Options.Create(new CacheOptions { DefaultCache = "does-not-exist" }),
            new[]
            {
                Provider(KnownCacheProviderNames.InMemory, Substitute.For<ISetCache>()),
                Provider(KnownCacheProviderNames.Redis, Substitute.For<ISetCache>()),
            });

        factory.CreateSetCache().Should().BeSameAs(NullSetCache.Instance);
        factory.CreateSetCache("also-missing").Should().BeSameAs(NullSetCache.Instance);
    }

    [Fact]
    public void Empty_factory_returns_null_cache()
    {
        var factory = new QueueCacheFactory(Options.Create(new CacheOptions()));

        factory.CreateSetCache().Should().BeSameAs(NullSetCache.Instance);
        factory.CreateSetCache("anything").Should().BeSameAs(NullSetCache.Instance);
        factory.ProviderNames.Should().BeEmpty();
    }

    [Fact]
    public void Disabled_provider_is_not_selected()
    {
        var factory = new QueueCacheFactory(
            Options.Create(new CacheOptions { DefaultCache = KnownCacheProviderNames.InMemory }),
            new[] { Provider(KnownCacheProviderNames.InMemory, Substitute.For<ISetCache>(), enabled: false) });

        factory.CreateSetCache().Should().BeSameAs(NullSetCache.Instance);
        factory.ProviderNames.Should().BeEmpty();
    }

    [Fact]
    public void AddProvider_registers_the_provider()
    {
        var redis = Substitute.For<ISetCache>();
        var factory = new QueueCacheFactory(Options.Create(new CacheOptions { DefaultCache = KnownCacheProviderNames.Redis }));

        factory.AddProvider(Provider(KnownCacheProviderNames.Redis, redis));

        factory.CreateSetCache().Should().BeSameAs(redis);
        factory.CreateSetCache(KnownCacheProviderNames.Redis).Should().BeSameAs(redis);
    }

    [Fact]
    public void Disposed_factory_add_provider_exception()
    {
        var factory = new QueueCacheFactory(Options.Create(new CacheOptions()));
        factory.Dispose();

        var act = () => factory.AddProvider(Provider(KnownCacheProviderNames.Redis, Substitute.For<ISetCache>()));

        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void Dispose_disposes_providers_and_swallows_exceptions()
    {
        var provider = Provider(KnownCacheProviderNames.Redis, Substitute.For<ISetCache>());
        provider.When(p => p.Dispose()).Throw(new Exception("test"));
        var factory = new QueueCacheFactory(Options.Create(new CacheOptions()), new[] { provider });

        var act = () => factory.Dispose();

        act.Should().NotThrow();
        provider.Received(1).Dispose();
    }

    [Fact]
    public void Single_cache_ctor_ignores_provider_name()
    {
        var cache = Substitute.For<ISetCache>();
        cache.Name.Returns(KnownCacheProviderNames.Redis);
        var factory = new QueueCacheFactory(cache);

        factory.CreateSetCache().Should().BeSameAs(cache);
        factory.CreateSetCache("anything").Should().BeSameAs(cache);
        factory.ProviderNames.Should().BeEquivalentTo(KnownCacheProviderNames.Redis);
    }
}
