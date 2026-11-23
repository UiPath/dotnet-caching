using StackExchange.Redis;
using UiPath.Caching.KeyMove;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests.KeyMove;

// Each test interleaves a second actor at the point where a check and the act that depends on it were separate reads.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class StateRaceTests(RedisContainerFixture fixture)
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_resumer_that_begins_the_commit_after_the_abandon_probe_wins_and_the_abandon_is_refused(bool useScripts)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts);
        var (stored, control) = await SyncedAsync(kit);

        // After the abandon has looked at the records and found none committed, a resumer marks the commit begun and commits the record.
        kit.Topology.AfterControlRead = async () =>
        {
            await kit.Scripts.CommitBeginAsync(kit.StateKey, stored.Id);
            await kit.Scripts.ControlCommitAsync(control, stored.Ep, stored.Id);
        };
        var abandon = await kit.Sequence.AbandonAsync(kit.Group, false, 1, null, CancellationToken.None);
        kit.Topology.AfterControlRead = null;

        abandon.Outcome.Should().Be(TransitionOutcome.StateChanged);
        (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!.Should().Match<StateRecord>(s => s.Id == stored.Id && !s.Durable);
        (await kit.ControlAsync(0)).Should().Contain(e => e.Name == "dur" && e.Value == "1");
        (await kit.Sequence.ResumeAsync(kit.Group, null, CancellationToken.None)).Outcome.Should().Be(TransitionOutcome.Completed);
        (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!.Id.Should().Be(stored.Id);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_resumer_that_finds_its_identity_already_abandoned_stops_before_any_commit(bool useScripts)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts);
        var (stored, _) = await SyncedAsync(kit);
        var abandon = await kit.Sequence.AbandonAsync(kit.Group, false, 1, null, CancellationToken.None);
        abandon.Outcome.Should().Be(TransitionOutcome.Completed);

        (await kit.Scripts.CommitBeginAsync(kit.StateKey, stored.Id)).Should().Be(0);
        (await kit.Db.HashGetAsync(kit.StateKey, "cmt")).Should().NotBe(stored.Id, "the abandoned identity never began its commit");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_controller_whose_lease_ran_out_writes_nothing_more(bool useScripts)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts);
        var init = await kit.InitAsync("a");
        var target = StateKit.TargetFrom(init, "S1", "fwd", 0);

        var run = await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(init), target, new SequenceOptions(HoldsLease: () => false), CancellationToken.None);
        run.Outcome.Should().Be(TransitionOutcome.LeaseLost);
        (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!.Id.Should().Be(init.Id);

        await kit.Scripts.TransitAsync(kit.StateKey, StateKit.Expecting(init), target);
        var abandon = await kit.Sequence.AbandonAsync(kit.Group, false, 1, new SequenceOptions(HoldsLease: () => false), CancellationToken.None);
        abandon.Outcome.Should().Be(TransitionOutcome.LeaseLost);
        (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!.Id.Should().Be(target.Id);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_lease_that_expires_between_the_epoch_sync_and_the_commit_stops_the_sequence_before_the_commit_begins(bool useScripts)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts);
        var (stored, control) = await SyncedAsync(kit, sync: false);
        var asked = 0;

        var result = await kit.Sequence.ResumeAsync(kit.Group, new SequenceOptions(HoldsLease: () => ++asked < 2), CancellationToken.None);

        result.Outcome.Should().Be(TransitionOutcome.LeaseLost);
        (await kit.Db.HashGetAsync(kit.StateKey, "cmt")).IsNull.Should().BeTrue();
        (await kit.ControlDb.HashGetAsync(control, "dur")).Should().Be("0");
        (await kit.ControlDb.HashGetAsync(control, "id")).Should().Be(stored.Id, "the epoch sync of the first step did run");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Reassert_refuses_when_a_transition_completes_between_the_state_and_the_record_reads(bool useScripts)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts);
        var init = await kit.InitAsync("a");
        await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(init), StateKit.TargetFrom(init, "S1", "fwd", 0), null, CancellationToken.None);
        var s1 = (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!;
        var racing = RaceDatabase.AfterHashGetAll(
            kit.ControlDb,
            async () => await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(s1), StateKit.TargetFrom(s1, "S3", "fwd", 1), null, CancellationToken.None));
        var raced = new StateRecovery(kit.Store, kit.Sampler, new ControlRecordReader(racing, kit.Names), kit.ControlDb, kit.Sequence, kit.Names, TimeProvider.System);

        var result = await raced.ReassertAsync(kit.Group, "confirmed", null, null, null, CancellationToken.None);

        result.Outcome.Should().Be(RecoveryOutcome.StateChanged);
        (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!.Should().Match<StateRecord>(s => s.State == "S3" && s.Ep == s1.Ep + 1 && s.Durable && s.HoldOp == string.Empty);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Reassert_refuses_records_that_are_older_than_the_state(bool useScripts)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts);
        var init = await kit.InitAsync("a");
        await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(init), StateKit.TargetFrom(init, "S1", "fwd", 0), null, CancellationToken.None);
        var s1 = (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!;
        await kit.Scripts.TransitAsync(kit.StateKey, StateKit.Expecting(s1), StateKit.TargetFrom(s1, "S3", "fwd", 1));

        var result = await kit.Recovery.ReassertAsync(kit.Group, "confirmed", null, null, null, CancellationToken.None);

        result.Outcome.Should().Be(RecoveryOutcome.StateChanged);
        (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!.Should().Match<StateRecord>(s => s.State == "S3" && !s.Durable);
    }

    [Theory]
    [InlineData(true, "vanish")]
    [InlineData(true, "changed")]
    [InlineData(true, "nogate")]
    [InlineData(false, "vanish")]
    [InlineData(false, "changed")]
    [InlineData(false, "nogate")]
    public async Task Recreation_never_turns_a_vanished_changed_or_gateless_record_into_an_empty_managed_set(bool useScripts, string what)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts);
        var control = kit.Names.Control(kit.Group, 0);
        await kit.Scripts.ControlSyncAsync(control, new ControlRecord(40, "recid", "S3", "fwd", "1", "S1", "fwd", "0", "39", "before", what == "nogate" ? [] : [("a", "n")]));
        await kit.Scripts.ControlCommitAsync(control, 40, "recid");
        var db = what switch
        {
            "vanish" => RaceDatabase.Before(kit.ControlDb, () => kit.ControlDb.KeyDelete(control), nameof(IDatabase.HashGetAllAsync), nameof(IDatabase.HashKeysAsync)),
            "changed" => RaceDatabase.Before(kit.ControlDb, () => kit.ControlDb.HashSet(control, "id", "other"), nameof(IDatabase.HashGetAllAsync), nameof(IDatabase.HashKeysAsync)),
            _ => kit.ControlDb,
        };
        var raced = new StateRecovery(kit.Store, kit.Sampler, kit.Records, db, kit.Sequence, kit.Names, TimeProvider.System);

        var result = await raced.RecreateAsync(kit.Group, [new PeerIdentity("S3", "fwd", 40, "recid")], [], null, CancellationToken.None);

        result.Outcome.Should().Be(RecoveryOutcome.NeedsReassert);
        (await kit.Store.ReadAsync(kit.Group, CancellationToken.None)).Should().BeNull();
    }

    private static async Task<(StateRecord Stored, RedisKey Control)> SyncedAsync(StateKit kit, bool sync = true)
    {
        var init = await kit.InitAsync("a");
        await kit.Scripts.TransitAsync(kit.StateKey, StateKit.Expecting(init), StateKit.TargetFrom(init, "S1", "fwd", 0));
        var stored = (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!;
        var control = kit.Names.Control(kit.Group, 0);
        if (sync)
        {
            await kit.Scripts.ControlSyncAsync(control, stored.ToControlRecord([("a", "n")]));
        }
        else
        {
            await kit.Scripts.ControlSyncAsync(control, MoveTestEnvironment.Record(stored.Ep - 1, "older", ("a", "n")));
        }

        return (stored, control);
    }
}
