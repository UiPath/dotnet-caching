using CloudNative.CloudEvents;
using CloudNative.CloudEvents.SystemTextJson;
using UiPath.Caching.CloudEvents;

namespace UiPath.Caching.Tests;

public class CacheEventFormatterProxyTests
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubstitute();
    private CacheEventFormatterProxy _sut = new CacheEventFormatterProxy(new JsonEventFormatter<CacheEventData>());

    [Fact]
    public void Works_as_expected()
    {
        _sut.Decode(ReadOnlyMemory<byte>.Empty).Should().BeNull();
        _sut.Encode(_fixture.Create<ICacheEvent>()).Should().Be(ReadOnlyMemory<byte>.Empty);

        var ev = new CacheCloudEventWrapper(new CloudEvent()
        {
            Id = _fixture.Create<string>(),
            Type = _fixture.Create<string>(),
            Source = new Uri("urn:"+_fixture.Create<string>()),
            Data = _fixture.Create<CacheEventData>(),
        });
        var mem = _sut.Encode(ev);
        var ev2 = _sut.Decode(mem);
        ev2.Should().BeEquivalentTo(ev);
        
    }
}
