using UiPath.Caching.PhaseB;

namespace UiPath.Caching.Tests.PhaseB;

public class GroupStateMonitorTests
{
    private const string Group = "g1";

    private readonly FakeStateReader _reader = new();
    private readonly ManualTimeProvider _time = new(DateTimeOffset.UnixEpoch);
    private readonly PhaseBStateOptions _options = new();
    private readonly List<(string Group, string Alert)> _alerts = [];

    [Fact]
    public async Task A_listed_group_with_a_durable_state_follows_it_and_is_polled_every_two_seconds()
    {
        var monitor = Monitor(listed: true);
        _reader.State = FakeStateReader.Record("S1", 5);

        var next = await monitor.StepAsync(Group, CancellationToken.None);

        next.Should().Be(TimeSpan.FromSeconds(2));
        var rules = monitor.RulesFor(Group);
        rules.Should().Match<GroupRules>(r => r.Relevant && r.Reads == ReadRule.Follow && r.Writes == WriteRule.Follow && r.Alerts.Count == 0);
        rules.Acting.Should().Be(new ActedState("S1", "fwd", 5, "id", 0, false));
    }

    [Fact]
    public async Task A_failed_read_is_never_absent_the_group_fails_writes_closed_from_the_first_admission_and_is_read_again_in_two_seconds()
    {
        var monitor = Monitor(listed: true);
        _reader.Fail = true;

        var next = await monitor.StepAsync(Group, CancellationToken.None);

        next.Should().Be(TimeSpan.FromSeconds(2));
        monitor.RulesFor(Group).Should().Match<GroupRules>(r => r.Reads == ReadRule.UnionNativeFirst && r.Writes == WriteRule.FailClosed && r.Alerts.Contains("no_snapshot"));
        _alerts.Should().ContainSingle().Which.Alert.Should().Be("no_snapshot");

        _reader.Fail = false;
        _reader.State = FakeStateReader.Record("S0", 1);
        await monitor.StepAsync(Group, CancellationToken.None);
        monitor.RulesFor(Group).Writes.Should().Be(WriteRule.Follow);
    }

    [Fact]
    public async Task Start_up_wait_changes_readiness_only_never_the_write_rule()
    {
        var monitor = Monitor(listed: true);
        _reader.Fail = true;
        await monitor.StepAsync(Group, CancellationToken.None);

        monitor.RulesFor(Group).Ready.Should().BeTrue();
        _time.Advance(TimeSpan.FromSeconds(5));
        var late = monitor.RulesFor(Group);

        late.Ready.Should().BeFalse();
        late.Writes.Should().Be(WriteRule.FailClosed);
        monitor.RulesFor(Group).Alerts.Should().NotContain("node_blind");
        _time.Advance(TimeSpan.FromSeconds(56));
        monitor.RulesFor(Group).Alerts.Should().Contain("node_blind");
    }

    [Fact]
    public async Task A_group_that_is_not_relevant_is_unaffected_even_while_the_state_shard_is_down()
    {
        var monitor = Monitor(listed: false);
        _reader.Fail = true;

        var next = await monitor.StepAsync(Group, CancellationToken.None);

        next.Should().Be(TimeSpan.FromSeconds(2));
        monitor.RulesFor(Group).Should().Be(GroupRules.Unaffected);
        monitor.RulesFor("never-registered").Should().Be(GroupRules.Unaffected);
    }

    [Fact]
    public async Task An_unlisted_unflagged_group_with_an_absent_hash_and_no_record_has_no_snapshot_and_is_not_polled()
    {
        var monitor = Monitor(listed: false);

        var next = await monitor.StepAsync(Group, CancellationToken.None);

        next.Should().BeNull();
        monitor.RulesFor(Group).Should().Be(GroupRules.Unaffected);
        _reader.Probes.Should().Be(1);
    }

    [Fact]
    public async Task A_flagged_group_without_a_hash_or_record_is_probed_again_every_minute_until_one_appears()
    {
        var monitor = Monitor(listed: false, flagged: true);

        var first = await monitor.StepAsync(Group, CancellationToken.None);

        first.Should().Be(TimeSpan.FromSeconds(60));
        monitor.RulesFor(Group).Should().Be(GroupRules.Unaffected);
        _reader.Sample = new ControlSample(9, "x", "S1", "fwd", true);
        var second = await monitor.StepAsync(Group, CancellationToken.None);

        second.Should().Be(TimeSpan.FromSeconds(2));
        monitor.RulesFor(Group).Should().Match<GroupRules>(r => r.Relevant && r.Writes == WriteRule.FailClosed && r.Alerts.Contains("state_missing"));
    }

