using System.Globalization;
using UiPath.Caching.KeyMove;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests.KeyMove;

[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class GroupRemovalTests(RedisContainerFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_S0_group_that_never_moved_loses_its_state()
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, true);
        await kit.InitAsync("a");

        (await Removal(kit).RemoveGroupAsync(kit.Group, null, Ct)).Should().Be(RemoveOutcome.Removed);

        (await kit.Store.ReadAsync(kit.Group, Ct)).Should().BeNull();
    }

    [Fact]
    public async Task An_S0_group_that_moved_and_came_back_loses_its_records_and_its_state()
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, true);
        await Walk(kit, ("S1", "fwd", 0), ("S0", "none", 0));
        (await kit.ControlDb.KeyExistsAsync(kit.Names.Control(kit.Group, 0))).Should().BeTrue();

        (await Removal(kit).RemoveGroupAsync(kit.Group, null, Ct)).Should().Be(RemoveOutcome.Removed);

        (await kit.ControlDb.KeyExistsAsync(kit.Names.Control(kit.Group, 0))).Should().BeFalse();
        (await kit.Store.ReadAsync(kit.Group, Ct)).Should().BeNull();
    }

    [Fact]
    public async Task An_S5_group_is_removed_only_after_its_sweep_completed()
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, true);
        var state = await Walk(kit, ("S1", "fwd", 0), ("S3", "fwd", 1), ("S4", "fwd", 1), ("S5", "fwd", 1));

        (await Removal(kit).RemoveGroupAsync(kit.Group, null, Ct)).Should().Be(RemoveOutcome.NotApplicable, "lswept is absent");
        (await kit.Store.ReadAsync(kit.Group, Ct)).Should().NotBeNull();
        await kit.Scripts.FieldsAsync(kit.StateKey, state.Id, [(StateFields.Swept, state.Id)], []);

        (await Removal(kit).RemoveGroupAsync(kit.Group, null, Ct)).Should().Be(RemoveOutcome.Removed);
        (await kit.ControlDb.KeyExistsAsync(kit.Names.Control(kit.Group, 0))).Should().BeFalse();
    }

    [Theory]
    [InlineData("S1")]
    [InlineData("S3")]
    public async Task A_group_that_is_moving_is_not_removed(string stage)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, true);
        await Walk(kit, ("S1", "fwd", 0), (stage, "fwd", stage == "S3" ? 1 : 0));

        (await Removal(kit).RemoveGroupAsync(kit.Group, null, Ct)).Should().Be(RemoveOutcome.NotApplicable);

        (await kit.Store.ReadAsync(kit.Group, Ct)).Should().NotBeNull();
        (await kit.ControlDb.KeyExistsAsync(kit.Names.Control(kit.Group, 0))).Should().BeTrue();
    }

    [Fact]
    public async Task An_S0_group_whose_X11_purge_is_not_finished_is_not_removed()
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, true);
        var state = await Walk(kit, ("S1", "fwd", 0), ("S0", "none", 0));
        await kit.Scripts.FieldsAsync(kit.StateKey, state.Id, [(StateFields.ColdStart, "-")], []);

        (await Removal(kit).RemoveGroupAsync(kit.Group, null, Ct)).Should().Be(RemoveOutcome.NotApplicable);
    }

    [Fact]
    public async Task A_record_of_a_newer_epoch_survives_and_keeps_the_state_so_the_command_can_be_repeated()
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, true);
        var state = await Walk(kit, ("S1", "fwd", 0), ("S0", "none", 0));
        await kit.ControlDb.HashSetAsync(kit.Names.Control(kit.Group, 0), "ep", (state.Ep + 5).ToString(CultureInfo.InvariantCulture));

        (await Removal(kit).RemoveGroupAsync(kit.Group, null, Ct)).Should().Be(RemoveOutcome.RecordsRemain);

        (await kit.Store.ReadAsync(kit.Group, Ct)).Should().NotBeNull();
        (await kit.ControlDb.KeyExistsAsync(kit.Names.Control(kit.Group, 0))).Should().BeTrue();
    }

    [Fact]
    public async Task A_lease_that_is_not_the_callers_stops_the_command_before_anything_is_deleted()
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, true);
        await Walk(kit, ("S1", "fwd", 0), ("S0", "none", 0));
        var lead = kit.Names.Lead(kit.Group);
        await kit.Db.StringSetAsync(lead, "successor");

        (await Removal(kit).RemoveGroupAsync(kit.Group, new LeaseFence(lead, "stale"), Ct)).Should().Be(RemoveOutcome.Refused);

        (await kit.Store.ReadAsync(kit.Group, Ct)).Should().NotBeNull();
        (await kit.ControlDb.KeyExistsAsync(kit.Names.Control(kit.Group, 0))).Should().BeTrue();
        await kit.Db.KeyDeleteAsync(lead);
    }

    [Fact]
    public async Task A_group_with_a_claim_needs_the_drop_and_stops_when_the_drop_fails()
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, true);
        var state = await Walk(kit, ("S1", "fwd", 0), ("S0", "none", 0));
        await kit.Scripts.FieldsAsync(kit.StateKey, state.Id, [(StateFields.Claim, "token")], []);
        var dropped = new List<string>();

        (await Removal(kit).RemoveGroupAsync(kit.Group, null, Ct)).Should().Be(RemoveOutcome.ClaimHeld);
        (await Removal(kit, (g, _, _) => Task.FromResult(ClaimOutcome.Failed)).RemoveGroupAsync(kit.Group, null, Ct)).Should().Be(RemoveOutcome.ClaimDropFailed);
        (await kit.Store.ReadAsync(kit.Group, Ct)).Should().NotBeNull();
        (await Removal(kit, (g, _, _) =>
        {
            dropped.Add(g);
            return Task.FromResult(ClaimOutcome.Done);
        }).RemoveGroupAsync(kit.Group, null, Ct)).Should().Be(RemoveOutcome.Removed);

        dropped.Should().Equal(kit.Group);
    }

    [Fact]
    public async Task A_group_with_no_state_is_reported()
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, true);

        (await Removal(kit).RemoveGroupAsync(kit.Group, null, Ct)).Should().Be(RemoveOutcome.NoState);
    }

    private static async Task<StateRecord> Walk(StateKit kit, params (string State, string Direction, int NPop)[] steps)
    {
        var state = await kit.InitAsync("a");
        foreach (var (next, direction, nPop) in steps)
        {
            (await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(state), StateKit.TargetFrom(state, next, direction, nPop), null, Ct)).Outcome.Should().Be(TransitionOutcome.Completed);
            state = (await kit.Store.ReadAsync(kit.Group, Ct))!;
        }

        return state;
    }

    private GroupRemoval Removal(StateKit kit, Func<string, LeaseFence?, CancellationToken, Task<ClaimOutcome>>? drop = null) =>
        new(kit.Store, kit.Scripts, new MoveScripts(kit.ControlDb, true, fixture.ServerVersion), kit.Names, drop);
}
