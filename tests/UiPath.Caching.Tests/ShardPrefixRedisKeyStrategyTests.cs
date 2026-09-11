using StackExchange.Redis;

namespace UiPath.Caching.Tests;

public class ShardPrefixRedisKeyStrategyTests
{
    public string _prefix = default!;
    public char _separator = default!;

    private ShardPrefixRedisKeyStrategy? _sut = null;

    private ShardPrefixRedisKeyStrategy Sut => _sut ??= new ShardPrefixRedisKeyStrategy(_prefix, _separator);


    [Theory]
    [InlineData("xxx", '$', "bla", "xxx${bla}")]
    [InlineData("aa", 'B', "ccc", "aab{ccc}")]
    [InlineData("app:s", ':', "{org1}:groups_1", "app:s:{org1}:groups_1")]
    [InlineData("app:s", ':', "authz_{org1}_groups_1", "app:s:authz_{org1}_groups_1")]
    public void WorksAsExpected(string prefix, char separator, string key, string expected)
    {
        _prefix = prefix;
        _separator = separator;
        CacheKey cacheKey = key;

        var actual = Sut.GetRedisKey(cacheKey);
        actual.Should().Be((RedisKey)expected);
    }

    [Theory]
    [InlineData("bla{}bla")]
    [InlineData("bla{bla")]
    [InlineData("bla}bla")]
    [InlineData("")]
    public void Refuses_a_key_that_cannot_carry_a_hash_tag(string key)
    {
        _prefix = "app:s";
        _separator = ':';
        CacheKey cacheKey = key;

        var act = () => Sut.GetRedisKey(cacheKey);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{nameof(ShardPrefixRedisKeyStrategy)}*slot affinity*");
    }
}