    [Fact]
    public async Task A_control_record_without_a_hash_makes_an_unlisted_group_relevant_and_lost()
    {
        var monitor = Monitor(listed: false);
        _reader.Sample = new ControlSample(9, "x", "S3", "fwd", true);

        await monitor.StepAsync(Group, CancellationToken.None);

        monitor.RulesFor(Group).Should().Match<GroupRules>(r => r.Relevant && r.Reads == ReadRule.UnionNativeFirst && r.Writes == WriteRule.FailClosed && r.Alerts.Contains("state_missing"));
    }

    [Fact]
    public async Task A_hash_makes_an_unlisted_group_relevant()
    {
        var monitor = Monitor(listed: false);
        _reader.State = FakeStateReader.Record("S2", 3);

        await monitor.StepAsync(Group, CancellationToken.None);

        monitor.RulesFor(Group).Should().Match<GroupRules>(r => r.Relevant && r.Writes == WriteRule.Follow);
    }

    [Theory]
    [InlineData("S0")]
    [InlineData("S1")]
    [InlineData("S4")]
    public async Task A_listed_group_whose_snapshot_is_older_than_sixty_seconds_reads_the_union_fails_writes_closed_and_withdraws_its_acks(string state)
    {
        var monitor = Monitor(listed: true);
        _reader.State = FakeStateReader.Record(state, 5);
        await monitor.StepAsync(Group, CancellationToken.None);
        monitor.Acknowledge(Group, 5, "id").Should().BeTrue();
        _reader.Fail = true;

        _time.Advance(TimeSpan.FromSeconds(59));
        await monitor.StepAsync(Group, CancellationToken.None);
        monitor.RulesFor(Group).Writes.Should().Be(WriteRule.Follow);
        monitor.AckedFor(Group).Should().NotBeNull();

        _time.Advance(TimeSpan.FromSeconds(2));
        var stale = monitor.RulesFor(Group);

        stale.Writes.Should().Be(WriteRule.FailClosed);
        stale.Reads.Should().Be(state == "S0" ? ReadRule.Follow : ReadRule.UnionNativeFirst);
        stale.WithdrawAcks.Should().BeTrue();
        stale.Alerts.Should().Contain("snapshot_stale");
        monitor.AckedFor(Group).Should().BeNull();
    }

    [Fact]
    public async Task A_stale_closed_group_and_a_stale_unlisted_group_keep_the_last_known_rules_and_only_alert()
    {
        var closed = Monitor(listed: true);
        _reader.State = FakeStateReader.Record("S5", 8);
        await closed.StepAsync(Group, CancellationToken.None);
        var drifted = new GroupStateMonitor(_reader, _options, _time, (g, a) => _alerts.Add((g, a)));
        drifted.Register([new GroupRegistration("g2", false, false)]);
        await drifted.StepAsync("g2", CancellationToken.None);
        _reader.Fail = true;

        _time.Advance(TimeSpan.FromSeconds(500));

        foreach (var (monitor, name) in new[] { (closed, Group), (drifted, "g2") })
        {
            var rules = monitor.RulesFor(name);
            rules.Writes.Should().Be(WriteRule.Follow, name);
            rules.WithdrawAcks.Should().BeFalse(name);
            rules.Alerts.Should().Contain("snapshot_stale", name);
        }
    }

    [Fact]
    public async Task Stale_after_is_three_polls_at_the_slow_poll_interval_so_a_closed_group_is_stale_after_one_hundred_eighty_seconds()
    {
        var monitor = Monitor(listed: true);
        _reader.State = FakeStateReader.Record("S5", 8);
        (await monitor.StepAsync(Group, CancellationToken.None)).Should().Be(TimeSpan.FromSeconds(60));

        _time.Advance(TimeSpan.FromSeconds(179));
        monitor.RulesFor(Group).Alerts.Should().BeEmpty();
        _time.Advance(TimeSpan.FromSeconds(2));

        monitor.RulesFor(Group).Alerts.Should().Contain("snapshot_stale");
        _options.StaleAfter(TimeSpan.FromSeconds(2)).Should().Be(TimeSpan.FromSeconds(60));
        _options.StaleAfter(TimeSpan.FromSeconds(60)).Should().Be(TimeSpan.FromSeconds(180));
    }

