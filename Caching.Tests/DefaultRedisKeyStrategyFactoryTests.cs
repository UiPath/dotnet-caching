namespace UiPath.Platform.Caching.Tests;

public class DefaultRedisKeyStrategyFactoryTests
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubstitute();

    private DefaultRedisKeyStrategyFactory? _sut = null;

    private DefaultRedisKeyStrategyFactory Sut => _sut ??= new DefaultRedisKeyStrategyFactory();

    [Theory]
    [InlineData(typeof(ICache))]
    [InlineData(typeof(IHashCache))]
    public void Create_WhenCalled_ReturnsInstance(Type cacheType)
    {
        var options = new CacheOptions
        {
            AppShortName = _fixture.Create<string>(),
            Separator = _fixture.Create<char>(),
        };

        var result = Sut.Create(options, cacheType);

        result.Should().NotBeNull();
    }

    [Theory]
    [InlineData(typeof(ICache), "app", ' ')]
    [InlineData(typeof(IHashCache), "  ", '$')]
    [InlineData(typeof(string), "app", '$')]
    public void Create_WhenCalled_ThrowsException(Type cacheType, string appShortName, char separator)
    {
        var options = new CacheOptions
        {
            AppShortName = appShortName,
            Separator = separator,
        };

        var act = () => Sut.Create(options, cacheType);

        act.Should().Throw<Exception>();
    }
}
