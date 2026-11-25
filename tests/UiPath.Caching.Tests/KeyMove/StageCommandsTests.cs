using UiPath.Caching.KeyMove;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests.KeyMove;

// The operator commands that store a consent or a hold: Init, Approve, Hold, Resume, SkipBarrier, Abandon.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class StageCommandsTests(RedisContainerFixture fixture)
{
    private static readonly StageDriverOptions Options = new();

    [Fact]
    public async Task Init_creates_the_S0_state_refreshes_the_set_of_an_S0_state_and_is_refused_in_S1()
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, true);
        var commands = new StageCommands(kit.Store, kit.Scripts, kit.Names, Options);

        var created = await commands.InitAsync(kit.Group, ["a"], CancellationToken.None);
        var refreshed = await commands.InitAsync(kit.Group, ["a", "b"], CancellationToken.None);

        created.Code.Should().Be(MoveScripts.StateCreated);
        refreshed.Code.Should().Be(MoveScripts.StateRefreshed);
        (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!.CacheIds.Should().Equal("a", "b");

        var s0 = (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!;
        await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(s0), StateKit.TargetFrom(s0, "S1", "fwd", 0), null, CancellationToken.None);
        (await commands.InitAsync(kit.Group, ["a"], CancellationToken.None)).Code.Should().Be(MoveScripts.StateNotInitial);
    }

    [Fact]
    public async Task Approve_stores_the_consent_with_the_identity_the_operator_and_the_evidence_and_it_is_void_once_the_state_moves()
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, true);
        var commands = new StageCommands(kit.Store, kit.Scripts, kit.Names, Options);
        var s0 = await kit.InitAsync("a");

        (await commands.ApproveAsync(kit.Group, ApprovalKinds.Start, "release floor attested", "alice", CancellationToken.None)).Should().Be(ApproveResult.Stored);

        var stored = (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!;
        var approval = Approval.ValidFor(stored, ApprovalKinds.Start)!;
        approval.Should().Match<Approval>(a => a.Operator == "alice" && a.Confirm == "release floor attested" && a.Identity == s0.Identity);

        await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(stored), StateKit.TargetFrom(stored, "S1", "fwd", 0), null, CancellationToken.None);
        var moved = (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!;
        Approval.Of(moved, ApprovalKinds.Start).Should().NotBeNull("the field is still there");
        Approval.ValidFor(moved, ApprovalKinds.Start).Should().BeNull("but it covers another identity");
    }

    [Fact]
    public async Task Approve_refuses_an_unknown_kind_a_missing_state_a_wrong_stage_and_the_wrong_override_sentence()
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, true);
        var commands = new StageCommands(kit.Store, kit.Scripts, kit.Names, Options);

        (await commands.ApproveAsync(kit.Group, ApprovalKinds.Start, "x", "op", CancellationToken.None)).Should().Be(ApproveResult.NoState);
        await kit.InitAsync("a");
        (await commands.ApproveAsync(kit.Group, "invented", "x", "op", CancellationToken.None)).Should().Be(ApproveResult.UnknownKind);
        (await commands.ApproveAsync(kit.Group, ApprovalKinds.Close, "x", "op", CancellationToken.None)).Should().Be(ApproveResult.NotApplicable, "nothing to close in S0");
        (await commands.ApproveAsync(kit.Group, ApprovalKinds.Release, "x", "op", CancellationToken.None)).Should().Be(ApproveResult.NotApplicable);
        (await commands.AbandonAsync(kit.Group, ApprovalKinds.ConfirmTexts[ApprovalKinds.Abandon], "op", CancellationToken.None)).Should().Be(ApproveResult.NotApplicable, "only a closed group is abandoned");
        (await commands.SkipBarrierAsync(kit.Group, ApprovalKinds.ConfirmTexts[ApprovalKinds.SkipBarrier], "op", CancellationToken.None)).Should().Be(ApproveResult.NotApplicable, "no transition waits");
        var wrong = () => commands.ApproveAsync(kit.Group, ApprovalKinds.StartUnsettled, "close enough", "op", CancellationToken.None);
        await wrong.Should().ThrowAsync<ArgumentException>();
        (await commands.ApproveAsync(kit.Group, ApprovalKinds.StartUnsettled, Options.UpgradeHoldOverrideText, "op", CancellationToken.None)).Should().Be(ApproveResult.Stored);
        var blank = () => commands.ApproveAsync(kit.Group, ApprovalKinds.Start, " ", "op", CancellationToken.None);
        await blank.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData(ApprovalKinds.CloseForce)]
    [InlineData(ApprovalKinds.ForceReverse)]
    [InlineData(ApprovalKinds.SkipBarrier)]
    [InlineData(ApprovalKinds.Abandon)]
    public async Task An_approval_that_goes_past_a_check_needs_its_exact_text_before_anything_else_is_checked(string kind)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, true);
        var commands = new StageCommands(kit.Store, kit.Scripts, kit.Names, Options);
        await kit.InitAsync("a");
        var text = ApprovalKinds.ConfirmTexts[kind];

        var loose = () => commands.ApproveAsync(kit.Group, kind, text.ToUpperInvariant(), "op", CancellationToken.None);

        (await loose.Should().ThrowAsync<ArgumentException>()).WithMessage($"*\"{text}\"*");
        (await commands.ApproveAsync(kit.Group, kind, text, "op", CancellationToken.None)).Should().Be(ApproveResult.NotApplicable, "the exact text passes, and S0 has nothing to go past");
    }

    [Fact]
    public async Task The_evidence_of_a_close_consent_is_the_stored_residue_so_a_new_inventory_voids_it()
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, true);
        var commands = new StageCommands(kit.Store, kit.Scripts, kit.Names, Options);
        var s0 = await kit.InitAsync("a");
        await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(s0), StateKit.TargetFrom(s0, "S1", "fwd", 0), null, CancellationToken.None);
        var s1 = (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!;
        await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(s1), StateKit.TargetFrom(s1, "S3", "fwd", 1), null, CancellationToken.None);
        var s3 = (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!;
        await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(s3), StateKit.TargetFrom(s3, "S4", "fwd", 1), null, CancellationToken.None);
        var s4 = (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!;
        await kit.Scripts.FieldsAsync(kit.StateKey, s4.Id, [("res:a", "2")], []);

        (await commands.ApproveAsync(kit.Group, ApprovalKinds.Close, "two residue keys acknowledged", "op", CancellationToken.None)).Should().Be(ApproveResult.Stored);
        Approval.ValidFor((await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!, ApprovalKinds.Close).Should().NotBeNull();

        await kit.Scripts.FieldsAsync(kit.StateKey, s4.Id, [("res:a", "3")], []);

        Approval.ValidFor((await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!, ApprovalKinds.Close).Should().BeNull();
    }

    [Fact]
    public async Task Hold_sets_the_operator_class_and_Resume_clears_both_classes()
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, true);
        var commands = new StageCommands(kit.Store, kit.Scripts, kit.Names, Options);
        (await commands.HoldAsync(kit.Group, "no state yet")).Should().BeFalse();
        await kit.InitAsync("a");

        (await commands.HoldAsync(kit.Group, "pause for the release")).Should().BeTrue();
        await kit.Store.SetHoldAsync(kit.Group, HoldKind.Automatic, "no_km:n2");
        (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!.Should().Match<StateRecord>(s => s.HoldOp == "pause for the release" && s.HoldAuto == "no_km:n2");

        (await commands.ResumeAsync(kit.Group)).Should().BeTrue();

        (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!.Should().Match<StateRecord>(s => s.HoldOp == string.Empty && s.HoldAuto == string.Empty);
    }

    [Fact]
    public async Task SkipBarrier_and_Abandon_apply_to_a_transition_at_dur_0_and_to_a_closed_group_only()
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, true);
        var commands = new StageCommands(kit.Store, kit.Scripts, kit.Names, Options);
        var s0 = await kit.InitAsync("a");
        await kit.Scripts.TransitAsync(kit.StateKey, StateKit.Expecting(s0), StateKit.TargetFrom(s0, "S1", "fwd", 0));

        (await commands.SkipBarrierAsync(kit.Group, ApprovalKinds.ConfirmTexts[ApprovalKinds.SkipBarrier], "op", CancellationToken.None)).Should().Be(ApproveResult.Stored);
        var stuck = (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!;
        Approval.ValidFor(stuck, ApprovalKinds.SkipBarrier).Should().NotBeNull();
        (await commands.AbandonAsync(kit.Group, ApprovalKinds.ConfirmTexts[ApprovalKinds.Abandon], "op", CancellationToken.None)).Should().Be(ApproveResult.NotApplicable);
    }

    [Fact]
    public async Task A_consent_written_against_a_state_that_moved_in_the_meantime_is_refused_by_the_script()
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, true);
        var s0 = await kit.InitAsync("a");
        await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(s0), StateKit.TargetFrom(s0, "S1", "fwd", 0), null, CancellationToken.None);

        var code = await kit.Scripts.FieldsAsync(kit.StateKey, s0.Id, [("appr:start", "stale")], []);

        code.Should().Be(MoveScripts.IdentityMismatch);
        (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!.Field("appr:start").Should().BeEmpty();
        (await kit.Scripts.FieldsAsync("no:such:state", "id", [("f", "v")], [])).Should().Be(0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_field_script_sets_and_deletes_fields_the_same_way_with_and_without_scripts(bool useScripts)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts);
        var s0 = await kit.InitAsync("a");

        (await kit.Scripts.FieldsAsync(kit.StateKey, s0.Id, [("x", "1"), ("y", "2")], ["upd"])).Should().Be(MoveScripts.Written);
        var after = (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!;
        after.Field("x").Should().Be("1");
        after.Field("y").Should().Be("2");
        after.FieldNames.Should().NotContain("upd");

        (await kit.Scripts.FieldsAsync(kit.StateKey, string.Empty, [], ["x"])).Should().Be(MoveScripts.Written, "no expected id means no id check");
        (await kit.Scripts.FieldsAsync(kit.StateKey, "other", [("z", "3")], [])).Should().Be(MoveScripts.IdentityMismatch);
        (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!.FieldNames.Should().Contain("y").And.NotContain("x").And.NotContain("z");
    }

    [Fact]
    public async Task On_a_cluster_a_consent_is_stored_and_read_back_through_the_state_slot()
    {
        await using var kit = await StateKit.ClusterAsync(true);
        var commands = new StageCommands(kit.Store, kit.Scripts, kit.Names, Options);
        await kit.InitAsync("a");

        (await commands.ApproveAsync(kit.Group, ApprovalKinds.Start, "release floor attested", "op", CancellationToken.None)).Should().Be(ApproveResult.Stored);
        (await commands.HoldAsync(kit.Group, "pause")).Should().BeTrue();

        var stored = (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!;
        Approval.ValidFor(stored, ApprovalKinds.Start).Should().NotBeNull();
        stored.HoldOp.Should().Be("pause");
    }
}