    [Theory]
    [InlineData("S0", true)]
    [InlineData("S5", true)]
    [InlineData("S1", false)]
    [InlineData("S3", false)]
    [InlineData("S4", false)]
    [InlineData(null, false)]
    public async Task A_flushed_hash_keeps_the_rules_of_a_last_known_s0_or_s5_and_closes_the_group_otherwise(string? lastKnown, bool keeps)
    {
        var monitor = Monitor(listed: true);
        if (lastKnown is not null)
        {
            _reader.State = FakeStateReader.Record(lastKnown, 5);
            await monitor.StepAsync(Group, CancellationToken.None);
        }

        _reader.State = null;
        await monitor.StepAsync(Group, CancellationToken.None);

        var rules = monitor.RulesFor(Group);
        rules.Reads.Should().Be(keeps ? ReadRule.Follow : ReadRule.UnionNativeFirst);
        rules.Writes.Should().Be(keeps ? WriteRule.Follow : WriteRule.FailClosed);
        rules.Alerts.Should().Contain("state_missing");
    }

    [Fact]
    public async Task An_unreadable_state_with_a_terminal_s5_record_and_no_last_known_state_serves_the_s5_forms_and_is_not_failed_closed()
    {
        var monitor = Monitor(listed: true);
        _reader.Fail = true;
        _reader.Sample = new ControlSample(12, "idz", "S5", "fwd", true);

        await monitor.StepAsync(Group, CancellationToken.None);

        var rules = monitor.RulesFor(Group);
        rules.Reads.Should().Be(ReadRule.UnionNativeFirst);
        rules.Writes.Should().Be(WriteRule.Follow);
        rules.Acting.Should().Match<ActedState>(a => a.State == "S5" && !a.Swept);
    }

    [Fact]
    public async Task A_state_at_dur_zero_waits_up_to_the_transition_wait_and_then_fails_writes_closed()
    {
        var monitor = Monitor(listed: true);
        _reader.State = FakeStateReader.Record("S0", 5, "old");
        await monitor.StepAsync(Group, CancellationToken.None);
        _reader.State = FakeStateReader.Record("S1", 6, "new", durable: false);
        await monitor.StepAsync(Group, CancellationToken.None);

        _time.Advance(TimeSpan.FromSeconds(4));
        var waiting = monitor.RulesFor(Group);
        waiting.Reads.Should().Be(ReadRule.UnionNativeFirst);
        waiting.Writes.Should().Be(WriteRule.WaitThenFailClosed);
        waiting.WriteWait.Should().Be(TimeSpan.FromSeconds(6));
        waiting.Acting!.Id.Should().Be("old", "the node never acts on an identity before dur=1");

        _time.Advance(TimeSpan.FromSeconds(6));
        await monitor.StepAsync(Group, CancellationToken.None);

        monitor.RulesFor(Group).Writes.Should().Be(WriteRule.FailClosed);
        _reader.State = FakeStateReader.Record("S1", 6, "new");
        await monitor.StepAsync(Group, CancellationToken.None);
        monitor.RulesFor(Group).Should().Match<GroupRules>(r => r.Writes == WriteRule.Follow && r.Acting!.Id == "new");
    }

    [Theory]
    [InlineData(4, "id")]
    [InlineData(5, "other")]
    public async Task A_regressed_hash_is_an_identity_conflict_the_node_keeps_its_own_identity_and_writes_close(long ep, string id)
    {
        var monitor = Monitor(listed: true);
        _reader.State = FakeStateReader.Record("S3", 5);
        await monitor.StepAsync(Group, CancellationToken.None);
        _reader.State = FakeStateReader.Record("S1", ep, id);
        await monitor.StepAsync(Group, CancellationToken.None);

        var rules = monitor.RulesFor(Group);

        rules.Alerts.Should().Contain("identity_conflict");
        rules.Writes.Should().Be(WriteRule.FailClosed);
        rules.Reads.Should().Be(ReadRule.UnionNativeFirst);
        rules.Acting.Should().Match<ActedState>(a => a.Ep == 5 && a.State == "S3");

        _reader.State = FakeStateReader.Record("S3", 6, "next");
        await monitor.StepAsync(Group, CancellationToken.None);
        monitor.RulesFor(Group).Should().Match<GroupRules>(r => r.Writes == WriteRule.Follow && r.Acting!.Ep == 6);
    }

