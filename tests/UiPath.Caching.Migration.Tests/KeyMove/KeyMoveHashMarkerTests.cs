using UiPath.Caching.KeyMove;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

// The supersession marker on a hash, with the writes of the client itself: a NoOrigin replace that stopped after its first step, N then gone, and a promoter that comes.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class KeyMoveHashMarkerTests(RedisFixture fixture)
{
    private static readonly Dictionary<string, string> Older = new() { ["a"] = "1", ["b"] = "2" };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task A_promoter_never_publishes_the_older_hash_over_a_newer_write_that_was_removed_or_expired_from_N(bool scripts, bool expires)
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, scripts, noOrigin: true);
        using var meter = new MeterCollector(kit.Kit.Cid);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3");
        await kit.SetAsync(kit.L("k"), Older);

        // The writer stops after its first step: N holds the newer hash with the marker, and L still the older one.
        kit.Kit.Schedule.Hook = (step, _) =>
        {
            if (step == SchedulePointStep.SourceDelete)
            {
                kit.Kit.Connected = false;
            }

            return Task.CompletedTask;
        };
        var deadline = expires ? DateTimeOffset.UtcNow.AddMilliseconds(400) : (DateTimeOffset?)null;
        (await kit.Client.SetAsync("k", KeyMoveHashKit.Items(("z", "9")), new HashWriteOptions(HashCacheSetOption.KeyReplace, deadline), Ct)).Outcome.Should().Be(WriteOutcome.Unknown);
        kit.Kit.Schedule.Hook = null;
        kit.Kit.Connected = true;
        kit.Client.Retries.Clear();
        (await kit.Kit.MarkerAsync("k")).Should().NotBeNull("the marker went with the write of N");
        (await kit.Kit.ExistsAsync(kit.L("k"))).Should().BeTrue("the second step did not run");
        if (expires)
        {
            await Task.Delay(800, Ct);
        }
        else
        {
            await kit.Db.KeyDeleteAsync(kit.N("k"));
        }

        (await kit.Kit.ExistsAsync(kit.N("k"))).Should().BeFalse();

        var read = await kit.Client.ReadAsync("k", null, Ct);

        read.Kind.Should().Be(ReadOutcome.Absent);
        (await kit.Kit.ExistsAsync(kit.L("k"))).Should().BeFalse("the helper deleted the older hash");
        (await kit.Kit.ExistsAsync(kit.N("k"))).Should().BeFalse("nothing is published over the newer write");
        (await kit.Kit.MarkerAsync("k")).Should().BeNull();
        meter.Count("cache.keymove.superseded_helped").Should().Be(1);
    }

    [Fact]
    public async Task A_NoOrigin_merge_ensures_the_promotion_and_sets_no_marker()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, noOrigin: true);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3");
        await kit.SetAsync(kit.L("k"), Older);

        (await kit.Client.SetAsync("k", KeyMoveHashKit.Items(("d", "4")), new HashWriteOptions(HashCacheSetOption.HashReplace, null), Ct)).Outcome.Should().Be(WriteOutcome.Committed);

        (await kit.FieldsAsync(kit.N("k"))).Should().BeEquivalentTo(new Dictionary<string, string>(Older) { ["d"] = "4" });
        (await kit.Kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
        (await kit.Kit.MarkerAsync("k")).Should().BeNull("a merge first makes sure L is gone, so it needs no marker");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_marked_replace_at_the_limit_of_the_record_is_refused_on_its_first_attempt_and_sets_nothing(bool scripts)
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, scripts, noOrigin: true);
        using var meter = new MeterCollector(kit.Kit.Cid);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3");
        await kit.SetAsync(kit.L("k"), Older);
        await kit.Kit.LimitMarkersAsync("k", 0);

        (await kit.Client.SetAsync("k", KeyMoveHashKit.Items(("z", "9")), new HashWriteOptions(HashCacheSetOption.KeyReplace, null), Ct)).Outcome.Should().Be(WriteOutcome.NotSent);

        (await kit.Kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
        (await kit.FieldsAsync(kit.L("k"))).Should().BeEquivalentTo(Older);
        meter.Count("cache.keymove.marker_capacity").Should().Be(1);
    }
}
