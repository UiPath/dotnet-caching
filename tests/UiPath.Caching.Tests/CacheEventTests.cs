namespace UiPath.Caching.Tests;

public class CacheEventTests
{
    [Theory]
    [InlineData(null, null, null, null, false)]
    [InlineData("id", "urn:machine", "type", "key", true)]
    [InlineData("  ", "urn:machine", "type", "key", false)]
    [InlineData("id", null, "type", "key", false)]
    [InlineData("id", "urn:machine", "  ", "key", false)]
    [InlineData("id", "urn:machine", "type", "  ", false)]
    public void Works_as_expected(string? id, string? url, string? type, string? key, bool isValid)
    {
        var sut = new CacheEvent
        {
            Id = id,
            Source = url == null ? null : new Uri(url),
            Type = type,
            Data = key == null ? null : new CacheEventData(key)
        };
        sut.IsValid().Should().Be(isValid);
    }
}
