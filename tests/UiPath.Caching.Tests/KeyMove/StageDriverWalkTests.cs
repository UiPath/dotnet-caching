using UiPath.Caching.KeyMove;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests.KeyMove;

// The leader's loop over a real state store.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class StageDriverWalkTests(RedisContainerFixture fixture)
{
    [Fact]
    public async Task A_jump_from_2_to_5_walks_S1_and_S3_waits_for_drained_then_S4_then_waits_for_the_consent_then_closes()
    {
        await using var kit = await DriverKit.CreateAsync(fixture);
        await kit.PrepareAsync();

        var ready = await kit.StepUntilAsync("S1");
        ready.MoveId.Should().Be(ready.Ep, "X1 writes moveId");

        var moving = await kit.StepUntilAsync("S3");
        moving.NPop.Should().Be(1);
        kit.Data.Calls.Should().Contain("count");
        await kit.StepAsync();
        (await kit.StateAsync()).AllAck.Should().Be(moving.Id, "every fresh node acknowledged S3, which the leader stores once");
        for (var n = 0; n < 5; n++)
        {
            await kit.StepAsync();
        }

        (await kit.StateAsync()).State.Should().Be("S3", "DRAINED does not hold yet");
        kit.Alerts.Should().Contain(("stage_pending", "drained:drain.confirming_pass,drain.skipped_accounted"));

        kit.Mover.Report = FakeMoverStatus.Drained(0, ("a", 0));
        kit.Mover.Inventory = new Dictionary<string, long> { ["a"] = 0 };
        var drained = await kit.StepUntilAsync("S4");
        drained.Should().Match<StateRecord>(s => s.Residue!["a"] == 0 && s.Field(StateFields.DrainedAt).Length > 0);

        for (var n = 0; n < 6; n++)
        {
            await kit.StepAsync(TimeSpan.FromSeconds(30));
        }

        (await kit.StateAsync()).State.Should().Be("S4", "X5 needs the stored consent");
        kit.Alerts.Should().Contain(("close_pending", "close.consent:no_consent"));

        (await kit.Commands.ApproveAsync(kit.Group, ApprovalKinds.Close, "residue acknowledged, old code deployed", "op", CancellationToken.None)).Should().Be(ApproveResult.Stored);
        var closed = await kit.StepUntilAsync("S5", advance: TimeSpan.FromSeconds(30));
        closed.Field(StateFields.ClosedAt).Should().NotBeEmpty();
    }

    [Fact]
    public async Task X1_tells_the_facts_that_a_start_was_taken_once_and_no_other_step_does()
    {
        await using var kit = await DriverKit.CreateAsync(fixture);
        await kit.PrepareAsync();
        kit.Facts.Started.Should().BeEmpty();

        kit.Facts.Evidence.AddRange(["f:e1", "n:e2"]);
        await kit.StepUntilAsync("S1");
        var s3 = await kit.StepUntilAsync("S3");

        kit.Facts.Started.Should().Equal(kit.Group);
        s3.Field(StateFields.Attested).Should().Be("f:e1,n:e2", "the Start recorded the evidence it consumed, fenced, in the state");
    }

    [Fact]
    public async Task The_acknowledgement_of_S3_stores_the_time_of_S3_with_it_and_the_acknowledgement_of_any_other_state_stores_none()
    {
        await using var kit = await DriverKit.CreateAsync(fixture);
        await kit.PrepareAsync();
        var ready = await kit.StepUntilAsync("S1");
        await kit.StepAsync();
        var acked = await kit.StateAsync();
        acked.AllAck.Should().Be(ready.Id);
        acked.Field(StateFields.T3At).Should().BeEmpty("S1 is not S3");

        var moving = await kit.StepUntilAsync("S3");
        await kit.StepAsync();

        var s3 = await kit.StateAsync();
        s3.AllAck.Should().Be(moving.Id);
        long.Parse(s3.Field(StateFields.T3At), System.Globalization.CultureInfo.InvariantCulture).Should().BeGreaterThan(0, "the time of the S3 acknowledgement is stored with it");
    }

    [Fact]
    public async Task A_group_with_a_NoOrigin_cache_visits_S2_and_X3_waits_for_every_fresh_node_to_ack_the_barrier()
    {
        await using var kit = await DriverKit.CreateAsync(fixture);
        kit.Facts.NoOrigin = true;
        await kit.PrepareAsync();
        await kit.StepUntilAsync("S1");

        kit.Fleet.Nodes[1].Acks = false;
        for (var n = 0; n < 6; n++)
        {
            await kit.StepAsync();
        }

        (await kit.StateAsync()).State.Should().Be("S1", "n2 has not acked S1, so there is no S2");
        kit.Alerts.Should().Contain(("stage_pending", "ack:n2"));
        kit.Fleet.Nodes[1].Acks = true;
        await kit.StepUntilAsync("S2");

        kit.Fleet.Nodes[0].Acks = false;
        for (var n = 0; n < 6; n++)
        {
            await kit.StepAsync();
        }

        (await kit.StateAsync()).State.Should().Be("S2", "the S2 barrier waits for the ack of the freeze identity");
        kit.Fleet.Nodes[0].Acks = true;
        (await kit.StepUntilAsync("S3")).NPop.Should().Be(1);
    }

    [Fact]
    public async Task A_lapsed_node_is_not_waited_for_because_only_fresh_adverts_count()
    {
        await using var kit = await DriverKit.CreateAsync(fixture);
        kit.Facts.NoOrigin = true;
        await kit.PrepareAsync();
        await kit.StepUntilAsync("S1");
        kit.Fleet.Nodes[1].Acks = false;
        kit.Fleet.Nodes[1].Age = TimeSpan.FromSeconds(7);

        await kit.StepUntilAsync("S2");
    }

    [Fact]
    public async Task X3_deletes_a_junk_native_key_then_counts_again_and_waits_while_one_remains()
    {
        await using var kit = await DriverKit.CreateAsync(fixture);
        await kit.PrepareAsync();
        await kit.StepUntilAsync("S1");
        kit.Data.PurgeWorks = false;
        kit.Data.Native = 4;

        for (var n = 0; n < 4; n++)
        {
            await kit.StepAsync();
        }

        (await kit.StateAsync()).State.Should().Be("S1");
        kit.Data.Calls.Should().Contain("purge_n");
        kit.Alerts.Should().Contain(("stage_pending", "native_keys_remain"));

        kit.Data.PurgeWorks = true;
        await kit.StepUntilAsync("S3");
        kit.Data.Native.Should().Be(0);
    }

    [Fact]
    public async Task The_library_never_advances_without_the_flag_and_a_pod_without_a_view_blocks_the_start()
    {
        await using var kit = await DriverKit.CreateAsync(fixture);
        await kit.PrepareAsync();
        kit.Fleet.All(n => n.Fl = 2);
        for (var n = 0; n < 15; n++)
        {
            await kit.StepAsync();
        }

        (await kit.StateAsync()).State.Should().Be("S0");

        kit.Fleet.All(n => n.Fl = 5);
        kit.Fleet.Nodes[1].Fl = null;
        for (var n = 0; n < 15; n++)
        {
            await kit.StepAsync();
        }

        (await kit.StateAsync()).State.Should().Be("S0", "a pod that has not read the flag blocks forward");

        kit.Fleet.Nodes[1].Fl = 3;
        await kit.StepUntilAsync("S3");
        kit.Mover.Report = FakeMoverStatus.Drained();
        for (var n = 0; n < 5; n++)
        {
            await kit.StepAsync();
        }

        (await kit.StateAsync()).State.Should().Be("S3", "the views are at 3, so S3 is the most the flag asks");
    }

    [Fact]
    public async Task Pods_with_different_views_move_only_to_the_lowest_stage_they_all_show()
    {
        await using var kit = await DriverKit.CreateAsync(fixture);
        await kit.PrepareAsync();
        kit.Fleet.Nodes[0].Fl = 5;
        kit.Fleet.Nodes[1].Fl = 3;
        await kit.StepUntilAsync("S3");
        kit.Mover.Report = FakeMoverStatus.Drained(0, ("a", 0));

        for (var n = 0; n < 6; n++)
        {
            await kit.StepAsync();
        }

        (await kit.StateAsync()).State.Should().Be("S3", "the min rule: one pod still shows 3");
        kit.Fleet.Nodes[1].Fl = 4;
        await kit.StepUntilAsync("S4");
    }

    [Fact]
    public async Task X1_is_refused_with_the_failing_gate_named_and_goes_ahead_when_it_clears()
    {
        await using var kit = await DriverKit.CreateAsync(fixture);
        await kit.PrepareAsync();
        kit.Fleet.Nodes[1].Km = false;

        for (var n = 0; n < 3; n++)
        {
            await kit.StepAsync();
        }

        (await kit.StateAsync()).State.Should().Be("S0");
        kit.Alerts.Should().Contain(("start_blocked", "start.km_support:n2"));
        kit.Alerts.Count(a => a.Code == "start_blocked").Should().Be(1, "an alert is raised on the edge, not every step");

        kit.Fleet.Nodes[1].Km = true;
        await kit.StepUntilAsync("S1");
    }

    [Fact]
    public async Task X1_is_refused_while_a_native_key_exists_and_the_leader_never_deletes_it_by_itself()
    {
        await using var kit = await DriverKit.CreateAsync(fixture);
        await kit.PrepareAsync();
        kit.Data.Native = 2;

        for (var n = 0; n < 3; n++)
        {
            await kit.StepAsync();
        }

        (await kit.StateAsync()).State.Should().Be("S0");
        kit.Alerts.Should().Contain(("start_blocked", "start.native_empty:native_keys:2"));
        kit.Data.Calls.Should().NotContain("purge_n");
    }

    [Fact]
    public async Task X1_is_refused_when_the_server_fails_the_protocol_probe_for_the_eviction_policy()
    {
        await using var kit = await DriverKit.CreateAsync(fixture, state: new KeyMoveStateOptions { ExpectedReplicasPerMaster = 0 });
        await kit.PrepareAsync();
        kit.Kit.Topology.Probe = _ => new MasterProbe(0, "allkeys-lru");

        await kit.StepAsync();

        (await kit.StateAsync()).State.Should().Be("S0");
        kit.Alerts.Should().Contain(("start_blocked", "start.server_preflight:eviction_policy"));
        await kit.Commands.ApproveAsync(kit.Group, ApprovalKinds.ControlRecordsEvictable, "records may be evicted", "op", CancellationToken.None);
        await kit.StepUntilAsync("S1");
    }

    [Fact]
    public async Task A_node_that_loses_the_lease_writes_nothing()
    {
        await using var kit = await DriverKit.CreateAsync(fixture);
        await kit.PrepareAsync();
        kit.HoldsLease = false;

        (await kit.StepAsync()).Should().BeNull();

        (await kit.StateAsync()).State.Should().Be("S0");
    }
}
