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
}
