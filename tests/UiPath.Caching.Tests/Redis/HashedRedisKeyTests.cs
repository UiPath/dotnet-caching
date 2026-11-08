using StackExchange.Redis;
using UiPath.Caching.Redis;
using Xunit;

namespace UiPath.Caching.Tests.Redis;

public sealed class HashedRedisKeyTests
{
    [Fact]
    public void Keys_Redis_treats_as_one_are_one_in_the_run_table()
    {
        var prefixed = new HashedRedisKey(((RedisKey)"folder:42").Prepend("app:perm:"));
        var whole = new HashedRedisKey((RedisKey)"app:perm:folder:42");
        var bytes = new HashedRedisKey((RedisKey)System.Text.Encoding.UTF8.GetBytes("app:perm:folder:42"));

        prefixed.Should().Be(whole);
        bytes.Should().Be(whole);
        prefixed.GetHashCode().Should().Be(whole.GetHashCode());
        bytes.GetHashCode().Should().Be(whole.GetHashCode());
        new HashedRedisKey((RedisKey)"app:perm:folder:43").Should().NotBe(whole);
    }

    [Fact]
    public void Keys_whose_hashes_collide_stay_apart()
    {
        // A table that matched on the hash alone would hand one key's run, and its value, to the other.
        var (first, second) = Colliding();

        first.GetHashCode().Should().Be(second.GetHashCode());
        first.Should().NotBe(second);
    }

    private static (HashedRedisKey First, HashedRedisKey Second) Colliding()
    {
        var seen = new Dictionary<int, RedisKey>();
        for (var i = 0; i < 2_000_000; i++)
        {
            RedisKey key = $"k:{i}";
            var hash = key.GetHashCode();
            if (seen.TryGetValue(hash, out var other))
            {
                return (new HashedRedisKey(other), new HashedRedisKey(key));
            }

            seen[hash] = key;
        }

        throw new InvalidOperationException("No hash collision found.");
    }
}
