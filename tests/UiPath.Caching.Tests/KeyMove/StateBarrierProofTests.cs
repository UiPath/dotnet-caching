using System.Net;
using StackExchange.Redis;
using UiPath.Caching.KeyMove;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests.KeyMove;

// Every barrier of a transition (TRANSIT, CTLSYNC, CMTBEGIN, CTLCOMMIT, DURABLE; WAIT numbers 1 to 5 of a run on one master) confirms only a write its WAIT provably covered.
// The WAIT is answered by a hook with 1, so a retry can only come from the proof: the connection killed, or the replication identity changed, between the write and the WAIT.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class StateBarrierProofTests(RedisContainerFixture fixture)
{
    private const int Sites = 5;

    private static readonly KeyMoveStateOptions WithReplica = new() { ExpectedReplicasPerMaster = 1, BarrierWait = TimeSpan.FromMilliseconds(50) };

    public static TheoryData<bool, int> EverySite => Cross();

    public static TheoryData<bool, int, string> EverySiteAndIdentity => CrossIdentity();

    [Theory]
    [MemberData(nameof(EverySite))]
    public async Task A_connection_replaced_between_the_write_and_the_wait_is_not_confirmed_and_the_barrier_runs_again_on_a_new_one(bool useScripts, int site)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts, WithReplica);
        await using var admin = await AdminAsync();
        var init = await kit.InitAsync("a");
        var waits = 0;
        kit.Topology.Wait = (_, _) => 1;
        kit.Topology.BeforeWait = async (endpoint, server) =>
        {
            if (Interlocked.Increment(ref waits) == site)
            {
                await ReplaceConnectionAsync(admin, endpoint, server);
            }
        };

        var result = await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(init), StateKit.TargetFrom(init, "S1", "fwd", 0), null, CancellationToken.None);

        result.Should().Be(new TransitionResult(TransitionOutcome.Completed));
        kit.Topology.Waits.Should().Be(Sites + 1, $"site {site}: the WAIT that ran on another connection confirmed nothing, so the write and WAIT ran again");
        kit.Topology.Opened.Should().Be(2, "the failed barrier replaced the connection");
        kit.Reports.Should().BeEmpty();
        (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!.Durable.Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(EverySite))]
    public async Task A_connection_replaced_before_every_wait_of_a_barrier_never_confirms_it(bool useScripts, int site)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts, WithReplica);
        await using var admin = await AdminAsync();
        var init = await kit.InitAsync("a");
        var waits = 0;
        kit.Topology.Wait = (_, _) => 1;
        kit.Topology.BeforeWait = async (endpoint, server) =>
        {
            if (Interlocked.Increment(ref waits) is var n && n >= site && n < site + WithReplica.BarrierRetries)
            {
                await ReplaceConnectionAsync(admin, endpoint, server);
            }
        };

        var result = await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(init), StateKit.TargetFrom(init, "S1", "fwd", 0), null, CancellationToken.None);

        AssertUnconfirmed(kit, result, site, WithReplica.BarrierRetries);
        kit.Reports.Should().Contain(r => r.Kind == "barrier_failed");
    }

    [Theory]
    [MemberData(nameof(EverySiteAndIdentity))]
    public async Task A_replication_identity_that_changed_after_the_wait_is_not_confirmed_and_the_barrier_runs_again(bool useScripts, int site, string field)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts, WithReplica);
        var init = await kit.InitAsync("a");
        var reads = 0;

        // A restarted server has another run id, a demoted and promoted one another replication id; both show in the INFO read after the WAIT.
        kit.Topology.Wait = (_, _) => 1;
        kit.Topology.Info = (_, text) => Interlocked.Increment(ref reads) == site ? text.Replace(field + ":", field + ":changed", StringComparison.Ordinal) : text;

        var result = await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(init), StateKit.TargetFrom(init, "S1", "fwd", 0), null, CancellationToken.None);

        result.Should().Be(new TransitionResult(TransitionOutcome.Completed));
        kit.Topology.Waits.Should().Be(Sites + 1);
        kit.Reports.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(EverySite))]
    public async Task A_write_whose_exec_reply_was_lost_is_not_confirmed_and_never_sent_again(bool useScripts, int site)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts, WithReplica);
        var init = await kit.InitAsync("a");
        var target = StateKit.TargetFrom(init, "S1", "fwd", 0);
        kit.Topology.Wait = (_, _) => 1;
        kit.Topology.LoseReply = number => number == site;

        var result = await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(init), target, null, CancellationToken.None);

        AssertUnconfirmed(kit, result, site, extraWaits: 0);
        kit.Topology.Opened.Should().Be(1, "a transaction that may have applied is neither repeated nor is its connection replaced to repeat it");
        (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!.Id.Should().Be(target.Id, "the write whose reply was lost, or an earlier one, applied once");
        kit.Reports.Should().Contain(r => r.Kind == "barrier_failed");

        // A resume repeats the step on purpose, on a clean run, and confirms it.
        kit.Topology.LoseReply = null;
        kit.Topology.Wait = (_, _) => 1;
        (await kit.Sequence.ResumeAsync(kit.Group, null, CancellationToken.None)).Outcome.Should().Be(TransitionOutcome.Completed);
        (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!.Durable.Should().BeTrue();
    }

    [Theory]
    [InlineData(true, "owner")]
    [InlineData(false, "owner")]
    [InlineData(true, "throw")]
    [InlineData(false, "throw")]
    public async Task A_lost_exec_reply_is_not_sent_again_whatever_the_owner_lookup_after_it_says(bool useScripts, string lookup)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts, WithReplica);
        var init = await kit.InitAsync("a");
        kit.Topology.Wait = (_, _) => 1;
        kit.Topology.LoseReply = number => number == 1;

        // The lookup before the write is the first; the one after it says another master, or fails.
        kit.Topology.StateOwner = (_, _, n) =>
        {
            if (n < 2)
            {
                return null;
            }

            return lookup == "owner"
                ? new DnsEndPoint("elsewhere", 6379)
                : throw new RedisConnectionException(ConnectionFailureType.SocketFailure, CommandFlags.None, "gone", null, CommandStatus.Unknown);
        };
        var result = await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(init), StateKit.TargetFrom(init, "S1", "fwd", 0), null, CancellationToken.None);

        result.Outcome.Should().Be(TransitionOutcome.BarrierStuck);
        kit.Topology.Execs.Should().Be(1);
        kit.Topology.Opened.Should().Be(1);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_slot_left_out_while_migrating_is_written_and_waited_for_even_when_the_migration_clears_without_a_change_of_owner(bool useScripts)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts, WithReplica);
        var init = await kit.InitAsync("a");
        var target = StateKit.TargetFrom(init, "S1", "fwd", 0);

        // The record already holds the target from an unconfirmed attempt; the first reading of the epoch sync says the slot migrates, the next that it does not.
        await kit.Scripts.ControlSyncAsync(kit.Names.Control(kit.Group, 0), MoveTestEnvironment.Record(target.Ep, target.Id, ("a", "n")));
        var readings = 0;
        kit.Topology.Wait = (_, _) => 1;
        kit.Topology.Layout = (real, _) => Interlocked.Increment(ref readings) == 4 ? real.WithUnsettled([0]) : real;

        var result = await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(init), target, null, CancellationToken.None);

        result.Should().Be(new TransitionResult(TransitionOutcome.Completed));
        kit.Topology.Waits.Should().Be(Sites, "the epoch sync of the slot went through a barrier, though a read already found the target");
    }

    [Fact]
    public async Task A_queued_script_that_fails_at_run_time_after_exec_succeeded_is_not_confirmed_and_never_sent_again()
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts: true, WithReplica);
        var init = await kit.InitAsync("a");
        await kit.Db.KeyDeleteAsync(kit.StateKey);
        await kit.Db.StringSetAsync(kit.StateKey, "not a hash");
        kit.Topology.Wait = (_, _) => 1;

        var result = await kit.Sequence.RunAsync(kit.Group, StateExpectation.Absent, StateKit.TargetFrom(init, "S1", "fwd", 0), null, CancellationToken.None);

        result.Outcome.Should().Be(TransitionOutcome.BarrierStuck);
        kit.Topology.Execs.Should().Be(1);
        kit.Topology.Opened.Should().Be(1);
        kit.Topology.Waits.Should().Be(0, "a transaction that partly applied is not waited for");
    }

    [Fact]
    public async Task A_server_that_refuses_scripts_inside_the_barrier_runs_the_fallback_on_the_same_connection()
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts: true, WithReplica);
        var init = await kit.InitAsync("a");
        kit.Topology.Wait = (_, _) => 1;
        kit.Topology.RefuseScripts = true;
        kit.Topology.ServerVersion = fixture.ServerVersion;

        var result = await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(init), StateKit.TargetFrom(init, "S1", "fwd", 0), null, CancellationToken.None);

        result.Should().Be(new TransitionResult(TransitionOutcome.Completed));
        kit.Topology.Opened.Should().Be(1, "the refusal was learned, not mistaken for a failed barrier");
        kit.Topology.Waits.Should().Be(Sites);
        (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!.Durable.Should().BeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_cluster_batch_of_control_writes_whose_connection_was_replaced_is_not_confirmed_and_runs_again(bool useScripts)
    {
        var connection = Environment.GetEnvironmentVariable("REDIS_CLUSTER_CONNECTION_STRING");
        await using var kit = await StateKit.ClusterAsync(useScripts, WithReplica);
        await using var admin = await ConnectionMultiplexer.ConnectAsync(connection + ",allowAdmin=true");
        var init = await kit.InitAsync("a");
        var target = StateKit.TargetFrom(init, "S1", "fwd", 0);
        var waits = 0;
        kit.Topology.Wait = (_, _) => 1;

        // The second WAIT is the first master's barrier of the epoch sync: its transactions, one per slot, all ran before the connection was replaced.
        kit.Topology.BeforeWait = async (endpoint, server) =>
        {
            if (Interlocked.Increment(ref waits) == 2)
            {
                await ReplaceConnectionAsync(admin, endpoint, server);
            }
        };

        var result = await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(init), target, null, CancellationToken.None);

        result.Should().Be(new TransitionResult(TransitionOutcome.Completed));
        var masters = (await kit.Real.ReadLayoutAsync(CancellationToken.None)).Masters.Count;
        kit.Topology.Waits.Should().Be(1 + (2 * masters) + 2 + 1, "one barrier of the batch ran twice");
        foreach (var slot in new[] { 0, 5461, 10922, 16383 })
        {
            (await kit.ControlAsync(slot)).Should().Contain(e => e.Name == "id" && e.Value == target.Id).And.Contain(e => e.Name == "dur" && e.Value == "1");
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_cluster_commit_that_applied_in_part_and_left_a_record_uncommitted_is_never_sent_again_in_the_same_call(bool useScripts)
    {
        await using var kit = await StateKit.ClusterAsync(useScripts, WithReplica);
        var init = await kit.InitAsync("a");
        var slots = kit.Names.SlotSet().Count;
        kit.Topology.Wait = (_, _) => 1;

        // The EXECs are the TRANSIT, one per slot of the epoch sync, the commit marker, then one per slot of the commit: one of those loses its reply without having run.
        var lost = 1 + slots + 1 + 5000;
        kit.Topology.LoseBeforeApply = number => number == lost;
        var result = await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(init), StateKit.TargetFrom(init, "S1", "fwd", 0), null, CancellationToken.None);

        result.Outcome.Should().Be(TransitionOutcome.CommitUncertain);
        kit.Topology.Execs.Should().BeLessThanOrEqualTo(2 + (2 * slots), "no transaction of the commit, applied or not, went out a second time");
        var committed = 0;
        foreach (var slot in kit.Names.SlotSet())
        {
            committed += (await kit.ControlAsync(slot)).Any(e => e.Name == "dur" && e.Value == "1") ? 1 : 0;
        }

        committed.Should().BeInRange(1, slots - 1, "one master's batch committed, and the record whose EXEC was lost did not");

        // A resume repeats the commit on purpose.
        kit.Topology.LoseBeforeApply = null;
        (await kit.Sequence.ResumeAsync(kit.Group, null, CancellationToken.None)).Outcome.Should().Be(TransitionOutcome.Completed);
    }

    [Theory]
    [InlineData(true, 2, false)]
    [InlineData(true, 3, false)]
    [InlineData(false, 2, true)]
    [InlineData(false, 3, true)]
    public async Task An_uncertain_epoch_sync_or_commit_marker_stops_the_attempt_even_when_barriers_are_skipped_or_it_is_the_way_back(bool useScripts, int site, bool wayBack)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts, WithReplica);
        var init = await kit.InitAsync("a");
        kit.Topology.Wait = (_, _) => 1;
        kit.Topology.LoseReply = number => number == site;
        var sequence = wayBack ? new SequenceOptions(WayBack: true) : new SequenceOptions(SkipBarriers: true);

        var result = await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(init), StateKit.TargetFrom(init, "S1", "fwd", 0), sequence, CancellationToken.None);

        result.Outcome.Should().Be(TransitionOutcome.BarrierStuck);
        kit.Topology.Execs.Should().Be(site, "nothing after the uncertain write was sent, and it was not sent again");
    }

    [Theory]
    [InlineData("refusal")]
    [InlineData("move")]
    public async Task An_uncertain_commit_on_one_master_stops_the_attempt_whatever_another_master_refuses_or_a_slot_moving_to_it(string other)
    {
        await using var kit = await StateKit.ClusterAsync(useScripts: true, WithReplica);
        var init = await kit.InitAsync("a");
        var slots = kit.Names.SlotSet().Count;
        var first = 1 + slots + 1 + 1;
        var fired = 0;
        kit.Topology.Wait = (_, _) => 1;
        kit.Topology.Layout = (real, _) => Volatile.Read(ref fired) == 1 && other == "move" ? Merged(real) : real;
        kit.Topology.LoseBeforeApply = number =>
        {
            if (number != first)
            {
                return false;
            }

            Volatile.Write(ref fired, 1);
            if (other == "refusal")
            {
                // The records of three slots now hold a higher epoch, so their CTLCOMMIT is refused.
                foreach (var slot in new[] { 0, 5461, 16383 })
                {
                    kit.ControlDb.HashSet(kit.Names.Control(kit.Group, slot), "ep", long.MaxValue / 2);
                }
            }

            return true;
        };

        var result = await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(init), StateKit.TargetFrom(init, "S1", "fwd", 0), null, CancellationToken.None);

        result.Outcome.Should().Be(TransitionOutcome.CommitUncertain);
        kit.Topology.Execs.Should().BeLessThanOrEqualTo(2 + (2 * slots), "no transaction of the commit went out a second time");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_durable_write_that_never_landed_and_got_no_reply_is_never_reported_completed(bool useScripts)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts, WithReplica);
        var init = await kit.InitAsync("a");
        kit.Topology.Wait = (_, _) => 1;
        kit.Topology.LoseBeforeApply = number => number == Sites;

        var result = await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(init), StateKit.TargetFrom(init, "S1", "fwd", 0), null, CancellationToken.None);

        result.Outcome.Should().Be(TransitionOutcome.CommitUncertain);
        result.Detail.Should().Contain("may not have landed");
        (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!.Durable.Should().BeFalse();
        kit.Topology.Execs.Should().Be(Sites);
    }

    [Fact]
    public async Task A_master_that_would_retry_makes_no_further_attempt_once_another_master_is_uncertain_and_its_write_in_flight_is_settled()
    {
        await using var kit = await StateKit.ClusterAsync(useScripts: true, WithReplica);
        var init = await kit.InitAsync("a");
        var masters = (await kit.Real.ReadLayoutAsync(CancellationToken.None)).Masters.Count;
        var first = 1 + kit.Names.SlotSet().Count + 1 + 1;
        var fired = 0;

        // From the commit on, every WAIT fails, so each master that is not uncertain would retry; the first commit EXEC loses its reply before it runs.
        kit.Topology.Wait = (_, _) => Volatile.Read(ref fired) == 1 ? 0 : 1;
        kit.Topology.LoseBeforeApply = number =>
        {
            if (number != first)
            {
                return false;
            }

            Volatile.Write(ref fired, 1);
            return true;
        };

        var result = await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(init), StateKit.TargetFrom(init, "S1", "fwd", 0), null, CancellationToken.None);

        result.Outcome.Should().Be(TransitionOutcome.CommitUncertain);

        // TRANSIT, one WAIT per master for the epoch sync, the commit marker, and one first attempt of each master that was not uncertain: its write in flight was waited for, none was repeated.
        kit.Topology.Waits.Should().Be(2 + masters + (masters - 1));
    }

    [Fact]
    public async Task A_master_paused_while_it_prepares_a_retry_sends_nothing_once_another_master_reports_uncertainty()
    {
        await using var kit = await StateKit.ClusterAsync(useScripts: true, WithReplica);
        var init = await kit.InitAsync("a");
        var masters = (await kit.Real.ReadLayoutAsync(CancellationToken.None)).Masters.Count;
        var first = 1 + kit.Names.SlotSet().Count + 1 + 1;
        var fired = 0;
        var paused = new SemaphoreSlim(0);
        var reported = new TaskCompletionSource();
        kit.Topology.Wait = (_, _) => Volatile.Read(ref fired) == 1 ? 0 : 1;

        // The uncertain master holds its lost EXEC until a retrying master is paused between its loop check and its dispatch, then reports.
        kit.Topology.LoseBeforeApply = number =>
        {
            if (number != first)
            {
                return false;
            }

            Volatile.Write(ref fired, 1);
            Assert.True(paused.Wait(TimeSpan.FromSeconds(60)), "no master reached a retry");
            return true;
        };

        // A retry opens a second connection; that is where the retrying masters are paused, until the uncertainty is reported.
        kit.Topology.BeforeOpen = async (_, number) =>
        {
            if (number >= 2 && Volatile.Read(ref fired) == 1)
            {
                paused.Release();
                await reported.Task.WaitAsync(TimeSpan.FromSeconds(60));
            }
        };
        var run = kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(init), StateKit.TargetFrom(init, "S1", "fwd", 0), null, CancellationToken.None);
        while (!run.IsCompleted && !kit.Reports.Any(r => r.Kind == "barrier_failed"))
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        reported.TrySetResult();
        var result = await run;

        result.Outcome.Should().Be(TransitionOutcome.CommitUncertain);
        kit.Topology.Waits.Should().Be(2 + masters + (masters - 1), "the retries that were paused when the uncertainty came were never dispatched");
    }

    [Fact]
    public async Task A_master_paused_before_an_unwaited_write_sends_nothing_once_another_master_reports_uncertainty()
    {
        await using var kit = await StateKit.ClusterAsync(useScripts: true, WithReplica);
        var init = await kit.InitAsync("a");
        var layout = await kit.Real.ReadLayoutAsync(CancellationToken.None);
        var stateOwner = await kit.Db.IdentifyEndpointAsync(kit.StateKey);
        var unwaited = layout.Masters.First(m => !m.Endpoint.Equals(stateOwner));
        var probe = unwaited.Slots[0].From;
        var reported = new TaskCompletionSource();

        // The way back skips the barrier of a master that failed recently: its writes go out alone. That master is held while it opens its connection, until another master reports an uncertain write.
        kit.Ledger.Record(unwaited.Endpoint);
        kit.Topology.Wait = (_, _) => 1;
        kit.Topology.LoseBeforeApply = number => number == 2;
        kit.Topology.BeforeOpen = async (endpoint, _) =>
        {
            if (endpoint.Equals(unwaited.Endpoint))
            {
                await reported.Task.WaitAsync(TimeSpan.FromSeconds(60));
            }
        };
        var run = kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(init), StateKit.TargetFrom(init, "S1", "fwd", 0), new SequenceOptions(WayBack: true, Ledger: kit.Ledger), CancellationToken.None);
        while (!run.IsCompleted && !kit.Reports.Any(r => r.Kind == "barrier_failed"))
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        reported.TrySetResult();
        var result = await run;

        result.Outcome.Should().NotBe(TransitionOutcome.Completed);
        (await kit.ControlAsync(probe)).Should().NotContain(e => e.Name == "id", "the held master never sent its epoch sync once another was uncertain");
    }

    private static ClusterLayout Merged(ClusterLayout real)
    {
        var masters = real.Masters.OrderBy(m => m.Endpoint.ToString(), StringComparer.Ordinal).ToArray();
        return new ClusterLayout([masters[0] with { Slots = [] }, masters[1] with { Slots = [.. masters[0].Slots, .. masters[1].Slots] }, .. masters.Skip(2)]);
    }

    private static TheoryData<bool, int> Cross()
    {
        var data = new TheoryData<bool, int>();
        foreach (var scripts in new[] { true, false })
        {
            for (var site = 1; site <= Sites; site++)
            {
                data.Add(scripts, site);
            }
        }

        return data;
    }

    private static TheoryData<bool, int, string> CrossIdentity()
    {
        var data = new TheoryData<bool, int, string>();
        foreach (var scripts in new[] { true, false })
        {
            for (var site = 1; site <= Sites; site++)
            {
                data.Add(scripts, site, "run_id");
                data.Add(scripts, site, "master_replid");
            }
        }

        return data;
    }

    // Sites 1 to 3 stop the sequence before any record is committed; from the commit on a barrier that fails only marks the wait as skipped,
    // except a commit or DURABLE whose EXEC reply was lost (no extra WAIT was tried), which stops the attempt as uncertain.
    private static void AssertUnconfirmed(StateKit kit, TransitionResult result, int site, int extraWaits)
    {
        if (site >= 4 && extraWaits == 0)
        {
            result.Outcome.Should().Be(TransitionOutcome.CommitUncertain);
            kit.Topology.Waits.Should().Be(site - 1);
            return;
        }

        if (site <= 3)
        {
            result.Outcome.Should().Be(TransitionOutcome.BarrierStuck, $"site {site}");
            kit.Topology.Waits.Should().Be(site - 1 + extraWaits);
            return;
        }

        result.Should().Be(new TransitionResult(TransitionOutcome.Completed, WaitSkipped: true), $"site {site}");
        kit.Topology.Waits.Should().Be(Sites - 1 + extraWaits);
    }

    // Kills the dedicated connection and waits until the client has a new one: the next command, the WAIT, runs on a connection the write never used.
    private static async Task ReplaceConnectionAsync(ConnectionMultiplexer admin, EndPoint endpoint, IServer server)
    {
        var id = (long)await server.ExecuteAsync("CLIENT", "ID");
        await admin.GetServer(endpoint).ExecuteAsync("CLIENT", "KILL", "ID", id);
        for (var i = 0; i < 100; i++)
        {
            try
            {
                if ((long)await server.ExecuteAsync("CLIENT", "ID") != id)
                {
                    return;
                }
            }
            catch (RedisException)
            {
                // Reconnecting.
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("The killed connection was never replaced.");
    }

    private async Task<ConnectionMultiplexer> AdminAsync()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        return await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString + ",allowAdmin=true");
    }
}
