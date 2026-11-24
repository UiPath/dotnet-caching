using UiPath.Caching.KeyMove;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

// FU3: the forward NoOrigin write sets a supersession marker with its first step, deletes L, and has the marker removed later and without a guarantee.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class KeyMoveMarkerWriteTests(RedisFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("S3")]
    [InlineData("S4")]
    public async Task The_forward_NoOrigin_write_in_S3_and_S4_is_a_marked_set_and_a_delete_of_L(string state)
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture, noOrigin: true);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync(state);

        kit.Client.StepsFor(kit.Client.PlanFor(write: true), delete: false).Should().Equal(WriteStep.BumpSetMarked, WriteStep.SrcDel);
    }

    [Fact]
    public async Task Origin_backed_caches_write_no_marker()
    {
        await using var origin = await KeyMoveKit.CreateAsync(fixture);
        await origin.SetStateAsync("S3");

        origin.Client.StepsFor(origin.Client.PlanFor(write: true), delete: false).Should().Equal(WriteStep.SrcDel, WriteStep.BumpSet);
    }

    [Theory]
    [InlineData(true, "S3")]
    [InlineData(false, "S3")]
    [InlineData(true, "S4")]
    [InlineData(false, "S4")]
    public async Task The_marker_is_set_with_N_and_before_L_is_deleted_and_removed_only_by_the_queue_afterwards(bool scripts, string state)
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture, scripts, noOrigin: true);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync(state);
        await kit.Db.StringSetAsync(kit.L("k"), "v0");
        string? between = null;
        string? nBetween = null;
        bool lBetween = false;
        var queuedBetween = -1;
        kit.Schedule.Hook = async (step, _) =>
        {
            if (step == SchedulePointStep.SourceDelete)
            {
                between = await kit.MarkerAsync("k");
                nBetween = await kit.GetAsync(kit.N("k"));
                lBetween = await kit.ExistsAsync(kit.L("k"));
                queuedBetween = kit.Client.Unmarks.Count;
            }
        };

        var result = await kit.Client.SetAsync("k", new Item("v1"), null, Ct);

        result.Outcome.Should().Be(WriteOutcome.Committed);
        between.Should().StartWith(kit.Id + "|", "the marker is part of the first step");
        nBetween.Should().Be("v1");
        lBetween.Should().BeTrue();
        queuedBetween.Should().Be(0, "the unmark is queued only after L was deleted");
        (await kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
        (await kit.MarkerAsync("k")).Should().Be(between, "the acknowledgement does not wait for the unmark");
        kit.Client.Unmarks.Count.Should().Be(1);

        await kit.Client.Unmarks.RunDueAsync();

        (await kit.MarkerAsync("k")).Should().BeNull();
        kit.Client.Unmarks.Count.Should().Be(0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_NoOrigin_delete_removes_the_marker_with_its_second_step(bool scripts)
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture, scripts, noOrigin: true);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        await kit.Client.SetAsync("k", new Item("v1"), null, Ct);
        (await kit.MarkerAsync("k")).Should().NotBeNull();

        var result = await kit.Client.RemoveAsync("k", Ct);

        result.Outcome.Should().Be(WriteOutcome.Committed);
        (await kit.MarkerAsync("k")).Should().BeNull();
        (await kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task N_lost_between_the_two_steps_of_a_write_and_a_promoter_never_publish_the_old_L(bool scripts)
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture, scripts, noOrigin: true);
        var promoted = await RunMarkedWriteAsync(kit, removeMarker: false);

        promoted.Kind.Should().Be(ReadOutcome.Absent, "the promoter helps the superseded copy away and publishes nothing");
        (await kit.GetAsync(kit.N("k"))).Should().BeNull();
        (await kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Negative_control_without_the_marker_the_promoter_publishes_the_old_L_over_the_acknowledged_write(bool scripts)
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture, scripts, noOrigin: true);
        var promoted = await RunMarkedWriteAsync(kit, removeMarker: true);

        promoted.Kind.Should().Be(ReadOutcome.Found);
        promoted.Value!.Name.Should().Be("v0");
        (await kit.GetAsync(kit.N("k"))).Should().Be("v0", "this is the bug the marker closes: v1 was acknowledged");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task In_S4_N_lost_inside_a_write_shows_the_older_copy_only_until_the_write_is_acknowledged(bool scripts)
    {
        // S4 has no promoter: a union reader may see the older copy inside the write, and nothing publishes it, so after the acknowledgement the key is a miss.
        await using var kit = await KeyMoveKit.CreateAsync(fixture, scripts, noOrigin: true);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S4");
        await kit.Db.StringSetAsync(kit.L("k"), "v0");
        PathRead<Item> inside = default;
        var entered = false;
        kit.Schedule.Hook = async (step, _) =>
        {
            if (step == SchedulePointStep.SourceDelete && !entered)
            {
                entered = true;
                await kit.Db.KeyDeleteAsync(kit.N("k"));
                inside = await kit.Client.ReadAsync("k", Ct);
            }
        };

        var write = await kit.Client.SetAsync("k", new Item("v1"), null, Ct);
        kit.Schedule.Hook = null;
        var after = await kit.Client.ReadAsync("k", Ct);

        write.Outcome.Should().Be(WriteOutcome.Committed);
        inside.Value!.Name.Should().Be("v0");
        (await kit.ExistsAsync(kit.N("k"))).Should().BeFalse("nothing published the older copy");
        after.Kind.Should().Be(ReadOutcome.Absent);
        (await kit.MarkerAsync("k")).Should().NotBeNull("the marker is set in S4 too, and waits for its unmark");
    }

    [Fact]
    public async Task An_unmark_that_fails_five_times_is_dropped_and_counted_and_leaves_only_an_orphan()
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture, noOrigin: true, options: new KeyMoveOptions { UnmarkAttempts = 2 });
        using var meter = new MeterCollector(kit.Cid);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        await kit.Client.SetAsync("k", new Item("v1"), null, Ct);
        kit.Plane.Data = null;

        await kit.Client.Unmarks.RunDueAsync(all: true);
        kit.Client.Unmarks.Count.Should().Be(1);
        await kit.Client.Unmarks.RunDueAsync(all: true);

        meter.Count("cache.keymove.unmark_dropped").Should().Be(1);
        kit.Client.Unmarks.Count.Should().Be(0);
        (await kit.MarkerAsync("k")).Should().NotBeNull();

        // N is lost: the orphan is harmless, because L is absent for good, and the next write of the key overwrites its field.
        await kit.Db.KeyDeleteAsync(kit.N("k"));
        kit.Plane.Data = new KeyMoveData(kit.Mux.GetDatabase(), kit.Scripts, kit.Names, () => 0, () => true);
        (await kit.Client.ReadAsync("k", Ct)).Kind.Should().Be(ReadOutcome.Absent);
        await kit.Client.SetAsync("k", new Item("v3"), null, Ct);
        (await kit.MarkerAsync("k")).Should().StartWith(kit.Id + "|");
    }

    [Fact]
    public async Task Above_half_the_queue_a_writer_removes_its_marker_before_it_is_acknowledged_until_the_queue_drains()
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture, noOrigin: true, options: new KeyMoveOptions { UnmarkQueueMax = 4 });
        await kit.PrepareAsync("a", "b", "c", "d", "e");
        await kit.SetStateAsync("S3");
        await kit.Client.SetAsync("a", new Item("v"), null, Ct);
        await kit.Client.SetAsync("b", new Item("v"), null, Ct);
        kit.Client.Unmarks.Saturated.Should().BeFalse("two of four is half, not above it");
        await kit.Client.SetAsync("c", new Item("v"), null, Ct);

        kit.Client.Unmarks.Saturated.Should().BeTrue();
        await kit.Client.SetAsync("d", new Item("v"), null, Ct);

        (await kit.MarkerAsync("d")).Should().BeNull("the marker was removed before the acknowledgement");
        kit.Client.Unmarks.Count.Should().Be(3);
        await kit.Client.Unmarks.RunDueAsync(all: true);
        kit.Client.Unmarks.Saturated.Should().BeFalse("below a quarter again");
        await kit.Client.SetAsync("e", new Item("v"), null, Ct);
        (await kit.MarkerAsync("e")).Should().NotBeNull();
        kit.Client.Unmarks.Count.Should().Be(1);
    }

    [Fact]
    public async Task A_first_attempt_at_the_record_limit_is_NotSent_and_changes_nothing()
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture, noOrigin: true);
        using var meter = new MeterCollector(kit.Cid);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        await kit.LimitMarkersAsync("k", 0);
        await kit.Db.StringSetAsync(kit.L("k"), "v0");

        var result = await kit.Client.SetAsync("k", new Item("v1"), null, Ct);

        result.Outcome.Should().Be(WriteOutcome.NotSent);
        kit.Schedule.Steps.Should().Equal(SchedulePointStep.Bump);
        (await kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
        (await kit.GetAsync(kit.L("k"))).Should().Be("v0");
        (await kit.FenceAsync("k")).Should().BeNull();
        (await kit.MarkerAsync("k")).Should().BeNull();
        kit.Client.Unmarks.Count.Should().Be(0);
        meter.Count("cache.keymove.marker_capacity").Should().Be(1);
    }

    [Fact]
    public async Task A_record_below_the_limit_takes_a_first_attempt_until_it_is_full()
    {
        // The record has room for one more field than it holds; a marker and the peak field take it.
        await using var kit = await KeyMoveKit.CreateAsync(fixture, noOrigin: true);
        await kit.PrepareAsync("k", "k2");
        await kit.SetStateAsync("S3");
        await kit.LimitMarkersAsync("k", 1);

        (await kit.Client.SetAsync("k", new Item("v1"), null, Ct)).Outcome.Should().Be(WriteOutcome.Committed);
        (await kit.Client.SetAsync("k2", new Item("v1"), null, Ct)).Outcome.Should().Be(WriteOutcome.NotSent);
        (await kit.Client.SetAsync("k", new Item("v2"), null, Ct)).Outcome.Should().Be(WriteOutcome.Committed, "the key has its field, so it is no new marker");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_rerun_across_a_minus_one_is_never_refused_for_the_limit(bool beforeStepOne)
    {
        var movedBefore = beforeStepOne ? SchedulePointStep.Bump : SchedulePointStep.SourceDelete;
        await using var kit = await KeyMoveKit.CreateAsync(fixture, noOrigin: true, options: new KeyMoveOptions { AdmissionPoll = TimeSpan.FromMilliseconds(5) });
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        await kit.LimitMarkersAsync("k", 1);
        var moved = false;
        kit.Schedule.Hook = async (step, _) =>
        {
            if (step == movedBefore && !moved)
            {
                // The identity moves, the key's marker is gone (removed as obsolete) and another key's marker has filled the record: a first attempt would now be refused.
                moved = true;
                await kit.MoveGateAsync();
                kit.ApplyRules("S3");
                await kit.Db.HashDeleteAsync(kit.Control("k"), kit.Field("k"));
                await kit.Db.HashSetAsync(kit.Control("k"), "sm:other", "x|y");
            }
        };

        var result = await kit.Client.SetAsync("k", new Item("v1"), null, Ct);

        result.Outcome.Should().Be(WriteOutcome.Committed, "an attempt was already sent, so the rerun is not a first attempt");
        (await kit.GetAsync(kit.N("k"))).Should().Be("v1");
        (await kit.MarkerAsync("k")).Should().StartWith(kit.Id + "|");
        (await kit.Db.HashGetAsync(kit.Control("k"), "lim")).Should().NotBeNull();
        (await kit.Db.HashLengthAsync(kit.Control("k"))).Should().BeGreaterThan((long)(await kit.Db.HashGetAsync(kit.Control("k"), "lim")), "a rerun may overshoot the limit");
    }

    [Fact]
    public async Task A_record_with_no_binding_value_is_NotSent_like_the_limit_and_changes_nothing()
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture, noOrigin: true);
        using var meter = new MeterCollector(kit.Cid);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        await kit.Db.HashSetAsync(kit.Control("k"), "bf", "missing");

        var result = await kit.Client.SetAsync("k", new Item("v1"), null, Ct);

        result.Outcome.Should().Be(WriteOutcome.NotSent);
        kit.Schedule.Steps.Should().Equal(SchedulePointStep.Bump);
        (await kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
        (await kit.FenceAsync("k")).Should().BeNull();
        (await kit.MarkerAsync("k")).Should().BeNull();
        meter.Count("cache.keymove.marker_no_binding").Should().Be(1);
    }

    [Fact]
    public async Task A_command_that_is_late_when_it_runs_is_refused_by_the_script_and_NotSent()
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture, noOrigin: true, options: new KeyMoveOptions { CommandTimeout = TimeSpan.FromSeconds(-5) });
        using var meter = new MeterCollector(kit.Cid);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");

        var result = await kit.Client.SetAsync("k", new Item("v1"), null, Ct);

        result.Outcome.Should().Be(WriteOutcome.NotSent);
        (await kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
        (await kit.MarkerAsync("k")).Should().BeNull();
        meter.Count("cache.keymove.marker_late").Should().Be(1);
        kit.Client.Permits.InFlight.Should().Be(0, "the reply came, so the permit is back");
    }

    [Fact]
    public async Task A_write_holds_no_permit_after_it_was_answered()
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture, noOrigin: true);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");

        await kit.Client.SetAsync("k", new Item("v1"), null, Ct);

        kit.Client.Permits.InFlight.Should().Be(0);
    }

    private static async Task<PathRead<Item>> RunMarkedWriteAsync(KeyMoveKit kit, bool removeMarker)
    {
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        await kit.Db.StringSetAsync(kit.L("k"), "v0");
        PathRead<Item> promoted = default;
        var inside = false;
        kit.Schedule.Hook = async (step, _) =>
        {
            if (step != SchedulePointStep.SourceDelete || inside)
            {
                return;
            }

            // The write has set N and the marker; N is evicted, and a promoter reads before the write's second step.
            inside = true;
            await kit.Db.KeyDeleteAsync(kit.N("k"));
            if (removeMarker)
            {
                await kit.Db.HashDeleteAsync(kit.Control("k"), kit.Field("k"));
            }

            promoted = await kit.Client.ReadAsync("k", Ct);
        };

        var write = await kit.Client.SetAsync("k", new Item("v1"), null, Ct);

        write.Outcome.Should().Be(WriteOutcome.Committed, "v1 was acknowledged");
        return promoted;
    }
}