    [Fact]
    public async Task A_listed_or_present_group_is_reconciled_with_a_sample_of_every_master_and_an_unlisted_absent_one_only_probed()
    {
        var listed = Monitor(listed: true);
        _reader.State = FakeStateReader.Record("S1", 5);
        await listed.StepAsync(Group, CancellationToken.None);
        _reader.Samples.Should().Be(1);

        var other = new GroupStateMonitor(_reader, _options, _time);
        other.Register([new GroupRegistration("g2", false, false)]);
        _reader.State = null;
        await other.StepAsync("g2", CancellationToken.None);

        _reader.Samples.Should().Be(1, "an unlisted group with no hash only asks whether a record exists");
        _reader.Probes.Should().Be(1);
    }

    [Fact]
    public async Task While_a_conflict_stands_every_step_samples_again_and_it_clears_when_the_state_catches_up()
    {
        var monitor = Monitor(listed: true);
        _reader.State = FakeStateReader.Record("S1", 5);
        _reader.Sample = new ControlSample(7, "newer", "S3", "fwd", true);
        await monitor.StepAsync(Group, CancellationToken.None);
        await monitor.StepAsync(Group, CancellationToken.None);
        _reader.Samples.Should().Be(2);
        monitor.RulesFor(Group).Alerts.Should().Contain("identity_conflict");

        _reader.State = FakeStateReader.Record("S3", 7, "newer");
        await monitor.StepAsync(Group, CancellationToken.None);

        monitor.RulesFor(Group).Should().Match<GroupRules>(r => r.Writes == WriteRule.Follow && r.Alerts.Count == 0);
    }

    [Fact]
    public async Task A_group_that_once_showed_a_state_stays_relevant_when_a_flush_makes_it_absent()
    {
        var monitor = Monitor(listed: false);
        _reader.State = FakeStateReader.Record("S0", 1);
        await monitor.StepAsync(Group, CancellationToken.None);
        _reader.State = FakeStateReader.Record("S2", 2);
        await monitor.StepAsync(Group, CancellationToken.None);

        _reader.State = null;
        var next = await monitor.StepAsync(Group, CancellationToken.None);

        next.Should().Be(TimeSpan.FromSeconds(2));
        monitor.RulesFor(Group).Should().Match<GroupRules>(r => r.Relevant && r.Writes == WriteRule.FailClosed && r.Reads == ReadRule.UnionNativeFirst && r.Alerts.Contains("state_missing"));
    }

    [Theory]
    [InlineData("S0")]
    [InlineData("S3")]
    public async Task A_record_at_the_epoch_of_the_state_under_another_id_is_an_identity_conflict_and_the_state_is_not_acted_on(string state)
    {
        var monitor = Monitor(listed: true);
        _reader.State = FakeStateReader.Record(state, 5, "A");
        _reader.Sample = new ControlSample(5, "B", state, "fwd", true);

        await monitor.StepAsync(Group, CancellationToken.None);

        var rules = monitor.RulesFor(Group);
        rules.Alerts.Should().ContainSingle().Which.Should().Be("identity_conflict");
        rules.Writes.Should().Be(WriteRule.FailClosed);
        rules.Acting.Should().BeNull();
        monitor.Acknowledge(Group, 5, "A").Should().BeFalse();
    }

    [Fact]
    public async Task A_record_above_the_state_is_an_identity_conflict_and_the_node_does_not_adopt_the_lower_state()
    {
        var monitor = Monitor(listed: true);
        _reader.State = FakeStateReader.Record("S1", 5);
        _reader.Sample = new ControlSample(7, "newer", "S3", "fwd", true);

        await monitor.StepAsync(Group, CancellationToken.None);

        var rules = monitor.RulesFor(Group);
        rules.Alerts.Should().ContainSingle().Which.Should().Be("identity_conflict");
        rules.Writes.Should().Be(WriteRule.FailClosed);
        rules.Acting.Should().BeNull();
    }

