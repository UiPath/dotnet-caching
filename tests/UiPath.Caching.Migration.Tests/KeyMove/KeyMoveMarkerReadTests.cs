using UiPath.Caching.KeyMove;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

// FU3: a reader, a batch read and a create that meet a live marker help the writer's cleanup, and publish nothing.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class KeyMoveMarkerReadTests(RedisFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_reader_on_a_live_marker_deletes_L_removes_the_marker_and_serves_N_without_publishing(bool scripts)
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture, scripts, noOrigin: true);
        using var meter = new MeterCollector(kit.Cid);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        await kit.LeaveSupersededAsync("k", "v0");

        var read = await kit.Client.ReadAsync("k", Ct);

        read.Kind.Should().Be(ReadOutcome.Absent);
        (await kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
        (await kit.MarkerAsync("k")).Should().BeNull();
        (await kit.ExistsAsync(kit.N("k"))).Should().BeFalse("nothing is published");
        if (scripts)
        {
            (await kit.FenceAsync("k")).Should().BeNull("a refused capture takes no token");
        }

        kit.Schedule.Steps.Should().NotContain(SchedulePointStep.Publish).And.NotContain(SchedulePointStep.CompareDelete).And.NotContain(SchedulePointStep.ReadSource);
        meter.Count("cache.keymove.superseded_helped").Should().Be(1);
        kit.Client.Hints.Has("k", kit.Client.PlanFor(write: false).Identity, MoveSide.Legacy).Should().BeTrue("L has just been deleted, so the miss is a fact");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_marker_of_an_older_identity_is_ignored_and_the_copy_is_promoted_as_before(bool scripts)
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture, scripts, noOrigin: true);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        await kit.Db.StringSetAsync(kit.L("k"), "v0");
        await kit.Db.HashSetAsync(kit.Control("k"), kit.Field("k"), "id0|n0");

        var read = await kit.Client.ReadAsync("k", Ct);

        read.Kind.Should().Be(ReadOutcome.Found);
        (await kit.GetAsync(kit.N("k"))).Should().Be("v0");
        (await kit.MarkerAsync("k")).Should().Be("id0|n0", "only SMGC removes an obsolete marker");
    }

    [Fact]
    public async Task A_refresh_while_the_keys_move_is_refused_so_it_never_meets_a_marker()
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture, noOrigin: true);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        await kit.LeaveSupersededAsync("k", "v0");

        var refresh = async () => await kit.Client.RefreshAsync("k", default, Ct);

        await refresh.Should().ThrowAsync<NotSupportedException>();
        (await kit.GetAsync(kit.L("k"))).Should().Be("v0");
        (await kit.MarkerAsync("k")).Should().NotBeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_batch_read_helps_each_key_that_has_a_marker_and_promotes_the_others(bool scripts)
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture, scripts, noOrigin: true);
        await kit.PrepareAsync("a", "b", "c");
        await kit.SetStateAsync("S3");
        await kit.LeaveSupersededAsync("a", "va");
        await kit.LeaveSupersededAsync("b", "vb");
        await kit.Db.StringSetAsync(kit.L("c"), "vc");

        var reads = await kit.Client.ReadManyAsync(["a", "b", "c"], Ct);

        reads["a"].Kind.Should().Be(ReadOutcome.Absent);
        reads["b"].Kind.Should().Be(ReadOutcome.Absent);
        reads["c"].Value!.Name.Should().Be("vc");
        foreach (var key in new[] { "a", "b" })
        {
            (await kit.ExistsAsync(kit.L(key))).Should().BeFalse();
            (await kit.MarkerAsync(key)).Should().BeNull();
            (await kit.ExistsAsync(kit.N(key))).Should().BeFalse();
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_create_that_meets_a_marker_at_its_capture_helps_and_runs_again_from_the_union_check(bool scripts)
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture, scripts, noOrigin: true);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        var planted = false;
        kit.Schedule.Hook = async (step, _) =>
        {
            if (step == SchedulePointStep.Capture && !planted)
            {
                // After the union check found nothing, and before the create's own capture: a write's first step lands.
                planted = true;
                await kit.LeaveSupersededAsync("k", "v0");
            }
        };

        var result = await kit.Client.TryAddAsync("k", new Item("claim"), TimeSpan.FromMinutes(1), Ct);

        result.Kind.Should().Be(TryAddKind.Created);
        (await kit.GetAsync(kit.N("k"))).Should().Be("claim");
        (await kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
        (await kit.MarkerAsync("k")).Should().BeNull();
        kit.Schedule.Steps.Count(s => s == SchedulePointStep.Capture).Should().Be(2);
    }

    [Fact]
    public async Task A_create_that_meets_a_marker_again_after_three_runs_is_unavailable_and_counted()
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture, noOrigin: true);
        using var meter = new MeterCollector(kit.Cid);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        kit.Schedule.Hook = async (step, _) =>
        {
            if (step == SchedulePointStep.Capture)
            {
                await kit.LeaveSupersededAsync("k", "v0");
            }
        };

        var create = async () => await kit.Client.TryAddAsync("k", new Item("claim"), null, Ct);

        await create.Should().ThrowAsync<CacheUnavailableException>();
        kit.Schedule.Steps.Count(s => s == SchedulePointStep.Capture).Should().Be(4, "the first run and three helped reruns");
        meter.Count("cache.keymove.pending_write").Should().Be(1);
        (await kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
    }
}
