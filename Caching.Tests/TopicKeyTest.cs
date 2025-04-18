namespace UiPath.Platform.Caching.Tests;

public class TopicKeyTest
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubstitute();

    [Fact]
    public void AllOperations()
    {
        new TopicKey(null).IsNull.Should().BeTrue();
        new TopicKey().IsNull.Should().BeTrue();
        new TopicKey(string.Empty).IsNull.Should().BeTrue();
        new TopicKey("         ").IsNull.Should().BeTrue();
        var value = _fixture.Create<string>().ToUpperInvariant();
        Assert.True(new TopicKey(value) == new TopicKey(value.ToLowerInvariant()));
        Assert.True(new TopicKey(value) == value.ToLowerInvariant());
        Assert.True(new TopicKey(value) != new TopicKey(value.ToLowerInvariant() + _fixture.Create<string>()));
        Assert.True(new TopicKey(value) != value.ToLowerInvariant() + _fixture.Create<string>());
        Assert.True(new TopicKey(value).GetHashCode() != 0);
        Assert.True(new TopicKey(value).GetHashCode() == new TopicKey(value.ToLowerInvariant()).GetHashCode());
    }
}
