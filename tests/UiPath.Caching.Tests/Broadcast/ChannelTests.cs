namespace UiPath.Caching.Tests.Broadcast;

public class ChannelTests
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubstitute();

    [Fact]
    public void Cast_and_comparations_works_as_expected()
    {
        var actual = new TopicKey();
        var expected = TopicKey.Null;
        Assert.Equal(expected, actual);
        Assert.True(actual.IsNull);

        Assert.True(actual == expected);
        Assert.False(actual != expected);
        var topicName = _fixture.Create<string>();

        actual = new TopicKey(topicName.ToUpper());
        expected = new TopicKey(topicName.ToLower());
        Assert.Equal(expected, actual);

        topicName = _fixture.Create<string>().ToLower();
        actual = topicName;
        expected = new TopicKey(topicName.ToUpper());
        Assert.Equal(expected, actual);

        string stringExpected = expected;
        Assert.Equal(stringExpected, topicName);
        Assert.Equal(stringExpected, actual.ToString());

        actual = _fixture.Create<string>().ToLower();
        expected = new TopicKey(_fixture.Create<string>());
        Assert.NotEqual(expected, actual);
        Assert.True(expected != actual);
        Assert.False(expected == actual);
    }
}
