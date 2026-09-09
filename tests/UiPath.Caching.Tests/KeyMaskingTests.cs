using StackExchange.Redis;
using UiPath.Caching.Redis;

namespace UiPath.Caching.Tests;

public class KeyMaskingTests
{
    private static readonly KeyMasking Sessions = new(enabled: true, ["session:", "d:"]);

    [Fact]
    public void Off_renders_every_key_in_full()
    {
        KeyMasking.Off.Render("session:cosmin", layerPrefix: null).Should().Be("session:cosmin");
        KeyMasking.Off.Render("myapp:s:session:cosmin", "myapp:s:").Should().Be("myapp:s:session:cosmin");
    }

    [Fact]
    public void A_key_under_a_listed_prefix_keeps_the_prefix_and_reveals_three_characters()
    {
        Sessions.Render("session:cosmin-abc", layerPrefix: null).Should().Be("session:cos****");
        Sessions.Render("SESSION:cosmin-abc", layerPrefix: null).Should().Be("SESSION:cos****", "prefixes compare case-insensitively");
        Sessions.Render("myapp:s:session:cosmin-abc", "myapp:s:").Should().Be("myapp:s:session:cos****", "the layer's own prefix is kept and the cache key behind it is judged");
    }

    [Fact]
    public void A_key_under_no_listed_prefix_is_logged_in_full()
    {
        Sessions.Render("order:cosmin", layerPrefix: null).Should().Be("order:cosmin");
        Sessions.Render("myapp:s:order:cosmin", "myapp:s:").Should().Be("myapp:s:order:cosmin");
    }

    [Theory]
    [InlineData("42")]
    [InlineData("-7")]
    [InlineData("0a1b2c3d-0000-4000-8000-000000000001")]
    [InlineData("{0a1b2c3d-0000-4000-8000-000000000001}")]
    public void A_number_or_guid_is_an_identifier_and_stays(string key)
    {
        new KeyMasking(enabled: true, [""]).Render(key, layerPrefix: null).Should().Be(key);
        new KeyMasking(enabled: true, [""]).Render("myapp:s:" + key, "myapp:s:").Should().Be("myapp:s:" + key);
    }

    [Fact]
    public void A_guid_behind_a_listed_prefix_is_a_string_and_is_masked()
    {
        Sessions.Render("d:0a1b2c3d-0000-4000-8000-000000000001", layerPrefix: null).Should().Be("d:0a1****");
    }

    [Fact]
    public void An_empty_listed_prefix_matches_every_key()
    {
        new KeyMasking(enabled: true, [""]).Render("cosmin-abc", layerPrefix: null).Should().Be("cos****");
    }

    [Theory]
    [InlineData("", "****")]
    [InlineData("ab", "****")]
    [InlineData("abc", "****")]
    [InlineData("abcd", "abc****")]
    public void MaskValue_reveals_at_most_three_characters(string value, string expected)
    {
        KeyMasking.MaskValue(value).Should().Be(expected);
    }

    [Fact]
    public void PrefixOf_learns_what_the_strategies_put_in_front_of_a_key()
    {
        KeyMasking.PrefixOf(new PrefixRedisKeyStrategy("myapp:s", ':')).Should().Be("myapp:s:");
        KeyMasking.PrefixOf(new PrefixCacheKeyStrategy("d", ':')).Should().Be("d:");
        KeyMasking.PrefixOf(new DefaultCacheKeyStrategy()).Should().BeNull("nothing is composed in front of the key");
        KeyMasking.PrefixOf(new ReversingRedisKeyStrategy()).Should().BeNull("the key is not the tail of what this strategy composes");
    }

    [Fact]
    public void Join_renders_each_key_the_same_way()
    {
        CacheKey[] keys = ["d:alpha", "order:beta"];

        LoggedKey.Join(keys, KeyMasking.Off).Should().Be("d:alpha,order:beta");
        LoggedKey.Join(keys, Sessions).Should().Be("d:alp****,order:beta");
    }

    private sealed class ReversingRedisKeyStrategy : IRedisKeyStrategy
    {
        public RedisKey GetRedisKey(CacheKey key) => new string(key.Name.Reverse().ToArray());
    }
}
