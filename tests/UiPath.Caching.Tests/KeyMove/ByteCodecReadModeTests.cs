using StackExchange.Redis;
using UiPath.Caching.Redis;
using UiPath.Caching.Serialization;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests.KeyMove;

// A decode through the byte seam is not a read of a key: it queues no rewrite and, when quiet, logs and counts nothing.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class ByteCodecReadModeTests(RedisContainerFixture fixture)
{
    private static readonly string RunTag = Guid.NewGuid().ToString("N")[..8];
    private static readonly byte[] OtherX = [0x12, (byte)'x'];
    private static readonly byte[] FastX = [0x10, (byte)'x'];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_decode_of_a_value_in_another_format_queues_no_rewrite_and_leaves_the_key_while_a_read_does_rewrite_it()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var harness = MigrateOnReadHarness.Build(fixture.ConnectionString, FormatNegotiationMode.Force, $"rm{Guid.NewGuid():N}"[..10]);
        var cache = harness.Cache();
        var policy = harness.Resolve(MigrateOnReadHarness.Migrating);
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var (key, redisKey) = await SeedAsync(cache, raw, OtherX);

        var decoded = ((IByteCodecCache)cache).Decode<string>(OtherX, policy);
        await harness.Migrator.WhenIdleAsync(Ct);

        decoded.Should().Be((SnapshotOutcome.Found, "x"));
        ((byte[]?)await raw.GetDatabase().StringGetAsync(redisKey)).Should().Equal(OtherX);
        harness.Telemetry.Metrics.Should().NotContain(m => m.Name == Metrics.SerializerRewrite, "nothing was queued for a key the decode never read");

        // The control: the same value read from the key is rewritten to the policy's format.
        (await cache.GetAsync<string>(key, policy, Ct)).Should().Be("x");
        await harness.Migrator.WhenIdleAsync(Ct);
        ((byte[]?)await raw.GetDatabase().StringGetAsync(redisKey)).Should().Equal(FastX);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void A_quiet_decode_of_a_value_the_read_list_leaves_out_is_not_counted_as_a_read_miss(bool quiet, bool counted)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var harness = MigrateOnReadHarness.Build(fixture.ConnectionString, FormatNegotiationMode.Force, $"rq{Guid.NewGuid():N}"[..10]);
        var policy = harness.Resolve(MigrateOnReadHarness.Narrow);

        var (outcome, _) = ((IByteCodecCache)harness.Cache()).Decode<string>(OtherX, policy, quiet);

        outcome.Should().Be(SnapshotOutcome.Unreadable);
        harness.Telemetry.Metrics.Any(m => m.Name == ValueReader.UnreadableMetric).Should().Be(counted);
    }

    private static async Task<(string Key, RedisKey RedisKey)> SeedAsync(ICache cache, ConnectionMultiplexer raw, byte[] bytes)
    {
        var key = $"rm:{RunTag}:{Guid.NewGuid():N}";
        await cache.SetAsync(key, "seed", TimeSpan.FromMinutes(5), Ct);
        var redisKey = raw.GetServer(raw.GetEndPoints()[0]).Keys(pattern: $"*{key}*").Single();
        await raw.GetDatabase().StringSetAsync(redisKey, bytes, TimeSpan.FromMinutes(5));
        return (key, redisKey);
    }
}
