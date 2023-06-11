namespace UiPath.Platform.Caching.Tests;

public class CacheEntryFactoryTests
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubsitute();

    [Fact]
    public void Works_as_expected()
    {
        var sut = new CacheEntryFactory();
        var value = _fixture.Create<string>();
        var expiration = _fixture.Create<DateTimeOffset>();
        var properties = _fixture.Create<IDictionary<string, string?>>();
        var original = sut.Create(value, expiration, properties);
        original.Value.Should().Be(value);
        original.Expiration.Should().Be(expiration);
        original.Metadata.Should().BeEquivalentTo(properties);
        var newExpiration = _fixture.Create<DateTimeOffset>();
        var newProperties = _fixture.Create<IDictionary<string, string?>>();
        var clone = original.NewEntry(newExpiration, newProperties);
        clone.Expiration.Should().Be(newExpiration);
        clone.Metadata.Should().BeEquivalentTo(newProperties);
    }
}
