namespace UiPath.Caching.Tests.Broadcast;
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
    [InlineData(typeof(ReadOnlyMemory<byte>), "readonlymemory:byte")]   // the distributed adapter's value type
    public void GetTopicKey(Type topicType, string expected)
    {
        // Arrange
        var strategy = new DefaultTopicKeyStrategy();

        // Act
        string actual = strategy.GetTopicKey(topicType);
        actual.Should().Be(expected);
    }

    /// <summary>
    /// The strategy runs on every operation of the memory tiers, and a generic type's name is built with a
    /// StringBuilder — so it has to be built once. Measured rather than inferred: a regression here is a
    /// per-operation allocation on every generic cache value, and nothing else would notice.
    /// </summary>
    [Fact]
    public void Repeated_lookups_of_a_generic_type_allocate_nothing()
    {
        var strategy = new DefaultTopicKeyStrategy();
        string first = strategy.GetTopicKey(typeof(ReadOnlyMemory<byte>));

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            strategy.GetTopicKey(typeof(ReadOnlyMemory<byte>));
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.Should().Be(0);
        ((string)strategy.GetTopicKey(typeof(ReadOnlyMemory<byte>))).Should().Be(first);
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
