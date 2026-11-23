using System.Net;
using StackExchange.Redis;
using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Tests.KeyMove;

[Collection("TwoServers")]
[Trait("Category", "Integration")]
public sealed class ServerRecordWriterTests(TwoServerFixture fixture)
{
    [Fact]
    public async Task A_queued_transit_writes_the_state_and_a_repeat_answers_2_without_being_the_first_code()
    {
        await using var kit = await TwoServerKit.CreateAsync(fixture);
        var seeded = await kit.SeedAsync();
        var target = TwoServerKit.Target(seeded);
        var op = MoveScripts.TransitOp(kit.StateKey, StateExpectation.Holding(seeded.Ep, seeded.Id, 1), target, null);

        var first = await kit.DstState.RunAsync(op, wayBack: false);
        var second = await kit.DstState.RunAsync(op, wayBack: false);

        first.Should().Match<RecordWrite>(w => w.Outcome == BarrierOutcome.NoReplica && w.Code == MoveScripts.Written && w.Uncertain == null);
        first.Proof.Should().NotBeNull("a confirmed write carries the proof of the connection it ran on");
        second.Code.Should().Be(MoveScripts.Repeated);
        (await kit.ReadStateAsync())!.Id.Should().Be(target.Id);
    }

    [Fact]
    public async Task A_queued_ctlsync_answers_1_then_0_for_a_repeat_and_the_caller_verifies_by_read()
    {
        await using var kit = await TwoServerKit.CreateAsync(fixture);
        var op = MoveScripts.ControlSyncOp(kit.DstRecord, Record(5, "id5"));

        (await kit.DstMaster.RunAsync(op, false)).Code.Should().Be(MoveScripts.Written);
        (await kit.DstMaster.RunAsync(op, false)).Code.Should().Be(MoveScripts.AlreadyWrittenOrOlder);
        (await kit.RecordAsync(RecordSide.Dst))["id"].Should().Be("id5");
    }

    [Fact]
    public async Task A_write_that_the_barrier_repeats_reports_the_attempts_so_a_repeats_code_is_not_taken_for_the_first()
    {
        await using var kit = await TwoServerKit.CreateAsync(fixture);
        kit.Cuts.Add(new TwoServerKit.Cut(RecordSide.Dst, "CTLSYNC", TwoServerKit.CutMode.Down, times: 1));

        var write = await kit.DstMaster.RunAsync(MoveScripts.ControlSyncOp(kit.DstRecord, Record(5, "id5")), false);

        write.Attempts.Should().Be(2);
        write.Code.Should().Be(MoveScripts.Written, "the first attempt applied nothing");
    }

    [Fact]
    public async Task An_exec_that_lost_its_reply_is_reported_as_uncertain_and_never_sent_again()
    {
        await using var kit = await TwoServerKit.CreateAsync(fixture);
        var cut = new TwoServerKit.Cut(RecordSide.Dst, "CTLSYNC", TwoServerKit.CutMode.Lost);
        kit.Cuts.Add(cut);

        var write = await kit.DstMaster.RunAsync(MoveScripts.ControlSyncOp(kit.DstRecord, Record(5, "id5")), false);

        write.Outcome.Should().Be(BarrierOutcome.NotConfirmed);
        write.Uncertain.Should().NotBeNull();
        write.Attempts.Should().Be(1);
        cut.Fired.Should().Be(1);
        (await kit.RecordAsync(RecordSide.Dst))["id"].Should().Be("id5", "the write applied");
    }

    [Fact]
    public async Task Several_records_are_written_in_transactions_of_their_own_with_one_proof_and_the_lowest_code_wins()
    {
        await using var kit = await TwoServerKit.CreateAsync(fixture);
        var keys = Enumerable.Range(0, 4).Select(i => (RedisKey)$"{kit.LibraryPrefix}mg:rec{i}").ToArray();
        await kit.RawDst.HashSetAsync(keys[2], [new("ep", 5), new("id", "other")]);

        var write = await kit.DstMaster.RunManyAsync([.. keys.Select(k => MoveScripts.ControlSyncOp(k, Record(5, "id5")))], false);

        write.Code.Should().Be(MoveScripts.IdentityConflict, "-5 of one record is the lowest");
        (await kit.RawDst.HashGetAsync(keys[0], "id")).ToString().Should().Be("id5");
        (await kit.RawDst.HashGetAsync(keys[3], "id")).ToString().Should().Be("id5");
        kit.Trace.Count(t => t == "dst:ctlsync").Should().Be(4);
    }

