using StackExchange.Redis;

namespace UiPath.Platform.Caching.Tests;

public class PrefixStrategyTests
{
    public string _prefix = default!;
    public CacheOptions _cacheOptions = default!;

    private PrefixStrategy? _sut = null;

    private PrefixStrategy Sut => _sut ??= new PrefixStrategy(_prefix, _cacheOptions);

    [Theory]
    [InlineData("app", "abc", ' ')]
    [InlineData("  ", "prefix", '$')]
    [InlineData("", "abc", '$')]
    [InlineData("xxx", "", '$')]
    public void Create_WhenCalled_ThrowsException(string appShortName, string prefix, char separator)
    {
        _prefix = prefix;
        _cacheOptions = new CacheOptions {
            Separator = separator,
            AppShortName = appShortName
        };

        var act = () => Sut;

        act.Should().Throw<Exception>();
    }

    [Theory]
    [InlineData("app", "xx", '$', "key", "app$xx$key")]
    [InlineData("XyZ", "PrefiX", 'A', "myKey", "xyzaprefixamykey")]
    public void WorksAsExpected(string appShortName, string prefix, char separator, string key, string expected)
    {
        _prefix = prefix;
        _cacheOptions = new CacheOptions
        {
            Separator = separator,
            AppShortName = appShortName
        };

        var actual = Sut.GetRedisKey(key);
        actual.Should().Be((RedisKey)expected);

        var actualChannel = Sut.GetRedisChannel(key);
        actualChannel.Should().Be((RedisChannel)expected);
    }
}
