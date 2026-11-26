using StackExchange.Redis;
using UiPath.Caching.KeyMove;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

// Exercises every write and delete form with both scripts and the transaction fallback.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class KeyMoveWriteTests(RedisFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("S1", true)]
    [InlineData("S1", false)]
    [InlineData("S2", true)]
    public async Task S1_and_S2_write_L_by_one_checked_step_and_leave_N_alone(string state, bool scripts)
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture, scripts);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync(state, nPop: 0);

        var result = await kit.Client.SetAsync("k", new Item("v1"), kit.Clock.ToDateTimeOffset(TimeSpan.FromMinutes(10)), Ct);

        result.Outcome.Should().Be(WriteOutcome.Committed);
        (await kit.GetAsync(kit.L("k"))).Should().Be("v1");
        (await kit.Db.KeyTimeToLiveAsync(kit.L("k")))!.Value.Should().BeGreaterThan(TimeSpan.FromMinutes(9));
        (await kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
        kit.Schedule.Steps.Should().Equal(SchedulePointStep.SourceSet);
        (await kit.Client.RemoveAsync("k", Ct)).Outcome.Should().Be(WriteOutcome.Committed);
        (await kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
    }

    [Theory]
    [InlineData("S1", false)]
    [InlineData("S5", true)]
    public async Task Forward_writes_without_a_fence_commit_with_untagged_native_keys(string state, bool swept)
    {
        await using var kit = await KeyMoveKit.CreateAsync(
            fixture,
            source: k => new InPlaceMoveSide<Item>(k.SidePlane, k.Group, k.L, ItemCodec.Instance, null, k.Clock));
        await kit.PrepareAsync("k");
        await kit.SetStateAsync(state, swept: swept);
        kit.Plane.Data = kit.SideData with { Names = new MoveKeyNames("prefix:", kit.Names.LibraryPrefix, cluster: true) };
        var writtenKey = swept ? kit.N("k") : kit.L("k");

        (await kit.Client.SetAsync("k", new Item("v1"), null, Ct)).Outcome.Should().Be(WriteOutcome.Committed);
        (await kit.GetAsync(writtenKey)).Should().Be("v1");
        (await kit.Client.RemoveAsync("k", Ct)).Outcome.Should().Be(WriteOutcome.Committed);
        (await kit.ExistsAsync(writtenKey)).Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task S2_is_closed_for_a_NoOrigin_cache_for_writes_and_deletes_and_nothing_is_written(bool scripts)
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture, scripts, noOrigin: true);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S2", nPop: 0);

        var write = async () => await kit.Client.SetAsync("k", new Item("v1"), null, Ct);
        var delete = async () => await kit.Client.RemoveAsync("k", Ct);

        await write.Should().ThrowAsync<CacheUnavailableException>();
        await delete.Should().ThrowAsync<CacheUnavailableException>();
        (await kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
        kit.Schedule.Steps.Should().BeEmpty();
    }

    [Theory]
    [InlineData("S3", true)]
    [InlineData("S3", false)]
    [InlineData("S4", true)]
    public async Task An_origin_backed_write_deletes_L_then_sets_N_under_a_replaced_fence(string state, bool scripts)
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture, scripts);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync(state);
        await kit.Db.StringSetAsync(kit.L("k"), "v0");
        await kit.Db.StringSetAsync(kit.Names.ForwardFence(kit.N("k")), "before");

        (await kit.Client.SetAsync("k", new Item("v1"), kit.Clock.ToDateTimeOffset(TimeSpan.FromMinutes(10)), Ct)).Outcome.Should().Be(WriteOutcome.Committed);

        kit.Schedule.Steps.Should().Equal(SchedulePointStep.SourceDelete, SchedulePointStep.FenceObserve, SchedulePointStep.Bump);
        (await kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
        (await kit.GetAsync(kit.N("k"))).Should().Be("v1");
        (await kit.Db.KeyTimeToLiveAsync(kit.N("k")))!.Value.Should().BeGreaterThan(TimeSpan.FromMinutes(9));
        (await kit.FenceAsync("k")).Should().NotBe("before");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_NoOrigin_write_sets_N_first_so_a_stored_key_never_reads_as_a_miss_between_the_steps(bool scripts)
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture, scripts, noOrigin: true);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        await kit.Db.StringSetAsync(kit.L("k"), "v0");
        string? between = null;
        kit.Schedule.Hook = async (step, _) =>
        {
            if (step == SchedulePointStep.SourceDelete)
            {
                // A reader that arrives between the steps still finds a stored key, and it is the new value.
                between = (await kit.Client.ReadAsync("k", Ct)).Value?.Name;
            }
        };

        await kit.Client.SetAsync("k", new Item("v1"), null, Ct);

        between.Should().Be("v1");
        kit.Schedule.Steps.Take(2).Should().Equal(SchedulePointStep.Bump, SchedulePointStep.SourceDelete);
        (await kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
    }

    [Theory]
    [InlineData("S3", true)]
    [InlineData("S3", false)]
    [InlineData("S4", true)]
    public async Task A_delete_removes_L_then_N_and_replaces_the_fence(string state, bool scripts)
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture, scripts);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync(state);
        await kit.Db.StringSetAsync(kit.L("k"), "v0");
        await kit.Db.StringSetAsync(kit.N("k"), "v1");
        await kit.Db.StringSetAsync(kit.Names.ForwardFence(kit.N("k")), "before");

        (await kit.Client.RemoveAsync("k", Ct)).Outcome.Should().Be(WriteOutcome.Committed);

        kit.Schedule.Steps.Should().Equal(SchedulePointStep.SourceDelete, SchedulePointStep.FenceObserve, SchedulePointStep.Bump);
        (await kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
        (await kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
        (await kit.FenceAsync("k")).Should().NotBe("before");
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task S5_deletes_the_source_copy_until_the_sweep_is_known_and_always_writes_N_natively(bool swept, bool scripts)
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture, scripts);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S5", swept: swept);
        await kit.Db.StringSetAsync(kit.L("k"), "old");

        (await kit.Client.SetAsync("k", new Item("v1"), kit.Clock.ToDateTimeOffset(TimeSpan.FromMinutes(10)), Ct)).Outcome.Should().Be(WriteOutcome.Committed);

        kit.Schedule.Steps.Should().Equal(swept ? [SchedulePointStep.NativeWrite] : [SchedulePointStep.SourceDelete, SchedulePointStep.NativeWrite]);
        (await kit.GetAsync(kit.N("k"))).Should().Be("v1");
        (await kit.ExistsAsync(kit.L("k"))).Should().Be(swept);
        kit.Schedule.Reset();
        (await kit.Client.RemoveAsync("k", Ct)).Outcome.Should().Be(WriteOutcome.Committed);
        (await kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
        kit.Schedule.Steps.Should().Equal(swept ? [SchedulePointStep.NativeWrite] : [SchedulePointStep.SourceDelete, SchedulePointStep.NativeWrite]);
    }

    [Fact]
    public async Task A_write_whose_remaining_life_is_below_a_millisecond_is_a_delete_and_one_with_no_expiry_is_not()
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        await kit.Db.StringSetAsync(kit.N("k"), "v0");

        (await kit.Client.SetAsync("k", new Item("v1"), kit.Clock.ToDateTimeOffset(TimeSpan.FromMilliseconds(-5)), Ct)).Outcome.Should().Be(WriteOutcome.Committed);
        (await kit.ExistsAsync(kit.N("k"))).Should().BeFalse();

        (await kit.Client.SetAsync("k", new Item("v2"), null, Ct)).Outcome.Should().Be(WriteOutcome.Committed);
        (await kit.GetAsync(kit.N("k"))).Should().Be("v2");
        (await kit.Db.KeyTimeToLiveAsync(kit.N("k"))).Should().BeNull();
    }

    [Fact]
    public async Task S0_and_unmanaged_caches_write_through_the_plain_path()
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture);
        await kit.SetStateAsync("S0");
        await kit.Client.SetAsync("k", new Item("v"), null, Ct);
        await kit.Client.RemoveAsync("k", Ct);
        kit.Plane.Rules = GroupRules.Unaffected;
        await kit.Client.SetAsync("k", new Item("v"), null, Ct);

        kit.Plain.Sets.Should().Be(2);
        kit.Plain.Removes.Should().Be(1);
        kit.Schedule.Steps.Should().BeEmpty();
    }

    [Theory]
    [InlineData("fail")]
    [InlineData("reverse")]
    public async Task A_failed_group_or_a_reverse_barrier_makes_a_write_unavailable_without_fallback(string how)
    {
        var reverse = how == "reverse";
        await using var kit = await KeyMoveKit.CreateAsync(fixture, noOrigin: reverse);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync(reverse ? "S1" : "S3", direction: reverse ? "rev" : "fwd");
        if (!reverse)
        {
            kit.Plane.Rules = kit.Plane.Rules with { Writes = WriteRule.FailClosed };
        }

        var write = async () => await kit.Client.SetAsync("k", new Item("v"), null, Ct);

        await write.Should().ThrowAsync<CacheUnavailableException>();
        kit.Plain.Sets.Should().Be(0);
        kit.Schedule.Steps.Should().BeEmpty();
        kit.Client.NoFallback.Should().BeTrue();
    }

    [Fact]
    public async Task A_write_waits_for_a_new_snapshot_and_then_runs_or_fails_closed()
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture, options: new KeyMoveOptions { AdmissionPoll = TimeSpan.FromMilliseconds(10) });
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        var open = kit.Plane.Rules;
        kit.Plane.Rules = open with { Writes = WriteRule.WaitThenFailClosed, WriteWait = TimeSpan.FromMilliseconds(400) };
        var write = kit.Client.SetAsync("k", new Item("v1"), null, Ct).AsTask();
        var admission = await kit.Time.NextTimerWhileRunningAsync(write, Ct);
        kit.Plane.Rules = open;
        kit.Time.Advance(admission);
        (await write).Outcome.Should().Be(WriteOutcome.Committed);

        kit.Plane.Rules = open with { Writes = WriteRule.WaitThenFailClosed, WriteWait = TimeSpan.FromMilliseconds(60) };
        var late = kit.Client.SetAsync("k", new Item("v2"), null, Ct).AsTask();
        await kit.Time.NextTimerWhileRunningAsync(late, Ct);
        kit.Time.Advance(TimeSpan.FromMilliseconds(60));
        await Assert.ThrowsAsync<CacheUnavailableException>(() => late);
        (await kit.GetAsync(kit.N("k"))).Should().Be("v1");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_script_that_answers_minus_one_reruns_the_whole_unit_under_the_new_identity(bool scripts)
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture, scripts);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        await kit.Db.StringSetAsync(kit.L("k"), "v0");
        kit.Schedule.Hook = async (step, nth) =>
        {
            if (step == SchedulePointStep.SourceDelete && nth == 1)
            {
                await kit.SetStateAsync("S5", swept: true);
            }
        };

        (await kit.Client.SetAsync("k", new Item("v1"), null, Ct)).Outcome.Should().Be(WriteOutcome.Committed);

        // Under S3 the first SRCDEL was refused; the unit ran again as an S5 write, which is a native write of N.
        kit.Schedule.Steps.Should().Equal(SchedulePointStep.SourceDelete, SchedulePointStep.NativeWrite);
        (await kit.GetAsync(kit.N("k"))).Should().Be("v1");
        (await kit.ExistsAsync(kit.L("k"))).Should().BeTrue();
    }

    [Fact]
    public async Task A_minus_one_the_gate_does_not_know_yet_is_reported_not_sent_and_writes_nothing()
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");

        // The record moved on and the node's gate did not: nothing can be run under an identity the server refuses.
        await kit.AdvanceRecordsAsync("k", kit.Ep + 1, "elsewhere");
        var result = await kit.Client.SetAsync("k", new Item("v1"), null, Ct);

        result.Outcome.Should().Be(WriteOutcome.NotSent);
        (await kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
    }

    [Fact]
    public async Task A_first_step_that_is_not_sent_stops_the_unit_typed_not_sent()
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        await kit.Db.StringSetAsync(kit.L("k"), "v0");
        kit.Connected = false;

        (await kit.Client.SetAsync("k", new Item("v1"), null, Ct)).Outcome.Should().Be(WriteOutcome.NotSent);

        (await kit.ExistsAsync(kit.L("k"))).Should().BeTrue();
        kit.Client.Retries.Count.Should().Be(0);
    }

    [Fact]
    public async Task A_first_step_with_no_answer_is_unknown_with_the_settle_bound()
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        var other = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        kit.Plane.Data = kit.Plane.Data! with { Db = other.GetDatabase(), Scripts = new MoveScripts(other.GetDatabase(), true, new Version(7, 0)) };
        kit.Schedule.Hook = async (step, _) =>
        {
            if (step == SchedulePointStep.SourceDelete)
            {
                await other.DisposeAsync();
            }
        };

        var result = await kit.Client.SetAsync("k", new Item("v1"), null, Ct);

        result.Outcome.Should().Be(WriteOutcome.Unknown);
        result.SettleWithin.Should().Be(TimeSpan.FromSeconds(30));
        kit.Client.Retries.Count.Should().Be(0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_later_step_that_fails_goes_to_the_retry_set_the_call_is_unknown_and_the_retry_completes_it(bool scripts)
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture, scripts);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        await kit.Db.StringSetAsync(kit.L("k"), "v0");

        // A fence in force before the first attempt is the token the retry may apply over.
        await kit.Db.StringSetAsync(kit.Names.ForwardFence(kit.N("k")), "token-before");
        kit.Schedule.Hook = (step, _) =>
        {
            if (step == SchedulePointStep.Bump)
            {
                kit.Connected = false;
            }

            return Task.CompletedTask;
        };

        var result = await kit.Client.SetAsync("k", new Item("v1"), null, Ct);

        result.Outcome.Should().Be(WriteOutcome.Unknown);
        kit.Client.Retries.IsPending("k").Should().BeTrue();
        (await kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
        (await kit.ExistsAsync(kit.N("k"))).Should().BeFalse();

        kit.Schedule.Hook = null;
        kit.Connected = true;
        await kit.Client.Retries.RunDueAsync(all: true);

        kit.Client.Retries.Count.Should().Be(0);
        (await kit.GetAsync(kit.N("k"))).Should().Be("v1");
    }

    [Fact]
    public async Task A_newer_write_of_the_key_supersedes_a_pending_retry_so_the_old_step_never_runs_after_it()
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        kit.Schedule.Hook = (step, nth) =>
        {
            if (step == SchedulePointStep.Bump && nth == 1)
            {
                kit.Connected = false;
            }

            return Task.CompletedTask;
        };
        await kit.Client.SetAsync("k", new Item("old"), null, Ct);
        kit.Client.Retries.IsPending("k").Should().BeTrue();

        kit.Schedule.Hook = null;
        kit.Connected = true;
        await kit.Client.SetAsync("k", new Item("new"), null, Ct);
        await kit.Client.Retries.RunDueAsync(all: true);

        kit.Client.Retries.Count.Should().Be(0);
        (await kit.GetAsync(kit.N("k"))).Should().Be("new");
    }

    [Fact]
    public async Task A_step_that_gives_up_counts_retry_exhausted_and_alerts()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch.AddYears(56));
        await using var kit = await KeyMoveKit.CreateAsync(fixture, clock: clock, options: new KeyMoveOptions { RetryLife = TimeSpan.FromSeconds(5) });
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        kit.Schedule.Hook = (step, _) =>
        {
            if (step == SchedulePointStep.Bump)
            {
                kit.Connected = false;
            }

            return Task.CompletedTask;
        };
        await kit.Client.SetAsync("k", new Item("v1"), null, Ct);
        using var meter = new MeterCollector(kit.Cid);

        clock.Advance(TimeSpan.FromSeconds(6));
        await kit.Client.Retries.RunDueAsync();

        kit.Client.Retries.Count.Should().Be(0);
        kit.Plane.Alerts.Should().ContainSingle(a => a.Code == "retry_exhausted");
        meter.Count("move_retry_exhausted").Should().Be(1);
    }

    [Fact]
    public async Task A_NoOrigin_forward_write_is_a_list_of_steps_so_a_third_step_is_a_table_change()
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture, noOrigin: true);
        await kit.SetStateAsync("S3");
        var steps = kit.Client.StepsFor(kit.Client.PlanFor(write: true), delete: false);

        steps.Should().Equal(WriteStep.BumpSetMarked, WriteStep.SrcDel);
    }

}
