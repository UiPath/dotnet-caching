using StackExchange.Redis;
using UiPath.Caching.PhaseB;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests.PhaseB;

[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class StateStoreTests(RedisContainerFixture fixture)
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Init_takes_an_epoch_above_the_highest_control_record_and_refreshes_an_s0_state_in_place(bool useScripts)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts);
        var high = long.MaxValue / 4;
        await kit.Scripts.ControlSyncAsync(kit.Names.Control(kit.Group, 0), MoveTestEnvironment.Record(high, "old", ("a", "n")));

        var first = await kit.Store.InitAsync(kit.Group, ["b", "a"], CancellationToken.None);
        var again = await kit.Store.InitAsync(kit.Group, ["a", "b", "c"], CancellationToken.None);

        first.Should().Match<InitResult>(r => r.Code == MoveScripts.StateCreated && r.Ep == high + 1);
        again.Code.Should().Be(MoveScripts.StateRefreshed);
        var state = (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!;
        state.Should().Match<StateRecord>(s => s.Ep == high + 1 && s.Id == first.Id && s.Durable && s.State == "S0" && s.Caches == "a+b+c");
        state.CacheIds.Should().Equal("a", "b", "c");
        state.CacheSet.Should().Be(StateRecord.HashOfCaches("a+b+c"));
    }

    [Fact]
    public async Task Init_names_the_state_under_the_library_prefix_and_never_creates_a_control_record()
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts: true);

        await kit.InitAsync("a");

        kit.StateKey.ToString().Should().Be($"{StateKit.Library}mg:{{{kit.Group}}}:state");
        (await kit.Db.KeyExistsAsync(kit.StateKey)).Should().BeTrue();
        (await kit.ControlAsync(0)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_state_that_cannot_be_read_is_an_error_and_never_an_absent_hash()
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts: true);
        (await kit.Store.ReadAsync(kit.Group, CancellationToken.None)).Should().BeNull();

        await kit.Db.HashSetAsync(kit.StateKey, "st", "S1");
        var unreadable = () => kit.Store.ReadAsync(kit.Group, CancellationToken.None);
        await unreadable.Should().ThrowAsync<FormatException>();

        await kit.Db.KeyDeleteAsync(kit.StateKey);
        await kit.Db.StringSetAsync(kit.StateKey, "x");
        var wrongType = () => kit.Store.ReadAsync(kit.Group, CancellationToken.None);
        await wrongType.Should().ThrowAsync<RedisServerException>();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Allack_is_stored_for_the_current_identity_only(bool useScripts)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts);
        var init = await kit.InitAsync("a");

        (await kit.Store.AllAckAsync(kit.Group, "stale")).Should().BeFalse();
        (await kit.Store.AllAckAsync(kit.Group, init.Id)).Should().BeTrue();

        (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!.AllAck.Should().Be(init.Id);
    }

    [Fact]
    public async Task The_probe_finds_the_control_record_at_the_first_slot_of_the_set()
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts: true);
        (await kit.Store.ProbeAsync(kit.Group, CancellationToken.None)).Should().BeNull();

        await kit.Scripts.ControlSyncAsync(kit.Names.Control(kit.Group, 0), new ControlRecord(7, "x", "S3", "fwd", "1", "S1", "fwd", "0", "6", "w", []));
        await kit.Scripts.ControlCommitAsync(kit.Names.Control(kit.Group, 0), 7, "x");

        (await kit.Store.ProbeAsync(kit.Group, CancellationToken.None)).Should().Be(new ControlSample(7, "x", "S3", "fwd", true));
    }

    [Fact]
    public async Task A_node_reads_an_init_ed_listed_group_at_startup_and_a_flushed_hash_keeps_the_last_known_s0_rules()
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts: true);
        await kit.InitAsync("a");
        var monitor = Monitor(kit, listed: true);

        await monitor.StepAsync(kit.Group, CancellationToken.None);
        monitor.RulesFor(kit.Group).Should().Match<GroupRules>(r => r.Writes == WriteRule.Follow && r.Acting!.State == "S0");

        await kit.Db.KeyDeleteAsync(kit.StateKey);
        await monitor.StepAsync(kit.Group, CancellationToken.None);

        monitor.RulesFor(kit.Group).Should().Match<GroupRules>(r => r.Writes == WriteRule.Follow && r.Alerts.Contains("state_missing"));
    }

    [Fact]
    public async Task A_restarted_node_with_a_listed_group_and_an_absent_hash_fails_writes_closed()
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts: true);

        var monitor = Monitor(kit, listed: true);
        await monitor.StepAsync(kit.Group, CancellationToken.None);

        monitor.RulesFor(kit.Group).Should().Match<GroupRules>(r => r.Writes == WriteRule.FailClosed && r.Reads == ReadRule.UnionNativeFirst && r.Alerts.Contains("state_missing"));
    }

    [Fact]
    public async Task An_unlisted_group_with_an_absent_hash_and_no_record_stays_on_phase_a_with_no_wait()
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts: true);
        var monitor = Monitor(kit, listed: false);

        var next = await monitor.StepAsync(kit.Group, CancellationToken.None);

        next.Should().BeNull();
        monitor.RulesFor(kit.Group).Should().Be(GroupRules.Unaffected);
    }

    [Fact]
    public async Task The_record_probe_of_an_unlisted_group_without_a_hash_makes_it_relevant_and_lost()
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts: true);
        await kit.Scripts.ControlSyncAsync(kit.Names.Control(kit.Group, 0), MoveTestEnvironment.Record(3, "x", ("a", "n")));
        var monitor = Monitor(kit, listed: false);

        await monitor.StepAsync(kit.Group, CancellationToken.None);

        monitor.RulesFor(kit.Group).Should().Match<GroupRules>(r => r.Relevant && r.Writes == WriteRule.FailClosed && r.Alerts.Contains("state_missing"));
    }

    [Fact]
    public async Task A_restart_that_meets_a_regressed_hash_sees_the_newer_control_record_and_closes_writes()
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts: true);
        var init = await kit.InitAsync("a");
        var target = StateKit.TargetFrom(init, "S1", "fwd", 0);
        (await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(init), target, null, CancellationToken.None)).Outcome.Should().Be(TransitionOutcome.Completed);

        // The state shard rolled back to the S0 identity; the control record kept the S1 one.
        await kit.Db.KeyDeleteAsync(kit.StateKey);
        await kit.Db.HashSetAsync(
            kit.StateKey,
            [new("st", "S0"), new("dir", "none"), new("nPop", "0"), new("ep", init.Ep), new("id", init.Id), new("dur", "1"), new("prev", init.Prev), new("caches", "a")]);
        var monitor = Monitor(kit, listed: true);

        await monitor.StepAsync(kit.Group, CancellationToken.None);

        monitor.RulesFor(kit.Group).Should().Match<GroupRules>(r => r.Writes == WriteRule.FailClosed && r.Alerts.Contains("identity_conflict") && r.Acting == null);
    }

    [Fact]
    public async Task A_restart_that_meets_a_control_record_of_the_same_epoch_under_another_id_closes_writes()
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts: true);
        var init = await kit.InitAsync("a");
        await kit.Scripts.ControlSyncAsync(kit.Names.Control(kit.Group, 0), MoveTestEnvironment.Record(init.Ep, "intruder", ("a", "n")));
        var monitor = Monitor(kit, listed: true);

        await monitor.StepAsync(kit.Group, CancellationToken.None);

        monitor.RulesFor(kit.Group).Should().Match<GroupRules>(r => r.Writes == WriteRule.FailClosed && r.Alerts.Contains("identity_conflict") && r.Acting == null);
    }

    [Fact]
    public async Task A_lost_commit_marker_on_the_state_is_a_commit_mismatch_found_from_the_control_record()
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts: true);
        var init = await kit.InitAsync("a");
        var target = StateKit.TargetFrom(init, "S1", "fwd", 0);
        await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(init), target, null, CancellationToken.None);
        await kit.Db.HashSetAsync(kit.StateKey, "dur", "0");
        var monitor = Monitor(kit, listed: true);

        await monitor.StepAsync(kit.Group, CancellationToken.None);

        monitor.RulesFor(kit.Group).Should().Match<GroupRules>(r => r.Writes == WriteRule.FailClosed && r.Alerts.Contains("commit_mismatch"));
    }

    private static GroupStateMonitor Monitor(StateKit kit, bool listed)
    {
        var monitor = new GroupStateMonitor(kit.Store, kit.Options, TimeProvider.System);
        monitor.Register([new GroupRegistration(kit.Group, false, listed)]);
        return monitor;
    }
}
