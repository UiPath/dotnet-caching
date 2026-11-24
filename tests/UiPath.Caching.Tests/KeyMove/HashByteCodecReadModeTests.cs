using StackExchange.Redis;
using UiPath.Caching.Redis;
using UiPath.Caching.Serialization;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests.KeyMove;

// A decode of hash fields through the byte seam is not a read of a key: it queues no rewrite and, when quiet, logs and counts nothing.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class HashByteCodecReadModeTests(RedisContainerFixture fixture)
{
    private static readonly string RunTag = Guid.NewGuid().ToString("N")[..8];
    private static readonly byte[] OtherX = [0x12, (byte)'x'];
    private static readonly byte[] FastX = [0x10, (byte)'x'];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_decode_of_a_field_in_another_format_queues_no_rewrite_and_leaves_the_hash_while_a_read_does_rewrite_it()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var harness = MigrateOnReadHarness.Build(fixture.ConnectionString, FormatNegotiationMode.Force, $"hm{Guid.NewGuid():N}"[..10]);
        var cache = harness.Hashes();
        var policy = harness.Resolve(MigrateOnReadHarness.Migrating);
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var (key, redisKey) = await SeedAsync(cache, raw, OtherX);

        var decoded = ((IByteHashCodecCache)cache).DecodeFields<string>([new("a", OtherX)], policy);
        await harness.Migrator.WhenIdleAsync(Ct);

        decoded.Outcome.Should().Be(SnapshotOutcome.Found);
        decoded.Values.Should().BeEquivalentTo(new Dictionary<string, string?> { ["a"] = "x" });
        ((byte[]?)await raw.GetDatabase().HashGetAsync(redisKey, "a")).Should().Equal(OtherX);
        harness.Telemetry.Metrics.Should().NotContain(m => m.Name == Metrics.SerializerRewrite, "nothing was queued for a hash the decode never read");

        // The control: the same field read from the key is rewritten to the policy's format.
        (await cache.GetAsync<string>(key, policy, Ct)).Should().Contain("a", "x");
        await harness.Migrator.WhenIdleAsync(Ct);
        ((byte[]?)await raw.GetDatabase().HashGetAsync(redisKey, "a")).Should().Equal(FastX);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void A_quiet_decode_of_a_field_the_read_list_leaves_out_is_not_counted_as_a_read_miss(bool quiet, bool counted)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var harness = MigrateOnReadHarness.Build(fixture.ConnectionString, FormatNegotiationMode.Force, $"hq{Guid.NewGuid():N}"[..10]);
        var policy = harness.Resolve(MigrateOnReadHarness.Narrow);

        var decoded = ((IByteHashCodecCache)harness.Hashes()).DecodeFields<string>([new("a", OtherX)], policy, quiet);

        decoded.Outcome.Should().Be(SnapshotOutcome.Unreadable);
        harness.Telemetry.Metrics.Any(m => m.Name == ValueReader.UnreadableMetric).Should().Be(counted);
    }

    [Fact]
    public void A_quiet_decode_counts_no_serializer_read_and_a_loud_one_counts_one_per_field()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var harness = MigrateOnReadHarness.Build(fixture.ConnectionString, FormatNegotiationMode.Force, $"hs{Guid.NewGuid():N}"[..10]);
        var codec = (IByteHashCodecCache)harness.Hashes();
        var policy = harness.Resolve(MigrateOnReadHarness.Migrating);
        var fields = new KeyValuePair<string, byte[]>[] { new("a", FastX), new("b", FastX) };

        codec.DecodeFields<string>(fields, policy, quiet: true);
        harness.Telemetry.Metrics.Where(m => m.Name == Metrics.SerializerRead).Should().BeEmpty();

        codec.DecodeFields<string>(fields, policy);
        harness.Telemetry.Metrics.Where(m => m.Name == Metrics.SerializerRead).Sum(m => m.Value).Should().Be(2);
    }

    private static async Task<(string Key, RedisKey RedisKey)> SeedAsync(IHashCache cache, ConnectionMultiplexer raw, byte[] field)
    {
        var key = $"hm:{RunTag}:{Guid.NewGuid():N}";
        await cache.SetAsync(key, new Dictionary<string, string?> { ["seed"] = "seed" }, new HashCacheEntryOptions(TimeToLive: TimeSpan.FromMinutes(5)), policy: null, Ct);
        var redisKey = raw.GetServer(raw.GetEndPoints()[0]).Keys(pattern: $"*{key}*").Single();
        var db = raw.GetDatabase();
        await db.HashSetAsync(redisKey, "a", field);
        await db.HashDeleteAsync(redisKey, "seed");
        return (key, redisKey);
    }
}
