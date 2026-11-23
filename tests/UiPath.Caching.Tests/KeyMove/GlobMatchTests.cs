using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Tests.KeyMove;

public class GlobMatchTests
{
    [Theory]
    [InlineData("app:s:*", "app:s:key", true)]
    [InlineData("app:s:*", "app:s:", true)]
    [InlineData("app:s:*", "app:h:key", false)]
    [InlineData("app:*", "app", false)]
    [InlineData("a?c", "abc", true)]
    [InlineData("a?c", "ac", false)]
    [InlineData("a[bc]d", "acd", true)]
    [InlineData("a[bc]d", "aed", false)]
    [InlineData("a[^bc]d", "aed", true)]
    [InlineData("a[b-d]e", "ace", true)]
    [InlineData("a[b-d]e", "afe", false)]
    [InlineData("a\\*b", "a*b", true)]
    [InlineData("a\\*b", "axb", false)]
    [InlineData("*:s:*", "x:y:s:z", true)]
    [InlineData("**", "anything", true)]
    [InlineData("a*b*c", "aXbYc", true)]
    [InlineData("a*b*c", "aXbY", false)]
    [InlineData("[\\a-z]", "-", true)]
    [InlineData("[\\a-z]", "b", false)]
    [InlineData("[a-]]", "_", true)]
    [InlineData("[z-a]", "m", true)]
    [InlineData("[abc", "a", true)]
    [InlineData("a\\", "a\\", true)]
    [InlineData("??", "\u00e9", true)]
    [InlineData("?", "\u00e9", false)]
    [InlineData("???", "\u20ac", true)]
    [InlineData("????", "\U0001F600", true)]
    public void A_key_matches_a_redis_glob_as_redis_reads_it(string pattern, string text, bool matches) =>
        GlobPatterns.Matches(pattern, text).Should().Be(matches);
}
