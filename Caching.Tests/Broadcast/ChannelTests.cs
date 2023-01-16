namespace UiPath.Platform.Caching.Tests.Broadcast;

public class ChannelTests
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubsitute();

    [Fact]
    public void Cast_and_comparations_works_as_expected()
    {
        var actual = new Channel();
        var expected = Channel.Null;
        Assert.Equal(expected, actual);
        Assert.True(actual.IsNull);

        Assert.True(actual == expected);
        Assert.False(actual != expected);
        var channelName = _fixture.Create<string>();

        actual = new Channel(channelName.ToUpper());
        expected = new Channel(channelName.ToLower());
        Assert.Equal(expected, actual);

        channelName = _fixture.Create<string>().ToLower();
        actual = channelName;
        expected = new Channel(channelName.ToUpper());
        Assert.Equal(expected, actual);

        string stringExpected = expected;
        Assert.Equal(stringExpected, channelName);
        Assert.Equal(stringExpected, actual.ToString());

        actual = _fixture.Create<string>().ToLower();
        expected = new Channel(_fixture.Create<string>());
        Assert.NotEqual(expected, actual);
        Assert.True(expected != actual);
        Assert.False(expected == actual);
    }
}
