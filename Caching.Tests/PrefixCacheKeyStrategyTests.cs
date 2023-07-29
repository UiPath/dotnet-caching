namespace UiPath.Platform.Caching.Tests;

public class PrefixCacheKeyStrategyTests
{

    public string _prefix = default!;
    public char? _separator = default!;

    private PrefixCacheKeyStrategy? _sut = null;

    private PrefixCacheKeyStrategy Sut => _sut ??= new PrefixCacheKeyStrategy(_prefix, _separator);

    [Theory]
    [InlineData("app", ' ')]
    [InlineData("  ", '$')]
    [InlineData("", '$')]
    public void Create_WhenCalled_ThrowsException(string prefix, char? separator)
    {
        _prefix = prefix;
        _separator = separator;

        var act = () => Sut;

        act.Should().Throw<Exception>();
    }

    [Theory]
    [InlineData("app", null, "key", "app:key")]
    [InlineData("xxx", '$', "bla", "xxx$bla")]
    [InlineData("aa", 'B', "ccc", "aabccc")]
    public void WorksAsExpected(string prefix, char? separator, string key, string expected)
    {
        _prefix = prefix;
        _separator = separator;
        CacheKey cacheKey = key;

        var actual = Sut.GetCacheKey<string>(cacheKey);
        actual.Should().Be((CacheKey)expected);
    }
}
