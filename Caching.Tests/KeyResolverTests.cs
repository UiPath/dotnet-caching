namespace UiPath.Platform.Caching.Tests;
public class KeyResolverTests
{
    [Theory]
    [InlineData("keY", "preFix", "prefix:key")]
    [InlineData("bau", null, "bau")]
    [InlineData("bau", "  ", "bau")]
    public void Works_as_expected(string key, string? prefix, string expected)
    {
        var sut = new KeyResolver(new OptionsWrapper<CacheOptions>(new CacheOptions
        {
            Separator = ':'
        }));
        var actual = sut.GetKey(key, prefix);
        actual.Should().BeEquivalentTo(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Works_as_expected_invalid_key(string key)
    {
        var sut = new KeyResolver(new OptionsWrapper<CacheOptions>(new CacheOptions
        {
            Separator = ':'
        }));
        var act = () => sut.GetKey(key);
        act.Should().Throw<ArgumentNullException>();
    }
}
