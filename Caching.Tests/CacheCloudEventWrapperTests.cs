using CloudNative.CloudEvents;
using UiPath.Platform.Caching.CloudEvents;

namespace UiPath.Platform.Caching.Tests;
public class CacheCloudEventWrapperTests
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubstitute();

    [Fact]
    public void Works_as_expected()
    {
        var cloudEvent = new CloudEvent
        {
            Type = _fixture.Create<string>(),
            Id = _fixture.Create<string>(),
            Source = new Uri("urn:" + _fixture.Create<string>()),
            Data = _fixture.Create<CacheEventData>()
        };
        var sut = new CacheCloudEventWrapper(cloudEvent);
        sut.Type.Should().BeEquivalentTo(cloudEvent.Type);
        sut.Id.Should().BeEquivalentTo(cloudEvent.Id);
        sut.Source.Should().Be(cloudEvent.Source);
        sut.Data.Should().Be(cloudEvent.Data);
        sut.CloudEvent.Should().Be(cloudEvent);
        sut.IsValid().Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void InvalidKey(string? key)
    {
        var cloudEvent = new CloudEvent
        {
            Type = _fixture.Create<string>(),
            Id = _fixture.Create<string>(),
            Source = new Uri("urn:" + _fixture.Create<string>()),
            Data = key == null ? null : new CacheEventData(key)
        };
        var sut = new CacheCloudEventWrapper(cloudEvent);
        sut.IsValid().Should().BeFalse();
    }
}
