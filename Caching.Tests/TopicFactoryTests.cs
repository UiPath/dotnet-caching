using NSubstitute.ClearExtensions;

namespace UiPath.Platform.Caching.Tests;

public class TopicFactoryTests : IAsyncLifetime
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubsitute();
    private CacheOptions _cacheOptions = default!;
    private List<ITopicProvider> _providers = default!;
    private ITopicProvider _defaultProvider = default!;
    private TopicKey _topicKey = default!;

    private TopicFactory? _sut = null;
    private TopicFactory Sut => _sut ??= new TopicFactory(Options.Create(_cacheOptions), _providers);

    [Theory]
    [InlineData("")]
    [InlineData("unk")]
    public void Unknown_provider_resolves_to_null(string defaultCache)
    {
        _cacheOptions.DefaultTopic = defaultCache;
        var providerName = _fixture.Create<string>();
        Sut.Get(providerName, _fixture.Create<Type>()).Should().BeOfType<NullTopicProvider>();
    }


    [Fact]
    public void Works_as_expected()
    {
        var provider = _providers.Where(p => p.Name != _defaultProvider.Name).ToList().First();
        Sut.Get(provider.Name, _fixture.Create<Type>()).Should().Be(provider);
    }

    [Fact]
    public void empty_factory()
    {
        _sut = new TopicFactory(Options.Create(_cacheOptions));
        Sut.Get(_fixture.Create<string>(), _fixture.Create<Type>()).Should().BeOfType<NullTopicProvider>();
    }

    [Fact]
    public void Default_works_as_expected()
    {
        Sut.Get(_defaultProvider.Name, _fixture.Create<Type>()).Should().Be(_defaultProvider);
    }


    [Fact]
    public void Disabled_provider_ignored()
    {
        var provider = _providers.Where(p => p.Name != _defaultProvider.Name).ToList().First();
        provider.ClearSubstitute();
        provider.Enabled.Returns(false);
        Sut.Get(provider.Name, _fixture.Create<Type>()).Should().NotBe(provider);
    }

    [Fact]
    public void Disposed_factory_add_provider_exception()
    {
        Sut.Dispose();
        Action act = () => Sut.AddProvider(_fixture.Create<ITopicProvider>());
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
        var provider = _fixture.Create<ITopicProvider>();
        provider.Enabled.Returns(true);
        var topic = _fixture.Create<ITopic<ICacheEvent>>();
        provider.Create(_topicKey).ReturnsForAnyArgs(topic);
        Sut.AddProvider(provider);
        Sut.Get(provider.Name, _fixture.Create<Type>()).Should().Be(provider);
    }

    [Fact]
    public void Add_disabled_provider()
    {
        var provider = _fixture.Create<ITopicProvider>();
        provider.Enabled.Returns(false);
        var topic = _fixture.Create<ITopic<ICacheEvent>>();
        provider.Create(_topicKey).Returns(topic);
        Sut.AddProvider(provider);
        Sut.Get(provider.Name, _fixture.Create<Type>()).Should().NotBe(topic);
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    public Task InitializeAsync()
    {
        _topicKey = _fixture.Create<string>();
        _providers = _fixture.CreateMany<ITopicProvider>().ToList();
        _providers.ForEach(p => p.Enabled.Returns(true));
        _defaultProvider = _providers.Skip(1).First();
        _cacheOptions = new CacheOptions
        {
            Enabled = true,
            DefaultTopic = _defaultProvider.Name,
        };
        return Task.CompletedTask;
    }
}
