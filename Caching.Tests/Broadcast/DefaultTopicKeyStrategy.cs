namespace UiPath.Platform.Caching.Tests.Broadcast;
public class DefaultTopicKeyStrategyTests
{

    [Theory]
    [InlineData(typeof(string), "string")]
    [InlineData(typeof(int), "int")]
    [InlineData(typeof(List<string>), "list:string")]
    [InlineData(typeof(List<int>), "list:int")]
    [InlineData(typeof(Dictionary<string, int>), "dictionary:string,int")]
    [InlineData(typeof(Dictionary<int, string>), "dictionary:int,string")]
    [InlineData(typeof(Dictionary<string, List<int>>), "dictionary:string,list:int")]
    [InlineData(typeof(Dictionary<List<int>, string>), "dictionary:list:int,string")]
    [InlineData(typeof(HashSet<string>), "hashset:string")]
    [InlineData(typeof(string[]), "string[]")]
    [InlineData(typeof(IEnumerable<int>), "ienumerable:int")]
    [InlineData(typeof(IEnumerable<string>), "ienumerable:string")]
    public void GetTopicKey(Type topicType, string expected)
    {
        // Arrange
        var strategy = new DefaultTopicKeyStrategy();

        // Act
        string actual = strategy.GetTopicKey(topicType);
        actual.Should().Be(expected);
    }

    [Theory]
    [InlineData(typeof(List<string>), "list#string")]
    [InlineData(typeof(List<int>), "list#int")]
    [InlineData(typeof(Dictionary<string, List<int>>), "dictionary#string,list#int")]
    [InlineData(typeof(string[]), "string[]")]
    public void GetTopicKeyWithSeparator(Type topicType, string expected)
    {
        // Arrange
        var strategy = new DefaultTopicKeyStrategy('#');

        // Act
        string actual = strategy.GetTopicKey(topicType);
        actual.Should().Be(expected);
    }
}
