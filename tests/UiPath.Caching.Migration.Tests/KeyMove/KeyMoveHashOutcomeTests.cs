using UiPath.Caching.KeyMove;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

// How a hash write, fill and refresh ends when the state changes under it, the connection fails, or the call is not managed at all.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class KeyMoveHashOutcomeTests(RedisFixture fixture)
{
    private static readonly Dictionary<string, string> Original = new() { ["a"] = "1", ["b"] = "2", ["c"] = "3" };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task While_the_state_is_unmanaged_every_call_goes_to_the_plain_path_and_nothing_is_stored_by_the_forms()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture);
        var items = KeyMoveHashKit.Items(("a", "1"));

        (await kit.Client.SetAsync("k", items, Merge(), Ct)).Outcome.Should().Be(WriteOutcome.Committed);
        (await kit.Client.SetAsync("k", items, Replace(), Ct)).Outcome.Should().Be(WriteOutcome.Committed);
        (await kit.Client.RemoveAsync("k", Ct)).Outcome.Should().Be(WriteOutcome.Committed);
        (await kit.Client.RemoveFieldsAsync("k", ["a"], Ct)).Outcome.Should().Be(WriteOutcome.Committed);
        (await kit.Client.FillAsync("k", items, Replace(), null, Ct)).Outcome.Should().Be(WriteOutcome.Committed);
        (await kit.Client.RefreshAsync("k", CacheExpiry.After(TimeSpan.FromMinutes(1)), Ct)).Outcome.Should().Be(WriteOutcome.Committed);
        (await kit.Client.RefreshAsync("k", CacheExpiry.After(TimeSpan.FromMinutes(1)), null, Ct)).Outcome.Should().Be(WriteOutcome.Committed);
        (await kit.Client.ReadAsync("k", null, Ct)).Kind.Should().Be(ReadOutcome.Absent);
        (await kit.Client.ContainsAsync("k", Ct)).Should().BeNull("the plain stub cannot say");
        (await kit.Client.ExpireTimeAsync("k", Ct)).Should().BeNull();

        (kit.Plain.Sets, kit.Plain.Removes, kit.Plain.FieldRemoves, kit.Plain.Fills, kit.Plain.Refreshes, kit.Plain.Reads).Should().Be((2, 1, 1, 1, 2, 1));
        kit.Client.Moving.Should().BeFalse();
        kit.Client.NoFallback.Should().BeFalse();
        (await kit.Kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
        (await kit.Kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
    }

    [Fact]
    public async Task In_a_moving_state_the_client_says_so_keeps_the_stored_hash_of_a_lost_fill_and_forgets_what_it_remembers_of_a_key()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3");
        var path = (IHashPathClient<Item>)kit.Client;

        path.Moving.Should().BeTrue();
        path.NoFallback.Should().BeTrue();
        path.KeepsStoredOnFill.Should().BeTrue();
        path.SettleWithin.Should().Be(TimeSpan.FromSeconds(30));
        path.InFlightKeys.Should().BeEmpty();
        kit.Client.Hints.Set("k", kit.Kit.Id, MoveSide.Legacy);
        path.KeyChanged("k");
        kit.Client.Hints.Has("k", kit.Kit.Id, MoveSide.Legacy).Should().BeFalse();
        kit.Client.Hints.Set("k", kit.Kit.Id, MoveSide.Legacy);
        path.Gap();
        kit.Client.Hints.Has("k", kit.Kit.Id, MoveSide.Legacy).Should().BeFalse();
        await path.StopRetriesAsync();
    }

    [Theory]
    [InlineData("replace")]
    [InlineData("delete")]
    [InlineData("delete-fields")]
    public async Task A_write_that_finds_the_state_unmanaged_when_it_runs_again_goes_to_the_plain_path(string write)
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3");
        await kit.SetAsync(kit.L("k"), Original);
        kit.Kit.Schedule.Hook = async (step, _) =>
        {
            if (step == SchedulePointStep.SourceDelete)
            {
                await kit.Kit.MoveGateAsync();
                kit.Kit.Plane.Rules = GroupRules.Unaffected;
            }
        };

        var result = write switch
        {
            "replace" => await kit.Client.SetAsync("k", KeyMoveHashKit.Items(("x", "9")), Replace(), Ct),
            "delete" => await kit.Client.RemoveAsync("k", Ct),
            _ => await kit.Client.RemoveFieldsAsync("k", ["a"], Ct),
        };

        result.Outcome.Should().Be(WriteOutcome.Committed);
        (kit.Plain.Sets, kit.Plain.Removes, kit.Plain.FieldRemoves).Should().Be(write switch { "replace" => (1, 0, 0), "delete" => (0, 1, 0), _ => (0, 0, 1) });
    }

    [Fact]
    public async Task A_write_planned_under_an_older_identity_fails_closed_when_the_rules_never_follow()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, options: new KeyMoveOptions { AdmissionPoll = TimeSpan.FromMilliseconds(5), MaxReruns = 2 });
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3");
        await kit.Kit.MoveGateAsync();

        await FluentActions.Awaiting(async () => await kit.Client.SetAsync("k", KeyMoveHashKit.Items(("x", "9")), Replace(), Ct)).Should().ThrowAsync<CacheUnavailableException>();

        (await kit.Kit.ExistsAsync(kit.N("k"))).Should().BeFalse("nothing was sent under rules that are not the gate's");
    }

    [Fact]
    public async Task A_write_planned_under_an_older_identity_is_built_again_once_the_rules_follow()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, options: new KeyMoveOptions { AdmissionPoll = TimeSpan.FromMilliseconds(20), MaxReruns = 100 });
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3");
        await kit.Kit.MoveGateAsync();

        var write = kit.Client.SetAsync("k", KeyMoveHashKit.Items(("x", "9")), Replace(), Ct).AsTask();
        await Task.Delay(150, Ct);
        write.IsCompleted.Should().BeFalse("the write waits for the rules to follow the gate");
        kit.Kit.ApplyRules("S3");

        (await write).Outcome.Should().Be(WriteOutcome.Committed);
        (await kit.FieldsAsync(kit.N("k"))).Should().BeEquivalentTo(new Dictionary<string, string> { ["x"] = "9" });
    }

    [Theory]
    [InlineData(false, WriteOutcome.NotSent)]
    [InlineData(true, WriteOutcome.Unknown)]
    public async Task A_write_whose_reruns_are_used_up_ends_without_a_guess_unless_an_attempt_already_applied(bool noOrigin, WriteOutcome expected)
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, noOrigin: noOrigin, options: new KeyMoveOptions { MaxReruns = 0 });
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3");
        kit.Kit.Schedule.Hook = async (step, _) =>
        {
            if (step == SchedulePointStep.SourceDelete)
            {
                await kit.Kit.MoveGateAsync();
            }
        };

        var result = await kit.Client.SetAsync("k", KeyMoveHashKit.Items(("x", "9")), Replace(), Ct);

        result.Outcome.Should().Be(expected);
    }

    [Fact]
    public async Task A_write_that_cannot_be_admitted_after_an_attempt_applied_is_Unknown_and_before_one_is_closed()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, noOrigin: true);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3");
        kit.Kit.Plane.RefuseEntry = true;
        await FluentActions.Awaiting(async () => await kit.Client.SetAsync("k", KeyMoveHashKit.Items(("x", "9")), Replace(), Ct)).Should().ThrowAsync<CacheUnavailableException>();
        kit.Kit.Plane.RefuseEntry = false;
        kit.Kit.Schedule.Hook = async (step, _) =>
        {
            if (step == SchedulePointStep.SourceDelete)
            {
                await kit.Kit.MoveGateAsync();
                kit.Kit.ApplyRules("S3");
                kit.Kit.Plane.RefuseEntry = true;
            }
        };

        var result = await kit.Client.SetAsync("k", KeyMoveHashKit.Items(("x", "9")), Replace(), Ct);

        result.Outcome.Should().Be(WriteOutcome.Unknown);
        (await kit.FieldsAsync(kit.N("k"))).Should().ContainKey("x", "the first attempt applied it");
    }

    [Fact]
    public async Task A_rerun_whose_marked_write_is_not_sent_after_an_attempt_applied_is_Unknown()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, noOrigin: true);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3");
        kit.Kit.Schedule.Hook = async (step, nth) =>
        {
            if (step == SchedulePointStep.SourceDelete && nth == 1)
            {
                await kit.Kit.MoveGateAsync();
                kit.Kit.ApplyRules("S3");
            }
            else if (step == SchedulePointStep.HashWrite && nth == 2)
            {
                kit.Kit.Connected = false;
            }
        };

        var result = await kit.Client.SetAsync("k", KeyMoveHashKit.Items(("x", "9")), Replace(), Ct);

        result.Outcome.Should().Be(WriteOutcome.Unknown);
        (await kit.FieldsAsync(kit.N("k"))).Should().ContainKey("x");
    }

    [Theory]
    [InlineData(false, WriteOutcome.NotSent)]
    [InlineData(true, WriteOutcome.Unknown)]
    public async Task A_step_refused_by_an_identity_nobody_can_join_ends_the_write_by_whether_an_attempt_applied(bool applied, WriteOutcome expected)
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, noOrigin: applied);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3");
        kit.Kit.Schedule.Hook = async (step, nth) =>
        {
            if (applied && step == SchedulePointStep.SourceDelete && nth == 1)
            {
                await kit.Kit.MoveGateAsync();
                kit.Kit.ApplyRules("S3");
            }
            else if (step == (applied ? SchedulePointStep.HashWrite : SchedulePointStep.SourceDelete) && nth == (applied ? 2 : 1))
            {
                await kit.Kit.AdvanceRecordsAsync("k", kit.Kit.Ep + 1, "elsewhere");
            }
        };

        var result = await kit.Client.SetAsync("k", KeyMoveHashKit.Items(("x", "9")), Replace(), Ct);

        result.Outcome.Should().Be(expected);
    }

    [Fact]
    public async Task A_later_step_refused_by_an_identity_nobody_can_join_is_Unknown_and_retried_until_the_fence_is_gone()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3");
        await kit.SetAsync(kit.L("k"), Original);
        kit.Kit.Schedule.Hook = async (step, _) =>
        {
            if (step == SchedulePointStep.HashWrite)
            {
                await kit.Kit.AdvanceRecordsAsync("k", kit.Kit.Ep + 1, "elsewhere");
            }
        };

        var result = await kit.Client.SetAsync("k", KeyMoveHashKit.Items(("x", "9")), Replace(), Ct);

        result.Outcome.Should().Be(WriteOutcome.Unknown);
        result.SettleWithin.Should().Be(TimeSpan.FromSeconds(30));
        (await kit.Kit.ExistsAsync(kit.L("k"))).Should().BeFalse("the first step ran");
        kit.Client.Retries.Count.Should().Be(1);
        ((IHashPathClient<Item>)kit.Client).InFlightKeys.Should().Contain("k");
    }

    [Fact]
    public async Task A_command_that_fails_in_flight_is_Unknown_for_the_first_step_and_the_write_stays_visible_to_the_retry_for_a_later_one()
    {
        FreezableDatabase? freezer = null;
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, interceptDb: db => FreezableDatabase.Over(db, out freezer));
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3");
        await kit.SetAsync(kit.L("k"), Original);
        kit.Kit.Schedule.Hook = (step, _) =>
        {
            if (step == SchedulePointStep.SourceDelete)
            {
                freezer!.Frozen = true;
            }

            return Task.CompletedTask;
        };

        var result = await kit.Client.SetAsync("k", KeyMoveHashKit.Items(("x", "9")), Replace(), Ct);

        result.Outcome.Should().Be(WriteOutcome.Unknown);
        (await kit.FieldsAsync(kit.L("k"))).Should().BeEquivalentTo(Original, "the first step never reached the server");
        kit.Client.Retries.Count.Should().Be(0, "a first step that failed has nothing applied to finish");
    }

    [Fact]
    public async Task A_fence_that_cannot_be_read_before_the_first_attempt_does_not_stop_the_write()
    {
        FreezableDatabase? freezer = null;
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, interceptDb: db => FreezableDatabase.Over(db, out freezer));
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3");
        await kit.SetAsync(kit.L("k"), Original);
        kit.Kit.Schedule.Hook = (step, _) =>
        {
            freezer!.Frozen = step == SchedulePointStep.FenceObserve;
            return Task.CompletedTask;
        };

        var result = await kit.Client.SetAsync("k", KeyMoveHashKit.Items(("x", "9")), Replace(), Ct);

        result.Outcome.Should().Be(WriteOutcome.Committed);
        (await kit.FieldsAsync(kit.N("k"))).Should().BeEquivalentTo(new Dictionary<string, string> { ["x"] = "9" });
    }

    [Fact]
    public async Task A_marked_write_with_no_reply_is_Unknown_and_keeps_its_permit_until_it_cannot_apply()
    {
        FreezableDatabase? freezer = null;
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, noOrigin: true, interceptDb: db => FreezableDatabase.Over(db, out freezer));
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3");
        kit.Kit.Schedule.Hook = (step, _) =>
        {
            if (step == SchedulePointStep.HashWrite)
            {
                freezer!.Frozen = true;
            }

            return Task.CompletedTask;
        };

        var result = await kit.Client.SetAsync("k", KeyMoveHashKit.Items(("x", "9")), Replace(), Ct);

        result.Outcome.Should().Be(WriteOutcome.Unknown);
        (await kit.Kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
    }

    [Fact]
    public async Task A_retry_of_a_write_whose_life_has_passed_is_never_sent()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, clock: clock);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3");
        await kit.SetAsync(kit.L("k"), Original);
        kit.Kit.Schedule.Hook = (step, _) =>
        {
            if (step == SchedulePointStep.HashWrite)
            {
                kit.Kit.Connected = false;
            }

            return Task.CompletedTask;
        };
        var until = clock.GetUtcNow().AddSeconds(30);
        (await kit.Client.SetAsync("k", KeyMoveHashKit.Items(("x", "9")), Replace(until), Ct)).Outcome.Should().Be(WriteOutcome.Unknown);
        kit.Kit.Schedule.Hook = null;
        kit.Kit.Connected = true;
        clock.Advance(TimeSpan.FromMinutes(1));

        await kit.Client.Retries.RunDueAsync(all: true);

        (await kit.Kit.ExistsAsync(kit.N("k"))).Should().BeFalse("a write whose life has passed would only be a delete");
        kit.Client.Retries.Count.Should().Be(0);
    }

    [Fact]
    public async Task A_client_that_keeps_no_retries_and_remembers_nothing_answers_the_interface_defaults()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture);
        var path = (IHashPathClient<Item>)kit.Plain;

        path.NoFallback.Should().BeFalse();
        path.KeepsStoredOnFill.Should().BeFalse();
        path.Moving.Should().BeFalse();
        path.InFlightKeys.Should().BeEmpty();
        path.SettledAsync("k").Should().BeSameAs(PathClientTasks.NoPendingRetry);
        path.StopRetriesAsync().IsCompletedSuccessfully.Should().BeTrue();
        path.KeyChanged("k");
        path.Gap();
    }

    private static HashWriteOptions Replace(DateTimeOffset? deadline = null) => new(HashCacheSetOption.KeyReplace, deadline);

    private static HashWriteOptions Merge(DateTimeOffset? deadline = null) => new(HashCacheSetOption.HashReplace, deadline);
}
