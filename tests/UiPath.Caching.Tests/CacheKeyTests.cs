namespace UiPath.Caching.Tests;

public class CacheKeyTests
{
    [Theory]
    [InlineData(" AbC ", CacheKeyCasing.Insensitive)]
    [InlineData(" AbC ", CacheKeyCasing.Sensitive)]
    [InlineData("abc", CacheKeyCasing.Insensitive)]
    [InlineData("ÄÖ Straße Σ", CacheKeyCasing.Insensitive)]
    [InlineData("ÄÖ Straße Σ", CacheKeyCasing.Sensitive)]
    [InlineData("", CacheKeyCasing.Insensitive)]
    [InlineData("   ", CacheKeyCasing.Sensitive)]
    public void A_span_builds_the_key_the_string_builds(string name, CacheKeyCasing casing)
    {
        var fromSpan = new CacheKey(name.AsSpan(), casing);
        var fromString = new CacheKey(name, casing);

        fromSpan.Name.Should().Be(fromString.Name);
        fromSpan.Casing.Should().Be(casing);
        fromSpan.Should().Be(fromString);
    }

    [Fact]
    public void A_span_longer_than_the_stack_buffer_builds_the_key_the_string_builds()
    {
        var name = string.Concat(Enumerable.Repeat(" AbC-Ü", 200));

        new CacheKey(name.AsSpan()).Name.Should().Be(new CacheKey(name).Name);
    }

    [Fact]
    public void A_span_without_a_casing_uses_the_default()
    {
        var key = new CacheKey("AbC".AsSpan());

        key.Casing.Should().Be(CacheKey.DefaultCasing);
        key.Name.Should().Be(new CacheKey("AbC").Name);
    }

    [Fact]
    public void A_span_with_an_unknown_casing_is_rejected()
    {
        var act = () => new CacheKey("abc".AsSpan(), (CacheKeyCasing)7);

        act.Should().Throw<ArgumentOutOfRangeException>().And.ParamName.Should().Be("casing");
    }

    [Fact]
    public void A_writable_span_builds_the_key_too()
    {
        Span<char> buffer = stackalloc char[8];
        "User:42".AsSpan().CopyTo(buffer);

        new CacheKey(buffer[..7]).Should().Be(new CacheKey("User:42"));
    }
}
