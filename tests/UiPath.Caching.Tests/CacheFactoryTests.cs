using NSubstitute.ClearExtensions;

namespace UiPath.Caching.Tests;

public class CacheFactoryTests : IAsyncLifetime
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubstitute();
    private CacheOptions _cacheOptions = default!;
    private List<ICacheProvider> _providers = default!;
    private ICacheProvider _defaultProvider = default!;

    private CacheFactory? _sut = null;
    private CacheFactory Sut => _sut ??= new CacheFactory(Options.Create(_cacheOptions), _providers);

    [Theory]
    [InlineData("")]
    [InlineData("unk")]
    public void Unknown_provider_resolves_to_null(string defaultCache)
    {
        _cacheOptions.DefaultCache = defaultCache;
        var cacheName = _fixture.Create<string>();
        Sut.CreateCache(cacheName).Should().BeOfType<NullCache>();
        Sut.CreateHashCache(cacheName).Should().BeOfType<NullHashCache>();
    }


    [Fact]
    public void Works_as_expected()
    {
        var provider = _providers.Where(p => p.Name != _defaultProvider.Name).ToList().First();
        var cache = _fixture.Create<ICache>();
        var hashCache = _fixture.Create<IHashCache>();
        provider.CreateCache().Returns(cache);
        provider.CreateHashCache().Returns(hashCache);
        Sut.CreateCache(provider.Name).Should().Be(cache);
        Sut.CreateHashCache(provider.Name).Should().Be(hashCache);
    }

    [Fact]
    public void empty_factory()
    {
        _sut = new CacheFactory(Options.Create(_cacheOptions));
        Sut.CreateCache(_fixture.Create<string>()).Should().Be(NullCache.Instance);
        Sut.CreateHashCache(_fixture.Create<string>()).Should().Be(NullHashCache.Instance);
    }

    [Fact]
    public void Default_works_as_expected()
    {
        var cache = _fixture.Create<ICache>();
        var hashCache = _fixture.Create<IHashCache>();
        _defaultProvider.CreateCache().Returns(cache);
        _defaultProvider.CreateHashCache().Returns(hashCache);
        Sut.CreateCache(_defaultProvider.Name).Should().Be(cache);
        Sut.CreateHashCache(_defaultProvider.Name).Should().Be(hashCache);
        Sut.ProviderNames.Should().Contain(_defaultProvider.Name);
    }


    [Fact]
    public void Disabled_provider_ignored()
    {
        var provider = _providers.Where(p => p.Name != _defaultProvider.Name).ToList().First();
        provider.ClearSubstitute();
        provider.Enabled.Returns(false);
        var cache = _fixture.Create<ICache>();
        var hashCache = _fixture.Create<IHashCache>();
        _defaultProvider.CreateCache().Returns(cache);
        _defaultProvider.CreateHashCache().Returns(hashCache);

        Sut.CreateCache(provider.Name).Should().Be(cache);
        Sut.CreateHashCache(provider.Name).Should().Be(hashCache);
        Sut.ProviderNames.Should().NotContain(provider.Name);
    }

    [Fact]
    public void Disposed_factory_add_provider_exception()
    {
        Sut.Dispose();
        Action act = () => Sut.AddProvider(_fixture.Create<ICacheProvider>());
        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void Dispose_provider_exception()
    {
        var f = _providers.First();
        f.When(x => x.Dispose()).Throw(new Exception("test"));
        var act = () => Sut.Dispose();
        act.Should().NotThrow<Exception>();
    }

    [Fact]
    public void Add_provider()
    {
        var provider = _fixture.Create<ICacheProvider>();
        provider.Enabled.Returns(true);
        var cache = _fixture.Create<ICache>();
        var hashCache = _fixture.Create<IHashCache>();
        provider.CreateCache().Returns(cache);
        provider.CreateHashCache().Returns(hashCache);
        Sut.AddProvider(provider);
        Sut.CreateCache(provider.Name).Should().Be(cache);
        Sut.CreateHashCache(provider.Name).Should().Be(hashCache);
    }

    [Fact]
    public void Add_disabled_provider()
    {
        var provider = _fixture.Create<ICacheProvider>();
        provider.Enabled.Returns(false);
        var cache = _fixture.Create<ICache>();
        var hashCache = _fixture.Create<IHashCache>();
        provider.CreateCache().Returns(cache);
        provider.CreateHashCache().Returns(hashCache);
        Sut.AddProvider(provider);
        Sut.CreateCache(provider.Name).Should().NotBe(cache);
        Sut.CreateHashCache(provider.Name).Should().NotBe(hashCache);
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    public ValueTask InitializeAsync()
    {
        _providers = _fixture.CreateMany<ICacheProvider>().ToList();
        _providers.ForEach(p => p.Enabled.Returns(true));
        _defaultProvider = _providers.Skip(1).First();
        _cacheOptions = new CacheOptions
        {
            Enabled = true,
            DefaultCache = _defaultProvider.Name,
        };
        return ValueTask.CompletedTask;
    }
}
