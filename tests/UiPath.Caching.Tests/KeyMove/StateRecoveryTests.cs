using StackExchange.Redis;
using UiPath.Caching.KeyMove;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests.KeyMove;

[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class StateRecoveryTests(RedisContainerFixture fixture)
{
    private static readonly ClaimFields Claim = new("fmt:1>2", "tok", "tok:9:S0");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_flushed_s0_group_is_recreated_from_the_peers_with_the_registered_caches_and_a_hold(bool useScripts)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts);
        var peer = new PeerIdentity("S0", "none", 7, "peerid");

        var result = await kit.Recovery.RecreateAsync(kit.Group, [peer, peer], ["b", "a"], null, CancellationToken.None);

        result.Should().Match<RecoveryResult>(r => r.Outcome == RecoveryOutcome.Done && r.Transition!.Outcome == TransitionOutcome.Completed);
        var state = (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!;
        state.Should().Match<StateRecord>(s => s.State == "S0" && s.Direction == "none" && s.Ep == 8 && s.Id != "peerid" && s.Durable && s.NPop == 0 && s.MoveId == 8 && s.Caches == "a+b" && s.HoldOp == "recreated");
        state.CacheSet.Should().Be(StateRecord.HashOfCaches("a+b"));
        state.KeepsSourceFallback("a").Should().BeTrue();
        (await kit.ControlAsync(0)).Should().Contain(e => e.Name == "ep" && e.Value == "8").And.Contain(e => e.Name == "dur" && e.Value == "1");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_lost_state_shard_is_recreated_from_the_surviving_records_with_the_set_the_gates_name_and_the_claim_copied(bool useScripts)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts);
        var init = await kit.InitAsync("a", "b");
        var target = StateKit.TargetFrom(init, "S3", "fwd", 1);
        (await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(init), target, null, CancellationToken.None)).Outcome.Should().Be(TransitionOutcome.Completed);
        await kit.Db.KeyDeleteAsync(kit.StateKey);

        var result = await kit.Recovery.RecreateAsync(kit.Group, [new PeerIdentity("S3", "fwd", target.Ep, target.Id)], ["a", "b", "late"], Claim, CancellationToken.None);

        result.Transition!.Outcome.Should().Be(TransitionOutcome.Completed);
        var state = (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!;
        state.Should().Match<StateRecord>(s => s.State == "S3" && s.NPop == 1 && s.Ep == target.Ep + 1 && s.Caches == "a+b" && s.Move == "fmt:1>2" && s.Claim == "tok" && s.Release == "tok:9:S0" && s.HoldOp == "recreated" && s.MoveId == target.Ep + 1);
        state.Prev.Should().Be($"S3:fwd:1:{state.Ep}:{state.Id}", "DURABLE copied the committed state");
        (await kit.ControlAsync(0)).Should().Contain(e => e.Name == "pid" && e.Value == target.Id);
    }

    [Fact]
    public async Task A_reverse_state_derives_npop_one()
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts: true);
        await WriteRecordAsync(kit, 20, "revid", "S1", "rev", "1", dur: true, "a");

        var result = await kit.Recovery.RecreateAsync(kit.Group, [new PeerIdentity("S1", "rev", 20, "revid")], [], null, CancellationToken.None);

        result.Transition!.Outcome.Should().Be(TransitionOutcome.Completed);
        (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!.NPop.Should().Be(1);
    }

    [Fact]
    public async Task Recreation_refuses_without_evidence_or_with_conflicting_evidence()
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts: true);
        var peer = new PeerIdentity("S3", "fwd", 7, "peerid");

        (await kit.Recovery.RecreateAsync(kit.Group, [], ["a"], null, CancellationToken.None)).Outcome.Should().Be(RecoveryOutcome.NoPeers);
        (await kit.Recovery.RecreateAsync(kit.Group, [peer, peer with { Id = "other" }], ["a"], null, CancellationToken.None)).Outcome.Should().Be(RecoveryOutcome.PeersDisagree);
        (await kit.Recovery.RecreateAsync(kit.Group, [peer], ["a"], null, CancellationToken.None)).Outcome.Should().Be(RecoveryOutcome.NeedsReassert, "a flushed S3 has no record to name its caches");

        await WriteRecordAsync(kit, 9, "recid", "S3", "fwd", "1", dur: true, "a");
        (await kit.Recovery.RecreateAsync(kit.Group, [peer], ["a"], null, CancellationToken.None)).Outcome.Should().Be(RecoveryOutcome.RecordsDisagree);
        (await kit.Store.ReadAsync(kit.Group, CancellationToken.None)).Should().BeNull();

        await kit.InitAsync("a");
        (await kit.Recovery.RecreateAsync(kit.Group, [peer], ["a"], null, CancellationToken.None)).Outcome.Should().Be(RecoveryOutcome.StateExists);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Reassert_rebuilds_an_s4_group_from_its_records_keeps_the_source_fallback_on_and_holds(bool useScripts)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts);
        await WriteRecordAsync(kit, 30, "s4id", "S4", "fwd", "1", dur: true, "a", "b");

        var result = await kit.Recovery.ReassertAsync(kit.Group, "operator confirmed", null, null, Claim, CancellationToken.None);

        result.Transition!.Outcome.Should().Be(TransitionOutcome.Completed);
        var state = (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!;
        state.Should().Match<StateRecord>(s => s.State == "S4" && s.Direction == "fwd" && s.NPop == 1 && s.Ep == 31 && s.MoveId == 31 && s.Caches == "a+b" && s.HoldOp == "reassert" && s.Durable && s.Move == "fmt:1>2");
        state.AllAck.Should().BeEmpty();
        state.KeepsSourceFallback("a").Should().BeTrue();
        state.KeepsSourceFallback("b").Should().BeTrue();
        (await kit.ControlAsync(0)).Should().Contain(e => e.Name == "ep" && e.Value == "31").And.Contain(e => e.Name == "pid" && e.Value == "s4id");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Reassert_over_a_rolled_back_hash_replaces_it_and_drops_what_it_must_not_inherit(bool useScripts)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts);
        var init = await kit.InitAsync("a");
        await kit.Db.HashSetAsync(kit.StateKey, [new("res:a", "0"), new("allAck", init.Id), new("lswept", init.Id), new("appr:close", "x"), new("drainedAt", "1"), new("hold:auto", "canary")]);
        await WriteRecordAsync(kit, init.Ep + 100, "newer", "S5", "fwd", "1", dur: true, "a");

        var result = await kit.Recovery.ReassertAsync(kit.Group, "operator confirmed", null, null, null, CancellationToken.None);

        result.Transition!.Outcome.Should().Be(TransitionOutcome.Completed);
        var state = (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!;
        state.Should().Match<StateRecord>(s => s.State == "S5" && s.Ep == init.Ep + 101 && s.Swept == string.Empty && s.AllAck == string.Empty && s.HoldAuto == string.Empty && s.HoldOp == "reassert");
        state.Residue!.Should().BeEmpty();
        state.FieldNames.Should().NotContain(["res:a", "appr:close", "drainedAt"]);
        state.KeepsSourceFallback("a").Should().BeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Reassert_with_the_highest_record_at_dur_zero_takes_the_state_from_its_prev_fields(bool useScripts)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts);
        await kit.Scripts.ControlSyncAsync(kit.Names.Control(kit.Group, 0), new ControlRecord(50, "never-ran", "S3", "fwd", "1", "S1", "fwd", "0", "49", "last-committed", [("a", "n")]));

        var result = await kit.Recovery.ReassertAsync(kit.Group, "operator confirmed", null, null, null, CancellationToken.None);

        result.Transition!.Outcome.Should().Be(TransitionOutcome.Completed);
        (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!.Should().Match<StateRecord>(s => s.State == "S1" && s.NPop == 0 && s.Ep == 51);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Reassert_after_a_whole_redis_flush_needs_the_operator_to_name_the_state_and_the_caches(bool useScripts)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts);

        (await kit.Recovery.ReassertAsync(kit.Group, "confirmed", null, null, null, CancellationToken.None)).Outcome.Should().Be(RecoveryOutcome.NoRecords);
        (await kit.Recovery.ReassertAsync(kit.Group, "confirmed", ("S5", "fwd"), null, null, CancellationToken.None)).Outcome.Should().Be(RecoveryOutcome.NoRecords);
        var micros = await kit.Store.ServerMicrosecondsAsync(kit.Group);

        var result = await kit.Recovery.ReassertAsync(kit.Group, "confirmed", ("S5", "fwd"), ["a", "b"], Claim, CancellationToken.None);

        result.Transition!.Outcome.Should().Be(TransitionOutcome.Completed);
        (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!.Should().Match<StateRecord>(s => s.State == "S5" && s.NPop == 1 && s.Ep > micros && s.Caches == "a+b" && s.HoldOp == "reassert" && s.Claim == "tok");
    }

    [Fact]
    public async Task Reassert_needs_a_confirmation()
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts: true);
        var blank = () => kit.Recovery.ReassertAsync(kit.Group, " ", null, null, null, CancellationToken.None);
        await blank.Should().ThrowAsync<ArgumentException>();
        (await kit.Store.ReadAsync(kit.Group, CancellationToken.None)).Should().BeNull();
    }

    private static async Task WriteRecordAsync(StateKit kit, long ep, string id, string state, string direction, string np, bool dur, params string[] caches)
    {
        var control = kit.Names.Control(kit.Group, 0);
        await kit.Scripts.ControlSyncAsync(control, new ControlRecord(ep, id, state, direction, np, "S1", "fwd", "0", (ep - 1).ToString(System.Globalization.CultureInfo.InvariantCulture), "before", [.. caches.Select(c => (c, "n"))]));
        if (dur)
        {
            await kit.Scripts.ControlCommitAsync(control, ep, id);
        }
    }
}
