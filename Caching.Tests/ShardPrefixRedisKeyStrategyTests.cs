using StackExchange.Redis;

namespace UiPath.Platform.Caching.Tests;

public class ShardPrefixRedisKeyStrategyTests
{
    public string _prefix = default!;
    public char _separator = default!;

    private ShardPrefixRedisKeyStrategy? _sut = null;

    private ShardPrefixRedisKeyStrategy Sut => _sut ??= new ShardPrefixRedisKeyStrategy(_prefix, _separator);


    [Theory]
    [InlineData("xxx", '$', "bla", "xxx${bla}")]
    [InlineData("aa", 'B', "ccc", "aab{ccc}")]
    public void WorksAsExpected(string prefix, char separator, string key, string expected)
    {
        _prefix = prefix;
        _separator = separator;
        CacheKey cacheKey = key;

        var actual = Sut.GetRedisKey(cacheKey);
        actual.Should().Be((RedisKey)expected);
    }
}
