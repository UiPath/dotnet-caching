namespace UiPath.Caching.Tests;

public class CacheEventFactoryTests : IAsyncLifetime
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubstitute();
    private Uri? _source = null;
    private CacheOptions _cacheOptions = default!;
    private string _type = default!;
    private string _cacheName = default!;
    private CacheEventData _data = default!;
    private string? _id;

    private CacheEventFactory? _sut = null;
    private CacheEventFactory Sut => _sut ??= _fixture.Create<CacheEventFactory>();

    [Fact]
    public void Create_with_no_id()
    {
        _id = default;
        var @event = Sut.Create(_cacheName, _type, _data, _id);
        @event.Should().NotBeNull().And.BeOfType<CacheEvent>();
        @event!.Type.Should().Be(_type);
        @event!.Source.Should().Be(_source);
        @event.Data.Should().Be(_data);
        @event.Id.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Create_with_id()
    {

        var @event = Sut.Create(_cacheName, _type, _data, _id);
        @event.Should().NotBeNull().And.BeOfType<CacheEvent>();
        @event!.Type.Should().Be(_type);
        @event!.Source.Should().Be(_source);
        @event.Data.Should().Be(_data);
        @event.Id.Should().Be(_id);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Exception_is_thrown_type_invalid(string type)
    {

        _data = _fixture.Create<CacheEventData>();
        Action act = () => Sut.Create(_cacheName, type, _data);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_with_no_source_options()
    {
        _cacheOptions.SourceUri = null;
        var @event = Sut.Create(_cacheName, _type, _data, _id);
        @event.Should().NotBeNull().And.BeOfType<CacheEvent>();
        @event!.Type.Should().Be(_type);
        @event!.Source.Should().NotBeNull();
        @event.Data.Should().Be(_data);
        @event.Id.Should().Be(_id);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData(" ", false)]
    [InlineData("abc", false)]
    [InlineData("CacheSet", true)]
    [InlineData("CacheRefreshed", true)]
    [InlineData("CacheRemoved", true)]
    [InlineData("CACHESET", true)]
    [InlineData("cacherefreshed", true)]
    public void known_events(string? eventType, bool isKnown)
    {
        var actual = Sut.IsKnown(eventType);
        actual.Should().Be(isKnown);
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    public ValueTask InitializeAsync()
    {
        _source = new Uri($"urn:{_fixture.Create<string>()}");
        _type = _fixture.Create<string>();
        _data = _fixture.Create<CacheEventData>();
        _id = _fixture.Create<string>();
        _cacheName = _fixture.Create<string>();

        _cacheOptions = new CacheOptions
        {
            SourceUri = _source,
        };
        _fixture.Inject(Options.Create(_cacheOptions));
        return ValueTask.CompletedTask;
    }
}
