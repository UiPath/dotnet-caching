using StackExchange.Redis;
using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Tests.KeyMove;

[Trait("Category", "Integration")]
public class StateClusterTests
{
    private static readonly KeyMoveStateOptions WithReplica = new() { ExpectedReplicasPerMaster = 1, BarrierWait = TimeSpan.FromMilliseconds(50) };

    public static TheoryData<bool, string> Runs() => new()
    {
        { true, string.Empty },
        { true, "app:" },
        { true, "{shard}:" },
        { false, string.Empty },
    };

    [Theory]
    [MemberData(nameof(Runs))]
    public async Task A_run_syncs_and_commits_the_control_record_of_every_slot_of_the_set_on_every_master(bool useScripts, string prefix)
    {
        await using var kit = await StateKit.ClusterAsync(useScripts, prefix: prefix);
        var init = await kit.InitAsync("a", "b");
        var target = StateKit.TargetFrom(init, "S1", "fwd", 0);

        var result = await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(init), target, null, CancellationToken.None);

        result.Should().Be(new TransitionResult(TransitionOutcome.Completed));
        var slots = kit.Names.SlotSet();
        slots.Count.Should().Be(prefix == "{shard}:" ? 1 : 16384);
        await AssertEveryRecordAsync(kit, slots, target, durable: true);
        (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!.Durable.Should().BeTrue();
        kit.Reports.Count(r => r.Kind == "no_replica").Should().BeInRange(1, 3);
    }

    [Theory]
    [InlineData("epsync")]
    [InlineData("commit")]
    public async Task A_run_that_stopped_with_half_of_the_slots_written_is_resumed_on_every_slot(string stoppedIn)
    {
        await using var kit = await StateKit.ClusterAsync(useScripts: true);
        var init = await kit.InitAsync("a");
        var target = StateKit.TargetFrom(init, "S3", "fwd", 1);
        await kit.Scripts.TransitAsync(kit.StateKey, StateKit.Expecting(init), target);
        var stored = (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!;
        var record = stored.ToControlRecord([("a", "n-before")]);
        var slots = kit.Names.SlotSet();
        var half = slots.Where(s => s % 2 == 0).ToArray();
        await Task.WhenAll(half.Select(s => kit.Scripts.ControlSyncAsync(kit.Names.Control(kit.Group, s), record)));
        if (stoppedIn == "commit")
        {
            await Task.WhenAll(slots.Where(s => s % 4 == 0).Select(s => kit.Scripts.ControlCommitAsync(kit.Names.Control(kit.Group, s), stored.Ep, stored.Id)));
        }

        var result = await kit.Sequence.ResumeAsync(kit.Group, null, CancellationToken.None);

        result.Outcome.Should().Be(TransitionOutcome.Completed);
        await AssertEveryRecordAsync(kit, slots, target, durable: true);
        (await kit.ControlAsync(half[0])).Should().Contain(e => e.Name == "c:a" && e.Value == "OPEN:n-before");
    }

    [Fact]
    public async Task A_slot_map_that_changed_during_the_barrier_has_the_moved_slots_issued_and_confirmed_on_their_new_owner()
    {
        await using var kit = await StateKit.ClusterAsync(useScripts: true, WithReplica);
        var init = await kit.InitAsync("a");
        var lie = true;

        // Until the first barrier of the control records answers, the topology claims the second master owns the first master's slots too.
        var waits = new List<string>();
        kit.Topology.Layout = (real, _) => lie ? Merged(real) : real;
        kit.Topology.Wait = (endpoint, _) =>
        {
            lock (waits)
            {
                waits.Add(endpoint.ToString()!);
                lie = waits.Count < 2;
            }

            return 1;
        };
        var target = StateKit.TargetFrom(init, "S1", "fwd", 0);
        var result = await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(init), target, null, CancellationToken.None);

        result.Should().Be(new TransitionResult(TransitionOutcome.Completed));
        var names = (await kit.Real.ReadLayoutAsync(CancellationToken.None)).Masters.Select(m => m.Endpoint.ToString()!).Order(StringComparer.Ordinal).ToArray();
        waits.Skip(1).Take(2).Should().BeEquivalentTo(names.Skip(1), "the claimed owners are confirmed first");
        waits[3].Should().Be(names[0], "the slots that moved are confirmed again on their true owner");
        await AssertEveryRecordAsync(kit, kit.Names.SlotSet(), target, durable: true);
    }