    [Fact]
    public async Task A_side_without_scripts_takes_the_condition_form_and_the_same_codes()
    {
        await using var kit = await TwoServerKit.CreateAsync(fixture, new TwoServerKit.KitOptions(DstScripts: false));
        var seeded = await kit.SeedAsync();
        var target = TwoServerKit.Target(seeded);
        var transit = MoveScripts.TransitOp(kit.StateKey, StateExpectation.Holding(seeded.Ep, seeded.Id, 1), target, null);

        (await kit.DstState.RunAsync(transit, false)).Code.Should().Be(MoveScripts.Written);
        (await kit.DstState.RunAsync(transit, false)).Code.Should().Be(MoveScripts.Repeated);
        var stale = MoveScripts.TransitOp(kit.StateKey, StateExpectation.Holding(seeded.Ep, seeded.Id, 1), TwoServerKit.Target(seeded), null);
        (await kit.DstState.RunAsync(stale, false)).Code.Should().Be(MoveScripts.ExpectationFailed, "the stored state is no longer the one replaced, and nothing is written");
        (await kit.ReadStateAsync())!.Id.Should().Be(target.Id);
        kit.Trace.Should().BeEmpty("no script ran");

        var sync = MoveScripts.ControlSyncOp(kit.DstRecord, Record(7, "id7"));
        (await kit.DstMaster.RunAsync(sync, false)).Code.Should().Be(MoveScripts.Written);
        (await kit.DstMaster.RunAsync(sync, false)).Code.Should().Be(MoveScripts.AlreadyWrittenOrOlder);
        (await kit.DstMaster.RunAsync(MoveScripts.ControlSyncOp(kit.DstRecord, Record(7, "x")), false)).Code.Should().Be(MoveScripts.IdentityConflict);
        (await kit.DstMaster.RunAsync(MoveScripts.ControlCommitOp(kit.DstRecord, 7, "id7"), false)).Code.Should().Be(MoveScripts.Written);
        (await kit.RecordAsync(RecordSide.Dst))["dur"].Should().Be("1");
    }

    [Fact]
    public async Task The_condition_form_reads_back_after_a_result_that_did_not_confirm_and_reports_the_applied_write()
    {
        await using var kit = await TwoServerKit.CreateAsync(fixture, new TwoServerKit.KitOptions(DstScripts: false, DstReplicaFree: false));

        var write = await kit.DstMaster.RunAsync(MoveScripts.ControlSyncOp(kit.DstRecord, Record(5, "id5")), false);

        write.Outcome.Should().Be(BarrierOutcome.NotConfirmed, "a master with no replica never confirms when replicas are expected");
        write.Code.Should().Be(MoveScripts.Written, "the record shows the write");
    }

    [Fact]
    public async Task The_condition_form_plans_again_when_the_record_changed_between_the_read_and_the_write()
    {
        await using var kit = await TwoServerKit.CreateAsync(fixture, new TwoServerKit.KitOptions(DstScripts: false));
        var reads = 0;
        var writer = Plain(kit, key => Disturb(kit, key, () => reads++ == 0 ? new HashEntry[] { new("ep", 1), new("id", "old") } : null));

        // The first read sees ep 1, then the record moves to ep 2: the conditions of that plan fail, and the next plan is made from what is there.
        var write = await writer.RunAsync(MoveScripts.ControlSyncOp(kit.DstRecord, Record(5, "id5")), false);

        write.Code.Should().Be(MoveScripts.Written);
        write.Confirmed.Should().BeTrue();
        (await kit.RecordAsync(RecordSide.Dst))["id"].Should().Be("id5");
        reads.Should().BeGreaterThan(1);
    }

