using UiPath.Platform.Caching.CloudEvents;

namespace UiPath.Platform.Caching.Tests;

public class CloudCacheEventFactoryTests : IAsyncLifetime
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubsitute();
    private Uri? _source = null;
    private CacheOptions _cacheOptions = default!;
    private string _type = default!;
    private string _cacheName = default!;
    private CacheEventData _data = default!;
    private string? _id;

    private CloudCacheEventFactory? _sut = null;
    private CloudCacheEventFactory Sut => _sut ??= _fixture.Create<CloudCacheEventFactory>();

    [Fact]
    public void Create_with_no_id()
    {
        string? id = default;
        var @event = Sut.Create(_cacheName, _type, _data, id);
        @event.Should().NotBeNull().And.BeOfType<CacheCloudEventWrapper>();
        @event!.Type.Should().Be(_type);
        @event!.Source.Should().Be(_source);
        @event.Data.Should().Be(_data);
        @event.Id.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Create_with_id()
    {
        var @event = Sut.Create(_cacheName, _type, _data, _id);
        @event.Should().NotBeNull().And.BeOfType<CacheCloudEventWrapper>();
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

        var data = _fixture.Create<CacheEventData>();
        Action act = () => Sut.Create(_cacheName, type, data);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_with_no_source_options()
    {
        _cacheOptions.SourceUri = null;
        var type = _fixture.Create<string>();
        var data = _fixture.Create<CacheEventData>();
        string? id = _fixture.Create<string>();
        var @event = Sut.Create(_cacheName, type, data, id);
        @event.Should().NotBeNull().And.BeOfType<CacheCloudEventWrapper>();
        @event!.Type.Should().Be(type);
        @event!.Source.Should().NotBeNull();
        @event.Data.Should().Be(data);
        @event.Id.Should().Be(id);
    }

    [Theory]
    [InlineData("CacheSet")]
    [InlineData("CacheRefreshed")]
    [InlineData("CacheRemoved")]
    [InlineData("CACHESET")]
    [InlineData("cacherefreshed")]
    public void known_event_types(string? eventType)
    {
        var actual = Sut.IsKnown(eventType);
        actual.Should().BeTrue();
    }

    [Theory]
    [InlineData("  ")]
    [InlineData("")]
    [InlineData(null)]
    public void unknown_event_types(string? eventType)
    {
        var actual = Sut.IsKnown(eventType);
        actual.Should().BeFalse();
    }


    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    public Task InitializeAsync()
    {
        _source = new Uri($"urn:{_fixture.Create<string>()}");
        _type = _fixture.Create<string>();
        _data = _fixture.Create<CacheEventData>();
        _id = _fixture.Create<string>();
        _cacheOptions = new CacheOptions
        {
            SourceUri = _source,
        };
        _fixture.Inject(Options.Create(_cacheOptions));
        return Task.CompletedTask;
    }
}

