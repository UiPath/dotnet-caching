namespace UiPath.Caching.Tests;

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

    [Fact]
    public void Preserves_sensitive_casing_through_prefixing()
    {
        var strategy = new PrefixCacheKeyStrategy("MyApp");
        var key = new CacheKey("AbC", CacheKeyCasing.Sensitive);

        var result = strategy.GetCacheKey<string>(key);

        result.Name.Should().Be("myapp:AbC");
        result.Casing.Should().Be(CacheKeyCasing.Sensitive);
    }

    [Fact]
    public void Insensitive_keys_behave_exactly_as_before()
    {
        var strategy = new PrefixCacheKeyStrategy("MyApp");
        var result = strategy.GetCacheKey<string>(new CacheKey("AbC"));
        result.Name.Should().Be("myapp:abc");
    }
}
