using UiPath.Platform.Caching;

namespace UiPath.Platform.Caching.Tests;

public class InMemoryRedisCacheProviderTests : IAsyncLifetime
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubstitute();
    private InMemoryRedisCacheOptions _options = default!;

    private InMemoryRedisCacheProvider? _sut = null;
    private InMemoryRedisCacheProvider Sut => _sut ??= _fixture.Create<InMemoryRedisCacheProvider>();

    [Fact]
    public void Works_as_expected()
    {
        Sut.CreateCache().Should().BeOfType<MultilayerCache>();
        Sut.CreateHashCache().Should().BeOfType<MultilayerHashCache>();
        Sut.Name.Should().Be("InMemoryRedis");
        Sut.Enabled.Should().Be(_options.Enabled);
    }


    [Fact]
    public void Dispose_can_be_called()
    {
        var x = Sut.CreateCache();
        var y = Sut.CreateHashCache();
        Action act = () => Sut.Dispose();
        act.Should().NotThrow();
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    public Task InitializeAsync()
    {
        _options = _fixture.Create<InMemoryRedisCacheOptions>();
        _fixture.Inject(Options.Create(_options));
        return Task.CompletedTask;
    }
}