    [Fact]
    public async Task The_condition_form_stops_at_the_watch_abort_limit_when_the_record_never_holds_still()
    {
        await using var kit = await TwoServerKit.CreateAsync(fixture, new TwoServerKit.KitOptions(DstScripts: false));
        var reads = 0;
        var writer = Plain(kit, key => Disturb(kit, key, () => new HashEntry[] { new("ep", 1 + Interlocked.Increment(ref reads)), new("id", "old") }));

        var write = await writer.RunAsync(MoveScripts.ControlSyncOp(kit.DstRecord, Record(500, "id500")), false);

        write.Code.Should().Be(RecordWrite.NoCode);
        write.Attempts.Should().Be(MoveScripts.MaxWatchAborts);
        write.Confirmed.Should().BeFalse();
    }

    [Fact]
    public async Task The_condition_form_of_a_fenced_write_is_refused_when_the_lease_is_not_held_and_writes_nothing()
    {
        await using var kit = await TwoServerKit.CreateAsync(fixture, new TwoServerKit.KitOptions(DstScripts: false));
        var seeded = await kit.SeedAsync();
        var fence = new LeaseFence(kit.Placement.Lead(kit.Group).Key, "token-a");
        await kit.RawDst.StringSetAsync(fence.Key, "token-b");

        var write = await kit.DstState.RunAsync(MoveScripts.CommitBeginOp(kit.StateKey, seeded.Id, fence), false);

        write.Code.Should().Be(MoveScripts.LeaseNotHeld);
        (await kit.ReadStateAsync())!.Field(StateFields.CommitBegun).Should().BeEmpty();
    }

    [Fact]
    public async Task The_condition_form_reports_no_code_for_a_write_the_read_back_does_not_show()
    {
        await using var kit = await TwoServerKit.CreateAsync(fixture, new TwoServerKit.KitOptions(DstScripts: false));
        var seeded = await kit.SeedAsync();
        var fence = new LeaseFence(kit.Placement.Lead(kit.Group).Key, "token-a");
        await kit.RawDst.StringSetAsync(fence.Key, "token-b");
        var writer = Plain(kit, key => kit.RawDst.HashGetAllAsync(key, CommandFlags.DemandMaster));

        // The lease is not asked for, so the plan is made; the fence condition fails on the server and nothing is written.
        var write = await writer.RunAsync(MoveScripts.CommitBeginOp(kit.StateKey, seeded.Id, fence), false);

        write.Confirmed.Should().BeFalse();
        write.Code.Should().Be(RecordWrite.NoCode, "the planned code would claim a write that never happened");
        (await kit.ReadStateAsync())!.Field(StateFields.CommitBegun).Should().BeEmpty();
    }

    private static ControlRecord Record(long ep, string id) => new(ep, id, "S1", "fwd", "0", "S0", "none", "0", "1", "prev", [("c1", "n1")]);

    private static ServerRecordWriter Plain(TwoServerKit kit, Func<RedisKey, Task<HashEntry[]>> read)
    {
        var master = new MasterCaps(new DnsEndPoint("m", 6379), new Version(7, 4), false, true, true, true, null, "noeviction");
        var caps = new SideCaps([master], ScanMode.PerShard, false, [], []);
        return new ServerRecordWriter(new ServerBarrier(kit.DstDb, kit.DstRecord, TimeProvider.System, replicaFree: true) { Retries = 0 }, caps, read);
    }

    // Reads the record, then replaces it with what the change gives (when it gives something): a write that raced the read.
    private static async Task<HashEntry[]> Disturb(TwoServerKit kit, RedisKey key, Func<HashEntry[]?> change)
    {
        var read = await kit.RawDst.HashGetAllAsync(key, CommandFlags.DemandMaster);
        if (change() is { } entries)
        {
            await kit.RawDst.KeyDeleteAsync(key);
            await kit.RawDst.HashSetAsync(key, entries);
        }

        return read;
    }
}