    [Fact]
    public async Task A_control_write_that_the_dedicated_connection_sent_to_another_master_is_issued_and_confirmed_on_the_owner()
    {
        await using var kit = await StateKit.ClusterAsync(useScripts: true, WithReplica);
        var init = await kit.InitAsync("a");
        var masters = (await kit.Real.ReadLayoutAsync(CancellationToken.None)).Masters.Select(m => m.Endpoint).OrderBy(e => e.ToString(), StringComparer.Ordinal).ToArray();
        var (liar, other) = (masters[0], masters[1]);
        var waits = new List<string>();
        kit.Topology.Wait = (endpoint, _) =>
        {
            lock (waits)
            {
                waits.Add(endpoint.ToString()!);
            }

            return 1;
        };

        // Every key of the first master has moved to the second, as far as both connections can tell.
        kit.Topology.ControlOwner = (_, key, _) => kit.Db.IdentifyEndpoint(key)?.Equals(liar) == true ? other : null;
        var target = StateKit.TargetFrom(init, "S1", "fwd", 0);
        var result = await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(init), target, null, CancellationToken.None);

        result.Outcome.Should().Be(TransitionOutcome.Completed);

        // Each of the two phases waits on the second master twice: for its own slots, then for the first master's.
        waits.Count(w => w == other.ToString()).Should().BeGreaterThanOrEqualTo(4, "the slots of the first master were confirmed on the second");
        await AssertEveryRecordAsync(kit, kit.Names.SlotSet(), target, durable: true);
    }

    [Fact]
    public async Task A_state_write_that_the_dedicated_connection_sent_to_another_master_is_issued_again_and_waited_for_on_the_owner()
    {
        await using var kit = await StateKit.ClusterAsync(useScripts: true, WithReplica);
        var init = await kit.InitAsync("a");
        var owner = (await kit.Db.IdentifyEndpointAsync(kit.StateKey))!;
        var other = (await kit.Real.ReadLayoutAsync(CancellationToken.None)).Masters.Select(m => m.Endpoint).First(e => !e.Equals(owner));
        var waits = new List<System.Net.EndPoint>();
        kit.Topology.Wait = (endpoint, _) =>
        {
            lock (waits)
            {
                waits.Add(endpoint);
            }

            return 1;
        };

        // The first TRANSIT is seen going to the wrong master (the owner check before and after it); the next check says the true owner.
        var checks = 0;
        kit.Topology.StateOwner = (endpoint, _, n) =>
        {
            checks = n;
            return n <= 2 && endpoint.Equals(owner) ? other : null;
        };
        var result = await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(init), StateKit.TargetFrom(init, "S1", "fwd", 0), null, CancellationToken.None);

        result.Outcome.Should().Be(TransitionOutcome.Completed);

        // Hop 1 on the owner is misdirected, hop 2 on the claimed owner finds the truth (the owner), hop 3 is confirmed: no WAIT follows a misdirected write.
        checks.Should().BeGreaterThanOrEqualTo(6);
        waits[0].Should().Be(owner);
        (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!.Durable.Should().BeTrue();
    }

    [Fact]
    public async Task A_run_whose_topology_never_settles_on_the_owner_of_a_record_is_stuck_not_confirmed()
    {
        await using var kit = await StateKit.ClusterAsync(useScripts: true, WithReplica);
        var init = await kit.InitAsync("a");
        var readings = 0;

        // The record of every slot of the first master is deleted right after each barrier, so no verification ever finds it.
        kit.Topology.Wait = (_, n) =>
        {
            if (n > 1)
            {
                Interlocked.Increment(ref readings);
                foreach (var (_, key) in kit.Names.ControlRecords(kit.Group).Take(64))
                {
                    kit.ControlDb.KeyDelete(key);
                }
            }

            return 1;
        };
        var result = await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(init), StateKit.TargetFrom(init, "S1", "fwd", 0), new SequenceOptions(SkipBarriers: true), CancellationToken.None);

        result.Outcome.Should().Be(TransitionOutcome.BarrierStuck);
        readings.Should().BeGreaterThan(0);
        (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!.Durable.Should().BeFalse();
    }

    [Fact]
    public async Task The_sample_covers_every_master_so_a_record_that_only_one_master_holds_is_found()
    {
        await using var kit = await StateKit.ClusterAsync(useScripts: true);
        var layout = await kit.Real.ReadLayoutAsync(CancellationToken.None);
        layout.Masters.Count.Should().BeGreaterThan(1);
        var small = layout.Masters[^1];
        var held = Enumerable.Range(0, 16384).Where(s => layout.OwnerOf(s).Equals(small.Endpoint)).ToArray();
        await Task.WhenAll(held.Select(s => kit.Scripts.ControlSyncAsync(kit.Names.Control(kit.Group, s), MoveTestEnvironment.Record(41, "idz", ("a", "n")))));

        var sample = await kit.Sampler.SampleAsync(kit.Group, CancellationToken.None);

        sample.Should().Match<ControlSample>(s => s.Ep == 41 && s.Id == "idz");
        (await kit.Sampler.ProbeAsync(kit.Group, CancellationToken.None)).Should().Be(layout.OwnerOf(kit.Names.SlotSet()[0]).Equals(small.Endpoint) ? sample with { Slot = -1 } : null);
    }

    [Fact]
    public async Task Init_after_a_sample_takes_an_epoch_above_the_highest_record_of_any_master()
    {
        await using var kit = await StateKit.ClusterAsync(useScripts: true);
        var layout = await kit.Real.ReadLayoutAsync(CancellationToken.None);
        var high = long.MaxValue / 4;
        var held = Enumerable.Range(0, 16384).Where(s => layout.OwnerOf(s).Equals(layout.Masters[1].Endpoint)).Take(2000).ToArray();
        await Task.WhenAll(held.Select(s => kit.Scripts.ControlSyncAsync(kit.Names.Control(kit.Group, s), MoveTestEnvironment.Record(high, "idz", ("a", "n")))));

        var result = await kit.Store.InitAsync(kit.Group, ["a"], CancellationToken.None);

        result.Ep.Should().Be(high + 1);
    }

    [Fact]
    public async Task Upkeep_finds_one_missing_record_among_every_slot_and_the_rebuild_restores_all_of_them()
    {
        await using var kit = await StateKit.ClusterAsync(useScripts: true);
        var init = await kit.InitAsync("a");
        var target = StateKit.TargetFrom(init, "S1", "fwd", 0);
        await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(init), target, null, CancellationToken.None);
        var state = (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!;
        (await kit.Upkeep.CheckAsync(kit.Group, state, CancellationToken.None)).Should().Be(UpkeepVerdict.Healthy);
        await kit.ControlDb.KeyDeleteAsync(kit.Names.Control(kit.Group, 9001));

        (await kit.Upkeep.CheckAsync(kit.Group, state, CancellationToken.None)).Should().Be(UpkeepVerdict.Rebuild);
        var result = await kit.Upkeep.RebuildAsync(kit.Group, state, null, null, CancellationToken.None);

        result.Outcome.Should().Be(TransitionOutcome.Completed);
        var rebuilt = (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!;
        rebuilt.Ep.Should().Be(state.Ep + 1);
        (await kit.Upkeep.CheckAsync(kit.Group, rebuilt, CancellationToken.None)).Should().Be(UpkeepVerdict.Healthy);
    }

    [Fact]
    public async Task Reassert_reads_every_record_and_refuses_committed_records_that_disagree_on_direction()
    {
        await using var kit = await StateKit.ClusterAsync(useScripts: true);
        static ControlRecord Record(string dir) => new(70, "same", "S3", dir, "1", "S1", "fwd", "0", "69", "before", [("a", "n")]);

        await kit.Scripts.ControlSyncAsync(kit.Names.Control(kit.Group, 5), Record("fwd"));
        await kit.Scripts.ControlCommitAsync(kit.Names.Control(kit.Group, 5), 70, "same");
        await kit.Scripts.ControlSyncAsync(kit.Names.Control(kit.Group, 12000), Record("rev"));
        await kit.Scripts.ControlCommitAsync(kit.Names.Control(kit.Group, 12000), 70, "same");

        var result = await kit.Recovery.ReassertAsync(kit.Group, "confirmed", null, null, null, CancellationToken.None);

        result.Outcome.Should().Be(RecoveryOutcome.RecordsDisagreeOnDirection);
        (await kit.Store.ReadAsync(kit.Group, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Reassert_after_a_lost_state_shard_rewrites_every_slot_and_a_node_restart_sees_the_rollback_through_the_smallest_master()
    {
        await using var kit = await StateKit.ClusterAsync(useScripts: true);
        var init = await kit.InitAsync("a");
        var target = StateKit.TargetFrom(init, "S3", "fwd", 1);
        await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(init), target, null, CancellationToken.None);
        await kit.Db.KeyDeleteAsync(kit.StateKey);

        var result = await kit.Recovery.ReassertAsync(kit.Group, "confirmed", null, null, null, CancellationToken.None);

        result.Transition!.Outcome.Should().Be(TransitionOutcome.Completed);
        var state = (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!;
        state.Should().Match<StateRecord>(s => s.State == "S3" && s.NPop == 1 && s.Ep == target.Ep + 1 && s.HoldOp == "reassert");
        await AssertEveryRecordAsync(kit, kit.Names.SlotSet(), new StateTarget("S3", "fwd", 1, state.Ep, state.Id, string.Empty, 0), durable: true);
    }

    [Fact]
    public async Task A_restart_finds_a_rolled_back_hash_from_the_record_of_one_master_alone()
    {
        await using var kit = await StateKit.ClusterAsync(useScripts: true);
        var init = await kit.InitAsync("a");
        var layout = await kit.Real.ReadLayoutAsync(CancellationToken.None);
        var small = layout.Masters[^1];
        var held = Enumerable.Range(0, 16384).Where(s => layout.OwnerOf(s).Equals(small.Endpoint)).ToArray();
        await Task.WhenAll(held.Select(s => kit.Scripts.ControlSyncAsync(kit.Names.Control(kit.Group, s), MoveTestEnvironment.Record(init.Ep + 5, "newer", ("a", "n")))));
        var monitor = new GroupStateMonitor(kit.Store, kit.Options, TimeProvider.System);
        monitor.Register([new GroupRegistration(kit.Group, false, true)]);

        await monitor.StepAsync(kit.Group, CancellationToken.None);

        monitor.RulesFor(kit.Group).Should().Match<GroupRules>(r => r.Writes == WriteRule.FailClosed && r.Alerts.Contains("identity_conflict") && r.Acting == null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Records_at_the_highest_epoch_under_different_ids_on_two_masters_are_a_conflict_whichever_one_is_read_first(bool swap)
    {
        await using var kit = await StateKit.ClusterAsync(useScripts: true);
        var layout = await kit.Real.ReadLayoutAsync(CancellationToken.None);
        var (a, b) = (layout.Masters[0], layout.Masters[1]);
        var ids = swap ? ("idY", "idX") : ("idX", "idY");
        await WriteMasterAsync(kit, layout, a, 60, ids.Item1, durable: false);
        await WriteMasterAsync(kit, layout, b, 60, ids.Item2, durable: false);

        var sample = await kit.Sampler.SampleAsync(kit.Group, CancellationToken.None);
        sample!.Conflicting.Should().BeTrue();
        foreach (var peerId in new[] { "idX", "idY" })
        {
            var result = await kit.Recovery.RecreateAsync(kit.Group, [new PeerIdentity("S3", "fwd", 60, peerId)], [], null, CancellationToken.None);
            result.Outcome.Should().Be(RecoveryOutcome.RecordsDisagree, peerId);
        }

        (await kit.Store.ReadAsync(kit.Group, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task A_committed_record_on_one_master_counts_even_when_another_master_shows_the_same_epoch_uncommitted()
    {
        await using var kit = await StateKit.ClusterAsync(useScripts: true);
        var layout = await kit.Real.ReadLayoutAsync(CancellationToken.None);
        await WriteMasterAsync(kit, layout, layout.Masters[0], 61, "same", durable: false);
        await WriteMasterAsync(kit, layout, layout.Masters[1], 61, "same", durable: true);
        var init = await kit.InitAsync("a");
        await kit.Db.HashSetAsync(kit.StateKey, [new("ep", 61), new("id", "same"), new("dur", "0"), new("st", "S3"), new("dir", "fwd")]);
        var monitor = new GroupStateMonitor(kit.Store, kit.Options, TimeProvider.System);
        monitor.Register([new GroupRegistration(kit.Group, false, true)]);

        var sample = await kit.Sampler.SampleAsync(kit.Group, CancellationToken.None);
        await monitor.StepAsync(kit.Group, CancellationToken.None);

        sample.Should().Match<ControlSample>(s => s.Durable && !s.Conflicting && s.Ep == 61);
        monitor.RulesFor(kit.Group).Alerts.Should().ContainSingle().Which.Should().Be("commit_mismatch");
        init.Should().NotBeNull();
    }

    private static async Task WriteMasterAsync(StateKit kit, ClusterLayout layout, MasterLayout master, long ep, string id, bool durable)
    {
        var slots = Enumerable.Range(0, 16384).Where(s => layout.OwnerOf(s).Equals(master.Endpoint)).ToArray();
        await Task.WhenAll(slots.Select(s => kit.Scripts.ControlSyncAsync(kit.Names.Control(kit.Group, s), new ControlRecord(ep, id, "S3", "fwd", "1", "S1", "fwd", "0", "59", "before", [("a", "n")]))));
        if (durable)
        {
            await Task.WhenAll(slots.Select(s => kit.Scripts.ControlCommitAsync(kit.Names.Control(kit.Group, s), ep, id)));
        }
    }

    private static ClusterLayout Merged(ClusterLayout real)
    {
        var masters = real.Masters.OrderBy(m => m.Endpoint.ToString(), StringComparer.Ordinal).ToArray();
        return new ClusterLayout(
        [
            masters[0] with { Slots = [] },
            masters[1] with { Slots = [.. masters[0].Slots, .. masters[1].Slots] },
            .. masters.Skip(2),
        ]);
    }

    private static async Task AssertEveryRecordAsync(StateKit kit, IReadOnlyList<int> slots, StateTarget target, bool durable)
    {
        var reads = slots.Select(s => kit.ControlDb.HashGetAsync(kit.Names.Control(kit.Group, s), [StateFields.Ep, StateFields.Id, StateFields.Durable])).ToArray();
        await Task.WhenAll(reads);
        foreach (var read in reads)
        {
            var held = read.Result;
            held[0].Should().Be(target.Ep);
            held[1].Should().Be(target.Id);
            held[2].Should().Be(durable ? "1" : "0");
        }
    }
}