    [Fact]
    public async Task A_committed_record_under_an_uncommitted_state_is_a_commit_mismatch_that_closes_writes_at_once()
    {
        var monitor = Monitor(listed: true);
        _reader.State = FakeStateReader.Record("S1", 6, "t", durable: false);
        _reader.Sample = new ControlSample(6, "t", "S1", "fwd", true);

        await monitor.StepAsync(Group, CancellationToken.None);

        var rules = monitor.RulesFor(Group);
        rules.Alerts.Should().ContainSingle().Which.Should().Be("commit_mismatch");
        rules.Writes.Should().Be(WriteRule.FailClosed);

        _reader.State = FakeStateReader.Record("S1", 6, "t");
        await monitor.StepAsync(Group, CancellationToken.None);
        monitor.RulesFor(Group).Writes.Should().Be(WriteRule.Follow);
    }

    [Fact]
    public async Task The_acknowledged_identity_is_for_the_identity_the_node_acts_under_and_a_new_identity_clears_it()
    {
        var monitor = Monitor(listed: true);
        monitor.AckedFor(Group).Should().BeNull();
        _reader.State = FakeStateReader.Record("S1", 5, "a");
        await monitor.StepAsync(Group, CancellationToken.None);

        monitor.Acknowledge(Group, 5, "wrong").Should().BeFalse();
        monitor.Acknowledge("nobody", 5, "a").Should().BeFalse();
        monitor.AckedFor(Group).Should().BeNull();
        monitor.Acknowledge(Group, 5, "a").Should().BeTrue();
        monitor.AckedFor(Group).Should().Be(new AckedIdentity(5, "a", "S1", "fwd"));

        _reader.State = FakeStateReader.Record("S2", 6, "b");
        await monitor.StepAsync(Group, CancellationToken.None);
        monitor.AckedFor(Group).Should().BeNull();
        monitor.Acknowledge(Group, 6, "b").Should().BeTrue();
        monitor.AckedFor(Group).Should().Be(new AckedIdentity(6, "b", "S2", "fwd"));
    }

    [Fact]
    public async Task An_alert_is_told_once_when_it_becomes_active_and_again_after_it_cleared()
    {
        var monitor = Monitor(listed: true);
        _reader.State = FakeStateReader.Record("S0", 5);
        await monitor.StepAsync(Group, CancellationToken.None);

        _reader.State = null;
        await monitor.StepAsync(Group, CancellationToken.None);
        await monitor.StepAsync(Group, CancellationToken.None);
        _alerts.Should().ContainSingle();

        _reader.State = FakeStateReader.Record("S0", 5);
        await monitor.StepAsync(Group, CancellationToken.None);
        _reader.State = null;
        await monitor.StepAsync(Group, CancellationToken.None);

        _alerts.Should().HaveCount(2);
    }

    [Fact]
    public async Task The_background_loop_reads_every_registered_group_at_startup_retries_every_two_seconds_and_polls_on_the_interval()
    {
        var monitor = new GroupStateMonitor(_reader, _options, _time);
        _reader.Fail = true;
        monitor.Start([new GroupRegistration("a", false, true), new GroupRegistration("b", false, true)], CancellationToken.None);
        await Until(() => _reader.Samples >= 2);

        _reader.Fail = false;
        _reader.State = FakeStateReader.Record("S1", 5);
        await AdvancingUntil(() => monitor.RulesFor("a").Writes == WriteRule.Follow && monitor.RulesFor("b").Writes == WriteRule.Follow);

        var reads = _reader.Reads;
        await AdvancingUntil(() => _reader.Reads >= reads + 2);
        await monitor.StopAsync();
    }

