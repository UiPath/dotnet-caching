using System.Reflection;
using StackExchange.Redis;

namespace UiPath.Caching.Tests;

public class PrefixRedisKeyStrategyTests
{

    public string _prefix = default!;
    public char _separator = default!;

    private PrefixRedisKeyStrategy? _sut = null;

    private PrefixRedisKeyStrategy Sut => _sut ??= new PrefixRedisKeyStrategy(_prefix, _separator);

    [Theory]
    [InlineData("app", ' ')]
    [InlineData("  ", '$')]
    [InlineData("", '$')]
    [InlineData("app", '\uD83D')]
    public void Create_WhenCalled_ThrowsException(string prefix, char separator)
    {
        _prefix = prefix;
        _separator = separator;

        var act = () => Sut;

        act.Should().Throw<Exception>();
    }

    [Theory]
    [InlineData("xxx", '$', "bla", "xxx$bla")]
    [InlineData("aa", 'B', "ccc", "aabccc")]
    public void WorksAsExpected(string prefix, char separator, string key, string expected)
    {
        _prefix = prefix;
        _separator = separator;
        CacheKey cacheKey = key;

        var actual = Sut.GetRedisKey(cacheKey);
        actual.Should().Be((RedisKey)expected);
    }

    [Theory]
    [InlineData("{tenant1}app", ':', "user:42", "{tenant1}")]
    [InlineData("app{ten", ':', "ant}user:42", "{ten:ant}")]
    [InlineData("app", ':', "{user}:42", "{user}")]
    [InlineData("app", ':', "user:42", "app:user:42")]
    public void The_client_hashes_the_prefixed_key_to_the_slot_of_the_concatenated_key(string prefix, char separator, string key, string hashed)
    {
        _prefix = prefix;
        _separator = separator;
        using var multiplexer = ClusterModeMultiplexer();
        var segmented = Sut.GetRedisKey(key);
        var concatenated = (RedisKey)$"{prefix}{separator}{key}";

        segmented.Should().Be(concatenated);
        var slot = multiplexer.GetHashSlot(segmented);
        slot.Should().Be(multiplexer.GetHashSlot(concatenated));
        slot.Should().Be(multiplexer.GetHashSlot((RedisKey)hashed)).And.BeInRange(0, 16383);
    }

    /// <summary>An unconnected multiplexer reports no slot until it learns it talks to a cluster; the slot function itself needs no server.</summary>
    private static ConnectionMultiplexer ClusterModeMultiplexer()
    {
        var multiplexer = ConnectionMultiplexer.Connect("127.0.0.1:1,abortConnect=false,connectTimeout=100,connectRetry=0");
        var strategy = typeof(ConnectionMultiplexer).GetProperty("ServerSelectionStrategy", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(multiplexer)!;
        strategy.GetType().GetProperty("ServerType")!.SetValue(strategy, ServerType.Cluster);
        return multiplexer;
    }
}
