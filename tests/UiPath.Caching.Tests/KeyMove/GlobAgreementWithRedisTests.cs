using StackExchange.Redis;
using UiPath.Caching.KeyMove;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests.KeyMove;

[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class GlobAgreementWithRedisTests(RedisContainerFixture fixture)
{
    private static readonly string[] Patterns =
    [
        "*", "**", "***", "a*", "*b", "a*c", "a**c", "*a*b*", "*?*", "a*b*c*d", "*abc", "?*", "*?",
        "?", "??", "???", "a?c", "?b?", "a??",
        "[abc]", "[a-c]", "[^abc]", "[^a-c]", "[c-a]", "[z-a]", "[a-]", "[a-]]", "[a-]]]", "[]]", "[]a]", "[^]]", "[-a]", "[a-a]", "[^-]",
        "[\\a-z]", "[\\a-\\z]", "[a\\-z]", "[\\]]", "[\\]a]", "[\\^a]", "[a\\", "[\\",
        "[abc", "[", "[^", "[a-", "[a-z", "a[bc", "a[b", "a[", "a[^", "[]",
        "\\a", "\\*", "\\?", "\\[", "\\\\", "a\\*c", "a\\?c", "a\\", "\\", "*\\", "*\\*", "a\\[b]",
        "é", "?", "??", "???", "????", "é?", "?é", "[é]", "[éa]", "[^é]", "[à-ÿ]", "[€]", "[€-₭]", "*€*", "€??", "€?", "[é€]",
        "\U0001F600", "?\U0001F600", "\U0001F600?", "????", "[\U0001F600]", "[^\U0001F600]", "*\U0001F600", "é*\U0001F600", "café*", "caf?", "caf??", "caf[é]",
    ];

    private static readonly string[] Keys =
    [
        string.Empty, "a", "b", "c", "d", "z", "-", "]", "^", "[", "\\", "*", "?", "_", "A", "ab", "ac", "abc", "abcd", "aXc", "abbc", "a*c", "a?c", "a\\c", "a]", "a[b]", "a[b", "aab",
        "bb", "cc", "abcabc", "xaxbx", "a-", "a-]", "a\\", "a\\b",
        "é", "éa", "aé", "éé", "à", "ÿ", "€", "€€", "a€", "€é", "₭", "\U0001F600", "\U0001F600\U0001F600", "a\U0001F600", "\U0001F600a",
        "caf", "cafe", "café", "café\U0001F600", "é\U0001F600", "éx\U0001F600", "abé", "ébc",
    ];

    [Fact]
    public async Task Matches_agrees_with_the_server_on_every_pattern_and_key()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var prefix = $"globtest:{Guid.NewGuid():N}:";
        await using var mux = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var db = mux.GetDatabase();
        var server = mux.GetServer(mux.GetEndPoints()[0]);
        var keys = Keys.Distinct().ToArray();
        try
        {
            await Task.WhenAll(keys.Select(k => db.StringSetAsync(prefix + k, "v", TimeSpan.FromMinutes(5))));
            var disagreements = new List<string>();
            foreach (var pattern in Patterns.Distinct())
            {
                var matched = server.Keys(db.Database, prefix + pattern, 10000).Select(k => (string)k!).ToHashSet(StringComparer.Ordinal);
                foreach (var key in keys)
                {
                    var expected = matched.Contains(prefix + key);
                    if (GlobPatterns.Matches(prefix + pattern, prefix + key) != expected)
                    {
                        disagreements.Add($"pattern '{pattern}' key '{key}': Redis {expected}");
                    }
                }
            }

            disagreements.Should().BeEmpty();
        }
        finally
        {
            await db.KeyDeleteAsync(keys.Select(k => (RedisKey)(prefix + k)).ToArray());
        }
    }

    [Fact]
    public void The_pattern_and_key_tables_cover_the_branches()
    {
        (Patterns.Distinct().Count() * Keys.Distinct().Count()).Should().BeGreaterThan(5000);
    }
}
