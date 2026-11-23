using UiPath.Caching.KeyMove;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

// Batch reads and writes preserve per-key ordering on both standalone Redis and cluster deployments. Only the existence probe is batched.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class KeyMoveBatchTests(RedisFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task A_batch_read_serves_hits_promotes_Src_keys_misses_the_rest_and_never_reads_Src_before_a_capture(bool cluster, bool scripts)
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture, scripts, cluster: cluster);
        string[] keys = ["hit", "src1", "src2", "none", "src3"];
        await kit.PrepareAsync(keys);
        await kit.SetStateAsync("S3");
        await kit.Db.StringSetAsync(kit.N("hit"), "n-value");
        foreach (var key in new[] { "src1", "src2", "src3" })
        {
            await kit.Db.StringSetAsync(kit.L(key), "l-" + key);
        }

        var read = await kit.Client.ReadManyAsync(keys, Ct);

        read["hit"].Value!.Name.Should().Be("n-value");
        read["src1"].Value!.Name.Should().Be("l-src1");
        read["src2"].Value!.Name.Should().Be("l-src2");
        read["src3"].Value!.Name.Should().Be("l-src3");
        read["none"].Kind.Should().Be(ReadOutcome.Absent);
        foreach (var key in new[] { "src1", "src2", "src3" })
        {
            (await kit.GetAsync(kit.N(key))).Should().Be("l-" + key);
            (await kit.ExistsAsync(kit.L(key))).Should().BeFalse();
        }

        var steps = kit.Schedule.Steps;
        steps.TakeWhile(s => s != SchedulePointStep.Capture).Should().NotContain(SchedulePointStep.ReadSource);
        steps.Count(s => s == SchedulePointStep.Capture).Should().Be(3);
        steps.Count(s => s == SchedulePointStep.ProbeSource).Should().Be(1);
    }

    [Fact]
    public async Task A_batch_read_carries_the_remaining_life_of_each_hit()
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture);
        await kit.PrepareAsync("a", "b");
        await kit.SetStateAsync("S3");
        await kit.Db.StringSetAsync(kit.N("a"), "va", TimeSpan.FromMinutes(5));
        await kit.Db.StringSetAsync(kit.N("b"), "vb");

        var read = await kit.Client.ReadManyAsync(["a", "b"], Ct);

        read["a"].Expiration!.Value.Should().BeCloseTo(kit.Clock.GetUtcNow().AddMinutes(5), TimeSpan.FromSeconds(10));
        read["b"].Expiration.Should().BeNull();
    }

    [Fact]
    public async Task A_key_that_expires_between_the_MGET_and_the_PTTL_is_a_miss_and_is_not_cached_locally()
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture);
        await kit.PrepareAsync("gone", "kept");
        await kit.SetStateAsync("S3");
        await kit.Db.StringSetAsync(kit.N("gone"), "v-gone");
        await kit.Db.StringSetAsync(kit.N("kept"), "v-kept");
        kit.Schedule.Hook = async (step, _) =>
        {
            if (step == SchedulePointStep.ReadLife)
            {
                await kit.Db.KeyDeleteAsync(kit.N("gone"));
            }
        };

        var read = await kit.Client.ReadManyAsync(["gone", "kept"], Ct);

        read["gone"].Kind.Should().Be(ReadOutcome.Absent);
        read["gone"].Value.Should().BeNull();
        read["kept"].Value!.Name.Should().Be("v-kept");
    }

    [Fact]
    public async Task A_batch_read_of_a_hinted_key_asks_neither_the_probe_nor_Src()
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture);
        await kit.PrepareAsync("a", "b");
        await kit.SetStateAsync("S3");
        await kit.Client.ReadAsync("a", Ct);
        kit.Schedule.Reset();

        var read = await kit.Client.ReadManyAsync(["a", "b"], Ct);

        read["a"].Kind.Should().Be(ReadOutcome.Absent);
        read["b"].Kind.Should().Be(ReadOutcome.Absent);
        kit.Schedule.Steps.Should().NotContain(SchedulePointStep.Capture);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_batch_write_keeps_the_step_order_of_each_key_and_a_key_whose_first_step_failed_is_reported_failed(bool cluster)
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture, cluster: cluster);
        string[] keys = ["a", "b", "c", "d"];
        await kit.PrepareAsync("a", "b", "c");
        await kit.SetStateAsync("S3");
        foreach (var key in keys)
        {
            await kit.Db.StringSetAsync(kit.L(key), "old-" + key);
        }

        // On a cluster "d" has no control record in its slots, so its first step is refused; one server has one record for every key.
        var results = await kit.Client.SetManyAsync([.. keys.Select(k => (k, new Item("new-" + k), (DateTimeOffset?)null))], Ct);

        results.Select(r => r.Outcome).Should().Equal(WriteOutcome.Committed, WriteOutcome.Committed, WriteOutcome.Committed, cluster ? WriteOutcome.NotSent : WriteOutcome.Committed);
        foreach (var key in new[] { "a", "b", "c" })
        {
            (await kit.GetAsync(kit.N(key))).Should().Be("new-" + key);
            (await kit.ExistsAsync(kit.L(key))).Should().BeFalse();
        }

        (await kit.ExistsAsync(kit.N("d"))).Should().Be(!cluster);
        (await kit.ExistsAsync(kit.L("d"))).Should().Be(cluster);
        var removed = await kit.Client.RemoveManyAsync(["a", "b"], Ct);
        removed.Select(r => r.Outcome).Should().OnlyContain(o => o == WriteOutcome.Committed);
        (await kit.ExistsAsync(kit.N("a"))).Should().BeFalse();
    }

    [Fact]
    public async Task A_batch_set_reports_a_closed_cache_as_not_sent_per_key()
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture, noOrigin: true);
        await kit.PrepareAsync("a");
        await kit.SetStateAsync("S2", nPop: 0);

        var results = await kit.Client.SetManyAsync([("a", new Item("x"), null)], Ct);

        results.Single().Outcome.Should().Be(WriteOutcome.NotSent);
    }
}
