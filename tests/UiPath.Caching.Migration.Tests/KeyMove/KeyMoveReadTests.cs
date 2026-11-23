using UiPath.Caching.KeyMove;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

// Verifies each state's union reads and read-through promotion with both scripts and the transaction fallback.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class KeyMoveReadTests(RedisFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_promotion_moves_L_to_N_with_the_capture_before_the_snapshot_and_the_steps_in_order(bool scripts)
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture, scripts);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        await kit.Db.StringSetAsync(kit.L("k"), "v0", TimeSpan.FromMinutes(5));

        var read = await kit.Client.ReadAsync("k", Ct);

        read.Kind.Should().Be(ReadOutcome.Found);
        read.Value!.Name.Should().Be("v0");
        (await kit.GetAsync(kit.N("k"))).Should().Be("v0");
        (await kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
        (await kit.Db.KeyTimeToLiveAsync(kit.N("k")))!.Value.Should().BeGreaterThan(TimeSpan.FromMinutes(4)).And.BeLessThanOrEqualTo(TimeSpan.FromMinutes(5));
        kit.Schedule.Steps.Should().Equal(
            SchedulePointStep.ReadDestination,
            SchedulePointStep.ReadDestinationOnMaster,
            SchedulePointStep.ProbeSource,
            SchedulePointStep.Capture,
            SchedulePointStep.ReadSource,
            SchedulePointStep.Publish,
            SchedulePointStep.ConfirmDestination,
            SchedulePointStep.CompareDelete);
    }

    [Fact]
    public async Task A_promotion_replaces_the_fence_so_a_second_promoter_that_captured_before_it_is_refused()
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        await kit.Db.StringSetAsync(kit.L("k"), "v0");
        var before = await StealFenceAsync(kit, "k");

        (await kit.Client.ReadAsync("k", Ct)).Value!.Name.Should().Be("v0");

        (await kit.FenceAsync("k")).Should().NotBe(before);
    }

    [Fact]
    public async Task A_miss_probes_first_makes_no_fence_and_sets_a_hint_that_a_local_event_drops()
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");

        (await kit.Client.ReadAsync("k", Ct)).Kind.Should().Be(ReadOutcome.Absent);
        kit.Schedule.Steps.Should().NotContain(SchedulePointStep.Capture);
        (await kit.FenceAsync("k")).Should().BeNull();
        kit.Hints().Count.Should().Be(1);

        kit.Schedule.Reset();
        (await kit.Client.ReadAsync("k", Ct)).Kind.Should().Be(ReadOutcome.Absent);
        kit.Schedule.Steps.Should().NotContain(SchedulePointStep.ProbeSource);

        kit.Client.KeyChanged("k");
        kit.Schedule.Reset();
        await kit.Client.ReadAsync("k", Ct);
        kit.Schedule.Steps.Should().Contain(SchedulePointStep.ProbeSource);
    }

    [Fact]
    public async Task A_hint_made_under_one_identity_is_not_read_under_another_and_a_gap_clears_every_hint()
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        await kit.Client.ReadAsync("k", Ct);

        await kit.SetStateAsync("S3");
        kit.Schedule.Reset();
        await kit.Client.ReadAsync("k", Ct);
        kit.Schedule.Steps.Should().Contain(SchedulePointStep.ProbeSource);

        kit.Client.Gap();
        kit.Hints().Count.Should().Be(0);
    }

    [Fact]
    public async Task A_hint_never_hides_an_N_that_another_node_created()
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        await kit.Client.ReadAsync("k", Ct);
        await kit.Db.StringSetAsync(kit.N("k"), "other");

        (await kit.Client.ReadAsync("k", Ct)).Value!.Name.Should().Be("other");
    }

    [Fact]
    public async Task A_missing_L_proves_N_was_published_before_so_N_is_read_again_before_the_miss()
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");

        // N appears after the first two reads and before the probe: the re-read after the absent probe finds it.
        kit.Schedule.Hook = async (step, _) =>
        {
            if (step == SchedulePointStep.ProbeSource)
            {
                await kit.Db.StringSetAsync(kit.N("k"), "late");
            }
        };

        (await kit.Client.ReadAsync("k", Ct)).Value!.Name.Should().Be("late");
        kit.Hints().Count.Should().Be(0);
    }

    [Fact]
    public async Task A_closed_gate_serves_a_miss_without_publishing_and_without_a_hint()
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        await kit.Db.StringSetAsync(kit.L("k"), "v0");
        await kit.Db.HashSetAsync(kit.Names.ControlFor(kit.Group, kit.N("k")), "c:" + kit.Cid, "CLOSED:x");

        (await kit.Client.ReadAsync("k", Ct)).Kind.Should().Be(ReadOutcome.Absent);

        (await kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
        (await kit.ExistsAsync(kit.L("k"))).Should().BeTrue();
        kit.Hints().Count.Should().Be(0);
    }

    [Fact]
    public async Task An_unreadable_L_is_left_alone_and_counted_and_an_expired_one_is_a_miss()
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture);
        await kit.PrepareAsync("a", "b");
        await kit.SetStateAsync("S3");
        await kit.Db.StringSetAsync(kit.L("a"), "junk:1");
        await kit.Db.StringSetAsync(kit.L("b"), "exp:1");

        (await kit.Client.ReadAsync("a", Ct)).Kind.Should().Be(ReadOutcome.Unreadable);
        (await kit.Client.ReadAsync("b", Ct)).Kind.Should().Be(ReadOutcome.Absent);

        (await kit.GetAsync(kit.L("a"))).Should().Be("junk:1");
        (await kit.GetAsync(kit.L("b"))).Should().Be("exp:1");
        (await kit.ExistsAsync(kit.N("a"))).Should().BeFalse();
        (await kit.ExistsAsync(kit.N("b"))).Should().BeFalse();
    }

    [Fact]
    public async Task A_too_large_entry_is_served_from_L_and_not_promoted()
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        await kit.Db.StringSetAsync(kit.L("k"), "big:1");

        (await kit.Client.ReadAsync("k", Ct)).Value!.Name.Should().Be("big:1");

        (await kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
        (await kit.ExistsAsync(kit.L("k"))).Should().BeTrue();
    }

    [Fact]
    public async Task A_capture_to_snapshot_gap_above_the_bound_abandons_the_promotion_and_serves_uncached()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch.AddYears(56));
        await using var kit = await KeyMoveKit.CreateAsync(fixture, clock: clock);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        await kit.Db.StringSetAsync(kit.L("k"), "v0");

        // The clock moves between the send of CAP and the arrival of the snapshot reply.
        kit.Schedule.Hook = (step, _) =>
        {
            if (step == SchedulePointStep.ReadSource)
            {
                clock.Advance(TimeSpan.FromMilliseconds(501));
            }

            return Task.CompletedTask;
        };

        var read = await kit.Client.ReadAsync("k", Ct);

        read.Value!.Name.Should().Be("v0");
        read.Uncached.Should().BeTrue();
        (await kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
        (await kit.ExistsAsync(kit.L("k"))).Should().BeTrue();
    }

    [Fact]
    public async Task A_gap_at_the_bound_still_promotes()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch.AddYears(56));
        await using var kit = await KeyMoveKit.CreateAsync(fixture, clock: clock);
        await kit.PrepareAsync("a");
        await kit.SetStateAsync("S3");
        await kit.Db.StringSetAsync(kit.L("a"), "v0");
        kit.Schedule.Hook = (step, _) =>
        {
            if (step == SchedulePointStep.ReadSource)
            {
                clock.Advance(TimeSpan.FromMilliseconds(500));
            }

            return Task.CompletedTask;
        };

        (await kit.Client.ReadAsync("a", Ct)).Uncached.Should().BeFalse();
        (await kit.ExistsAsync(kit.N("a"))).Should().BeTrue();
    }

    [Fact]
    public async Task A_promotion_held_past_the_budget_is_abandoned_without_publishing()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch.AddYears(56));
        await using var kit = await KeyMoveKit.CreateAsync(fixture, clock: clock, options: new KeyMoveOptions { PromotionBudget = TimeSpan.Zero });
        await kit.PrepareAsync("b");
        await kit.SetStateAsync("S3");
        await kit.Db.StringSetAsync(kit.L("b"), "v1");
        kit.Schedule.Hook = (step, _) =>
        {
            if (step == SchedulePointStep.ReadSource)
            {
                clock.Advance(TimeSpan.FromMilliseconds(1));
            }

            return Task.CompletedTask;
        };

        (await kit.Client.ReadAsync("b", Ct)).Uncached.Should().BeTrue();
        (await kit.ExistsAsync(kit.N("b"))).Should().BeFalse();
    }

    [Fact]
    public async Task A_connection_event_since_the_capture_abandons_the_promotion()
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        await kit.Db.StringSetAsync(kit.L("k"), "v0");
        kit.Schedule.Hook = (step, _) =>
        {
            if (step == SchedulePointStep.ReadSource)
            {
                kit.ConnectionEvent();
            }

            return Task.CompletedTask;
        };

        var read = await kit.Client.ReadAsync("k", Ct);

        read.Uncached.Should().BeTrue();
        (await kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
    }

    [Fact]
    public async Task A_publish_that_finds_N_created_returns_that_value_and_leaves_L()
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        await kit.Db.StringSetAsync(kit.L("k"), "v0");
        kit.Schedule.Hook = async (step, _) =>
        {
            if (step == SchedulePointStep.Publish)
            {
                await kit.Db.StringSetAsync(kit.N("k"), "winner");
            }
        };

        (await kit.Client.ReadAsync("k", Ct)).Value!.Name.Should().Be("winner");

        (await kit.GetAsync(kit.N("k"))).Should().Be("winner");
        (await kit.ExistsAsync(kit.L("k"))).Should().BeTrue();
    }

    [Fact]
    public async Task A_publish_whose_fence_moved_starts_again_twice_and_then_serves_uncached_and_counts_fence_refused()
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        await kit.Db.StringSetAsync(kit.L("k"), "v0");
        kit.Schedule.Hook = async (step, _) =>
        {
            if (step == SchedulePointStep.Publish)
            {
                await kit.Db.StringSetAsync(kit.Names.ForwardFence(kit.N("k")), Guid.NewGuid().ToString("N"));
            }
        };

        var read = await kit.Client.ReadAsync("k", Ct);

        read.Value!.Name.Should().Be("v0");
        read.Uncached.Should().BeTrue();
        kit.Schedule.Steps.Count(s => s == SchedulePointStep.Capture).Should().Be(3);
        (await kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
    }

    [Fact]
    public async Task A_publish_past_the_conservative_deadline_serves_from_L_and_deletes_nothing()
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        await kit.Db.StringSetAsync(kit.L("k"), "v0", TimeSpan.FromMilliseconds(400));
        kit.Schedule.Hook = async (step, _) =>
        {
            if (step == SchedulePointStep.Publish)
            {
                await Task.Delay(600, Ct);
            }
        };

        var read = await kit.Client.ReadAsync("k", Ct);

        read.Value!.Name.Should().Be("v0");
        read.Uncached.Should().BeTrue();
        (await kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
    }

    [Fact]
    public async Task A_cleanup_that_finds_N_evicted_keeps_L_as_the_copy()
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        await kit.Db.StringSetAsync(kit.L("k"), "v0");
        kit.Schedule.Hook = async (step, _) =>
        {
            if (step == SchedulePointStep.ConfirmDestination)
            {
                await kit.Db.KeyDeleteAsync(kit.N("k"));
            }
        };

        (await kit.Client.ReadAsync("k", Ct)).Value!.Name.Should().Be("v0");

        (await kit.ExistsAsync(kit.L("k"))).Should().BeTrue();
    }

    [Fact]
    public async Task A_cleanup_whose_L_changed_deletes_nothing()
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        await kit.Db.StringSetAsync(kit.L("k"), "v0");
        kit.Schedule.Hook = async (step, _) =>
        {
            if (step == SchedulePointStep.CompareDelete)
            {
                await kit.Db.StringSetAsync(kit.L("k"), "newer");
            }
        };

        await kit.Client.ReadAsync("k", Ct);

        (await kit.GetAsync(kit.L("k"))).Should().Be("newer");
    }

    [Fact]
    public async Task A_script_that_answers_the_identity_changed_reruns_the_read_under_the_new_rules()
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        await kit.Db.StringSetAsync(kit.L("k"), "v0");
        kit.Schedule.Hook = async (step, nth) =>
        {
            if (step == SchedulePointStep.Capture && nth == 1)
            {
                // The transition lands under the unit's feet: the record and the rules now hold a newer identity.
                await kit.SetStateAsync("S4");
            }
        };

        var read = await kit.Client.ReadAsync("k", Ct);

        read.Value!.Name.Should().Be("v0");
        (await kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
    }

    [Theory]
    [InlineData(true, "n", true)]
    [InlineData(false, "n", true)]
    [InlineData(true, "l", false)]
    public async Task S4_reads_N_first_then_L_only_for_a_cache_with_residue(bool residue, string where, bool expectFound)
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S4");
        kit.Plane.Residue = residue;
        await kit.Db.StringSetAsync(where == "n" ? kit.N("k") : kit.L("k"), "v");

        var read = await kit.Client.ReadAsync("k", Ct);

        read.Kind.Should().Be((where == "l" && residue) || expectFound ? ReadOutcome.Found : ReadOutcome.Absent);
        (await kit.ExistsAsync(where == "n" ? kit.N("k") : kit.L("k"))).Should().BeTrue();
        kit.Schedule.Steps.Should().NotContain(SchedulePointStep.Capture);
    }

    [Fact]
    public async Task S5_reads_N_only_and_never_a_residue_L()
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S5");
        await kit.Db.StringSetAsync(kit.L("k"), "old");

        (await kit.Client.ReadAsync("k", Ct)).Kind.Should().Be(ReadOutcome.Absent);
        kit.Schedule.Steps.Should().NotContain(SchedulePointStep.ReadSource);
    }

    [Fact]
    public async Task S1_reads_L_only_while_nothing_is_promoted_and_a_junk_N_is_never_served()
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S1", nPop: 0);
        await kit.Db.StringSetAsync(kit.N("k"), "junk-n");
        await kit.Db.StringSetAsync(kit.L("k"), "v0");

        (await kit.Client.ReadAsync("k", Ct)).Value!.Name.Should().Be("v0");
        kit.Schedule.Steps.Should().NotContain(SchedulePointStep.ReadDestination);
    }

    [Fact]
    public async Task S1_for_a_NoOrigin_cache_checks_the_identity_on_a_miss_and_reruns_when_it_moved()
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture, noOrigin: true);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S1", nPop: 0);

        // The record already shows a later identity: a promotion may have moved the key, so the unit re-reads the state.
        await kit.AdvanceRecordsAsync("k", kit.Ep + 1, "later");
        kit.Schedule.Hook = async (step, nth) =>
        {
            if (step == SchedulePointStep.ControlCheck && nth == 1)
            {
                await kit.SetStateAsync("S3");
                await kit.Db.StringSetAsync(kit.N("k"), "moved");
            }
        };

        (await kit.Client.ReadAsync("k", Ct)).Value!.Name.Should().Be("moved");
        kit.Schedule.Steps.Should().Contain(SchedulePointStep.ControlCheck);
    }

    [Fact]
    public async Task S1_for_an_origin_backed_cache_makes_no_identity_check_on_a_miss()
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S1", nPop: 0);

        (await kit.Client.ReadAsync("k", Ct)).Kind.Should().Be(ReadOutcome.Absent);
        kit.Schedule.Steps.Should().NotContain(SchedulePointStep.ControlCheck);
    }

    [Fact]
    public async Task Unstarted_groups_S0_and_unmanaged_caches_read_through_the_plain_path()
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture);
        await kit.SetStateAsync("S0");
        await kit.Client.ReadAsync("k", Ct);
        kit.Plane.Rules = GroupRules.Unaffected;
        await kit.Client.ReadAsync("k", Ct);
        kit.Plane.Rules = new GroupRules(true, ReadRule.Follow, WriteRule.Follow, new ActedState("S3", "fwd", 9, "x", 1, false), null, false, true, [], new HashSet<string> { "other" });
        await kit.Client.ReadAsync("k", Ct);

        kit.Plain.Reads.Should().Be(3);
        kit.Schedule.Steps.Should().BeEmpty();
    }

    [Fact]
    public async Task The_blind_rule_and_a_reverse_state_read_the_union_without_promotion()
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3", direction: "rev");
        await kit.Db.StringSetAsync(kit.L("k"), "v0");

        (await kit.Client.ReadAsync("k", Ct)).Value!.Name.Should().Be("v0");
        (await kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
        kit.Schedule.Steps.Should().NotContain(SchedulePointStep.Capture);
    }

    // A promoter that captured earlier holds the token in force; the test takes it the way CAP does.
    private static async Task<string?> StealFenceAsync(KeyMoveKit kit, string key)
    {
        var fence = kit.Names.ForwardFence(kit.N(key));
        await kit.Db.StringSetAsync(fence, "captured-before");
        return "captured-before";
    }
}
