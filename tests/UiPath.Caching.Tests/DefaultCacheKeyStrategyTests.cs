namespace UiPath.Caching.Tests;

public class DefaultCacheKeyStrategyTests
{
    [Fact]
    public void Writes_the_key_unchanged_into_a_span()
    {
        Span<char> buffer = stackalloc char[16];

        new DefaultCacheKeyStrategy().TryGetCacheKey<string>("user:42", buffer, out var written).Should().BeTrue();

        buffer[..written].ToString().Should().Be("user:42");
    }

    [Fact]
    public void A_span_too_small_for_the_key_is_refused()
    {
        Span<char> buffer = stackalloc char[3];

        new DefaultCacheKeyStrategy().TryGetCacheKey<string>("user:42", buffer, out var written).Should().BeFalse();

        written.Should().Be(0);
    }
}