    [Fact]
    public async Task A_state_that_is_not_durable_is_polled_every_two_seconds_whatever_stage_it_names()
    {
        var monitor = Monitor(listed: true);
        _reader.State = FakeStateReader.Record("S4", 5);
        await monitor.StepAsync(Group, CancellationToken.None);

        _reader.State = FakeStateReader.Record("S5", 6, "next", durable: false);
        var next = await monitor.StepAsync(Group, CancellationToken.None);

        next.Should().Be(TimeSpan.FromSeconds(2), "the node still acts under S4 and must see dur=1 within a poll");
        _time.Advance(TimeSpan.FromSeconds(61));
        monitor.RulesFor(Group).Alerts.Should().Contain("snapshot_stale", "StaleAfter follows the 2 s poll of the stage in force, not the 60 s of the newest read");

        _reader.State = FakeStateReader.Record("S5", 6, "next");
        (await monitor.StepAsync(Group, CancellationToken.None)).Should().Be(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task The_records_are_read_before_the_state_so_a_late_transition_never_looks_like_a_conflict()
    {
        var monitor = Monitor(listed: true);
        _reader.State = FakeStateReader.Record("S1", 5);

        await monitor.StepAsync(Group, CancellationToken.None);

        _reader.Calls.Should().Equal("sample", "read");

        var unlisted = new GroupStateMonitor(_reader, _options, _time);
        unlisted.Register([new GroupRegistration("g2", false, false)]);
        _reader.Calls.Clear();
        await unlisted.StepAsync("g2", CancellationToken.None);
        _reader.Calls.Should().Equal("probe", "read", "sample", "read");
    }

    [Fact]
    public async Task A_faulted_callback_or_step_is_reported_and_the_loop_goes_on()
    {
        var monitor = new GroupStateMonitor(_reader, _options, _time, (_, _) => throw new InvalidOperationException("the callback is broken"));
        _reader.Fail = true;
        monitor.Start([new GroupRegistration("a", false, true)], CancellationToken.None);
        await TimeSteps.UntilAsync(() => _reader.Samples >= 1);

        _reader.Fail = false;
        _reader.State = FakeStateReader.Record("S1", 5);
        await AdvancingUntil(() => monitor.RulesFor("a").Writes == WriteRule.Follow);

        var reads = _reader.Reads;
        await AdvancingUntil(() => _reader.Reads >= reads + 2);
        await monitor.StopAsync();
    }

    [Fact]
    public async Task A_step_that_throws_what_the_reader_should_not_is_retried_after_the_retry_interval()
    {
        var reader = new ThrowingReader();
        var monitor = new GroupStateMonitor(reader, _options, _time);
        monitor.Start([new GroupRegistration("a", false, true)], CancellationToken.None);
        await TimeSteps.UntilAsync(() => reader.Reads >= 1);

        await AdvancingUntil(() => reader.Reads >= 3);

        await monitor.StopAsync();
    }

    [Fact]
    public async Task The_monitor_cannot_be_started_twice_and_a_refused_start_leaves_nothing_running()
    {
        var monitor = new GroupStateMonitor(_reader, _options, _time);
        var bad = () => monitor.Start([new GroupRegistration("bad+name", false, true)], CancellationToken.None);
        bad.Should().Throw<ArgumentException>();

        monitor.Start([new GroupRegistration("a", false, true)], CancellationToken.None);
        var again = () => monitor.Start([new GroupRegistration("b", false, true)], CancellationToken.None);
        again.Should().Throw<InvalidOperationException>();

        await monitor.StopAsync();
        monitor.Start([new GroupRegistration("c", false, true)], CancellationToken.None);
        await monitor.StopAsync();
    }

    [Fact]
    public void A_group_name_the_names_cannot_hold_is_refused()
    {
        var monitor = new GroupStateMonitor(_reader, _options, _time);

        var act = () => monitor.Start([new GroupRegistration("bad{name}", false, true)], CancellationToken.None);

        act.Should().Throw<ArgumentException>();
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var tries = 0; !condition(); tries++)
        {
            tries.Should().BeLessThan(1000);
            await Task.Delay(10);
        }
    }

    // The loop may not have armed its next delay when the clock moves, so the clock moves again until the condition holds.
    private async Task AdvancingUntil(Func<bool> condition)
    {
        for (var tries = 0; !condition(); tries++)
        {
            tries.Should().BeLessThan(1000);
            _time.Advance(TimeSpan.FromSeconds(2));
            await Task.Delay(10);
        }
    }

    // The monitor is registered without running its loop; a test drives it with StepAsync.
    private GroupStateMonitor Monitor(bool listed, bool flagged = false)
    {
        var monitor = new GroupStateMonitor(_reader, _options, _time, (g, a) => _alerts.Add((g, a)));
        monitor.Register([new GroupRegistration(Group, flagged, listed)]);
        return monitor;
    }

    private sealed class ThrowingReader : IStateReader
    {
        private int _reads;

        public int Reads => Volatile.Read(ref _reads);

        public Task<StateRecord?> ReadAsync(string group, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _reads);
            throw new InvalidCastException("not a Redis failure");
        }

        public Task<ControlSample?> ProbeAsync(string group, CancellationToken cancellationToken) => Task.FromResult<ControlSample?>(null);

        public Task<ControlSample?> SampleAsync(string group, CancellationToken cancellationToken) => Task.FromResult<ControlSample?>(null);
    }
}
