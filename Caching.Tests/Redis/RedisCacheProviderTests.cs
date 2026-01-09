namespace UiPath.Platform.Caching.Tests.Redis;

public class RedisCacheProviderTests : IAsyncLifetime
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubstitute();
    private RedisCacheOptions _options = default!;

    private RedisCacheProvider? _sut = null;
    private RedisCacheProvider Sut => _sut ??= _fixture.Create<RedisCacheProvider>();

    [Fact]
    public void Works_as_expected()
    {
        Sut.CreateCache().Should().BeOfType<RedisCache>();
        Sut.CreateHashCache().Should().BeOfType<RedisHashCache>();
        Sut.Name.Should().Be("Redis");
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

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    public ValueTask InitializeAsync()
    {
        _options = _fixture.Create<RedisCacheOptions>();
        _fixture.Inject(Options.Create(_options));
        return ValueTask.CompletedTask;
    }
}
