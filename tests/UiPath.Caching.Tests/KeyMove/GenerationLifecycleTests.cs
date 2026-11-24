using StackExchange.Redis;
using UiPath.Caching.KeyMove;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests.KeyMove;

// The forward generation fg and the retiring one rg through the transitions, in the state hash and in the control records.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class GenerationLifecycleTests(RedisContainerFixture fixture)
{
    private static readonly TimeSpan Ten = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task The_start_mints_fg_as_the_id_of_the_new_identity_and_the_steps_after_it_keep_it()
    {
        await using var kit = await DriverKit.CreateAsync(fixture);
        await kit.PrepareAsync();

        var s1 = await kit.StepUntilAsync("S1");
        var s3 = await kit.StepUntilAsync("S3");
        var s4 = await ToDrainedFromS3Async(kit);

        s1.ForwardGeneration.Should().Be(s1.Id);
        s1.RetiringGeneration.Should().BeEmpty();
        s3.ForwardGeneration.Should().Be(s1.Id);
        s4.ForwardGeneration.Should().Be(s1.Id);
        (await RecordAsync(kit)).Should().Contain(("bf", "fg")).And.Contain(("fg", s1.Id)).And.NotContain(f => f.Field == "rg");
    }

    [Fact]
    public async Task The_reverse_start_moves_fg_to_rg_and_the_retire_step_clears_it_and_the_release_leaves_neither_and_the_next_start_mints_a_new_one()
    {
        await using var kit = await DriverKit.CreateAsync(fixture);
        var drained = await ToDrainedAsync(kit);
        var first = drained.ForwardGeneration;
        kit.Fleet.All(n => n.Fl = 2);

        var reverse = await kit.StepUntilAsync("S1", "rev", advance: Ten);
        var afterReverseStart = await RecordAsync(kit);
        var reverseMoving = await kit.StepUntilAsync("S3", "rev");
        var afterRetire = await RecordAsync(kit);
        await kit.StepUntilAsync("S4", "rev");
        await kit.Commands.ApproveAsync(kit.Group, ApprovalKinds.Release, "ok", "op", CancellationToken.None);
        var off = await kit.StepUntilAsync("S0", "none");
        var afterRelease = await RecordAsync(kit);
        kit.Fleet.All(n => n.Fl = 5);
        var again = await kit.StepUntilAsync("S1");

        reverse.ForwardGeneration.Should().BeEmpty();
        reverse.RetiringGeneration.Should().Be(first);
        afterReverseStart.Should().Contain(("rg", first)).And.NotContain(f => f.Field == "fg");
        reverseMoving.RetiringGeneration.Should().BeEmpty();
        afterRetire.Should().NotContain(f => f.Field == "rg").And.NotContain(f => f.Field == "fg");
        off.ForwardGeneration.Should().BeEmpty();
        off.RetiringGeneration.Should().BeEmpty();
        afterRelease.Should().NotContain(f => f.Field == "fg" || f.Field == "rg" || f.Field == "bf");
        again.ForwardGeneration.Should().Be(again.Id).And.NotBe(first);
    }

    [Fact]
    public async Task The_return_to_the_move_and_a_rebuild_keep_fg_with_a_new_identity()
    {
        await using var kit = await DriverKit.CreateAsync(fixture);
        var drained = await ToDrainedAsync(kit);
        kit.Fleet.All(n => n.Fl = 3);

        var back = await kit.StepUntilAsync("S3", advance: Ten);
        var rebuilt = await kit.Kit.Upkeep.RebuildAsync(kit.Group, back, null, null, CancellationToken.None);
        var afterRebuild = await kit.StateAsync();

        back.Id.Should().NotBe(drained.Id);
        back.ForwardGeneration.Should().Be(drained.ForwardGeneration);
        rebuilt.Outcome.Should().Be(TransitionOutcome.Completed);
        afterRebuild.Id.Should().NotBe(back.Id);
        afterRebuild.ForwardGeneration.Should().Be(drained.ForwardGeneration);
        (await RecordAsync(kit)).Should().Contain(("fg", drained.ForwardGeneration));
    }

    [Fact]
    public async Task The_way_back_from_before_the_move_leaves_neither_generation()
    {
        await using var kit = await DriverKit.CreateAsync(fixture);
        await kit.PrepareAsync();
        var s1 = await kit.StepUntilAsync("S1");
        s1.ForwardGeneration.Should().NotBeEmpty();
        kit.Fleet.All(n => n.Fl = 2);

        var off = await kit.StepUntilAsync("S0", "none", advance: Ten);

        off.ForwardGeneration.Should().BeEmpty();
        off.RetiringGeneration.Should().BeEmpty();
        (await RecordAsync(kit)).Should().NotContain(f => f.Field == "fg" || f.Field == "rg");
    }

    [Fact]
    public async Task The_abandon_of_a_reverse_start_that_never_committed_restores_fg_and_rg_of_the_committed_state()
    {
        await using var kit = await DriverKit.CreateAsync(fixture);
        var drained = await ToDrainedAsync(kit);
        var state = await kit.StateAsync();

        // A reverse start that was written to the state hash and went no further.
        var (set, clear) = Generations.Of(BindingChange.Retire, state, "reverse-start-id");
        var target = new StateTarget("S1", "rev", 1, state.Ep + 1, "reverse-start-id", StateRecord.FormatPrev(state.State, state.Direction, state.NPop, state.Ep, state.Id), 1, MoveId: state.Ep + 1, Set: set, Clear: clear);
        (await kit.Kit.Scripts.TransitAsync(kit.Kit.StateKey, StateExpectation.Holding(state.Ep, state.Id, 1), target)).Should().Be(MoveScripts.Written);
        var inFlight = await kit.StateAsync();
        inFlight.ForwardGeneration.Should().BeEmpty();
        inFlight.RetiringGeneration.Should().Be(drained.ForwardGeneration);

        var result = await kit.Kit.Sequence.AbandonAsync(kit.Group, false, 1, new SequenceOptions(), CancellationToken.None);
        var restored = await kit.StateAsync();

        result.Outcome.Should().Be(TransitionOutcome.Completed);
        restored.State.Should().Be("S4");
        restored.ForwardGeneration.Should().Be(drained.ForwardGeneration);
        restored.RetiringGeneration.Should().BeEmpty();
        (await RecordAsync(kit)).Should().Contain(("fg", drained.ForwardGeneration));
    }

    [Fact]
    public async Task DURABLE_keeps_the_generations_of_the_committed_state_as_pfg_and_prg()
    {
        await using var kit = await DriverKit.CreateAsync(fixture);
        var drained = await ToDrainedAsync(kit);
        kit.Fleet.All(n => n.Fl = 2);

        var reverse = await kit.StepUntilAsync("S1", "rev", advance: Ten);

        drained.Field(StateFields.PrevFg).Should().Be(drained.ForwardGeneration);
        drained.Field(StateFields.PrevRg).Should().BeEmpty();
        reverse.Field(StateFields.PrevFg).Should().BeEmpty();
        reverse.Field(StateFields.PrevRg).Should().Be(drained.ForwardGeneration);
    }

    [Fact]
    public async Task The_abandon_of_a_retire_step_that_never_committed_restores_rg_and_the_abandon_of_a_start_leaves_no_generation()
    {
        await using var kit = await DriverKit.CreateAsync(fixture);
        await kit.PrepareAsync();
        var off = await kit.StateAsync();
        var startTarget = new StateTarget("S1", "fwd", 0, off.Ep + 1, "start-id", StateRecord.FormatPrev(off.State, off.Direction, off.NPop, off.Ep, off.Id), 1, MoveId: off.Ep + 1, Set: [(StateFields.Fg, "start-id")], Clear: [StateFields.Rg]);
        (await kit.Kit.Scripts.TransitAsync(kit.Kit.StateKey, StateExpectation.Holding(off.Ep, off.Id, 1), startTarget)).Should().Be(MoveScripts.Written);

        await kit.Kit.Sequence.AbandonAsync(kit.Group, false, 1, new SequenceOptions(), CancellationToken.None);
        var abandonedStart = await kit.StateAsync();
        await kit.StepUntilAsync("S1");
        var drained = await ToDrainedFromS3Async(kit);
        kit.Fleet.All(n => n.Fl = 2);
        var reverse = await kit.StepUntilAsync("S1", "rev", advance: Ten);
        var retireTarget = new StateTarget("S3", "rev", 1, reverse.Ep + 1, "retire-id", StateRecord.FormatPrev(reverse.State, reverse.Direction, reverse.NPop, reverse.Ep, reverse.Id), 1, Clear: [StateFields.Rg]);
        (await kit.Kit.Scripts.TransitAsync(kit.Kit.StateKey, StateExpectation.Holding(reverse.Ep, reverse.Id, 1), retireTarget)).Should().Be(MoveScripts.Written);
        (await kit.StateAsync()).RetiringGeneration.Should().BeEmpty();

        await kit.Kit.Sequence.AbandonAsync(kit.Group, false, 1, new SequenceOptions(), CancellationToken.None);
        var abandonedRetire = await kit.StateAsync();

        abandonedStart.State.Should().Be("S0");
        abandonedStart.ForwardGeneration.Should().BeEmpty();
        abandonedRetire.State.Should().Be("S1");
        abandonedRetire.Direction.Should().Be("rev");
        abandonedRetire.RetiringGeneration.Should().Be(drained.ForwardGeneration);
        abandonedRetire.ForwardGeneration.Should().BeEmpty();
    }

    [Fact]
    public async Task Reassert_takes_fg_from_the_record_of_the_highest_ep()
    {
        await using var kit = await DriverKit.CreateAsync(fixture);
        var drained = await ToDrainedAsync(kit);
        await kit.Kit.Db.KeyDeleteAsync(kit.Kit.StateKey);

        var result = await kit.Kit.Recovery.ReassertAsync(kit.Group, "state lost", null, null, null, CancellationToken.None);
        var state = await kit.StateAsync();

        result.Outcome.Should().Be(RecoveryOutcome.Done);
        state.State.Should().Be("S4");
        state.ForwardGeneration.Should().Be(drained.ForwardGeneration);
        (await RecordAsync(kit)).Should().Contain(("fg", drained.ForwardGeneration));
    }

    [Fact]
    public async Task Reassert_in_S1_reverse_takes_rg_from_the_record_and_gives_no_fg()
    {
        await using var kit = await DriverKit.CreateAsync(fixture);
        var drained = await ToDrainedAsync(kit);
        kit.Fleet.All(n => n.Fl = 2);
        await kit.StepUntilAsync("S1", "rev", advance: Ten);
        await kit.Kit.Db.KeyDeleteAsync(kit.Kit.StateKey);

        var result = await kit.Kit.Recovery.ReassertAsync(kit.Group, "state lost", null, null, null, CancellationToken.None);
        var state = await kit.StateAsync();

        result.Outcome.Should().Be(RecoveryOutcome.Done);
        state.Direction.Should().Be("rev");
        state.RetiringGeneration.Should().Be(drained.ForwardGeneration);
        state.ForwardGeneration.Should().BeEmpty();
    }

    [Fact]
    public async Task Reassert_with_no_record_that_shows_fg_gives_the_forward_run_a_new_one()
    {
        await using var kit = await DriverKit.CreateAsync(fixture);
        var drained = await ToDrainedAsync(kit);
        await kit.Kit.Db.KeyDeleteAsync(kit.Kit.StateKey);
        await kit.Kit.ControlDb.HashDeleteAsync(kit.Kit.Names.Control(kit.Group, 0), ["fg", "bf"]);

        await kit.Kit.Recovery.ReassertAsync(kit.Group, "state lost", null, null, null, CancellationToken.None);
        var state = await kit.StateAsync();

        state.ForwardGeneration.Should().NotBeEmpty().And.NotBe(drained.ForwardGeneration);
        state.ForwardGeneration.Should().Be(state.Id);
    }

    [Fact]
    public async Task The_cold_start_of_a_closed_group_leaves_neither_generation()
    {
        await using var kit = await DriverKit.CreateAsync(fixture);
        kit.Facts.AcceptRefill = true;
        await ToDrainedAsync(kit);
        await kit.Commands.ApproveAsync(kit.Group, ApprovalKinds.Close, "residue acknowledged", "op", CancellationToken.None);
        var closed = await kit.StepUntilAsync("S5", advance: TimeSpan.FromSeconds(30));
        closed.ForwardGeneration.Should().NotBeEmpty();
        await kit.Commands.AbandonAsync(kit.Group, "cold start accepted", "op", CancellationToken.None);

        var flipped = await kit.StepUntilAsync("S0", "none");

        flipped.ForwardGeneration.Should().BeEmpty();
        flipped.RetiringGeneration.Should().BeEmpty();
        (await RecordAsync(kit)).Should().NotContain(f => f.Field == "fg" || f.Field == "rg" || f.Field == "bf");
    }

    private static async Task<StateRecord> ToDrainedAsync(DriverKit kit)
    {
        await kit.PrepareAsync();
        await kit.StepUntilAsync("S3");
        return await ToDrainedFromS3Async(kit);
    }

    private static async Task<StateRecord> ToDrainedFromS3Async(DriverKit kit)
    {
        kit.Mover.Report = FakeMoverStatus.Drained(0, ("a", 0));
        kit.Mover.Inventory = new Dictionary<string, long> { ["a"] = 0 };
        return await kit.StepUntilAsync("S4");
    }

    private static async Task<List<(string Field, string Value)>> RecordAsync(DriverKit kit) =>
        [.. (await kit.Kit.ControlAsync(0)).Select(e => (e.Name.ToString(), e.Value.ToString()))];
}
