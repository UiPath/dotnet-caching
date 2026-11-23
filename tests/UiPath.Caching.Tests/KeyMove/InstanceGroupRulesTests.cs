using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Tests.KeyMove;

/// <summary>The Dst availability table for an instance group, driven through the monitor with a reader whose Dst can go down.</summary>
public sealed class InstanceGroupRulesTests
{
    private const string Group = "g1";

    private readonly InstanceReader _reader = new();
    private readonly ManualTimeProvider _time = new(DateTimeOffset.UnixEpoch);
    private readonly KeyMoveStateOptions _options = new();

    [Fact]
    public async Task A_dst_that_is_down_keeps_the_last_known_rules_with_home_on_the_src_and_the_node_stays_ready()
    {
        var monitor = await MonitorAtAsync(State("S1", 5, nPop: 0));
        _reader.DstDown = true;

        await monitor.StepAsync(Group, TestContext.Current.CancellationToken);

        var rules = monitor.RulesFor(Group);
        rules.Should().Match<GroupRules>(r => r.Reads == ReadRule.Follow && r.Writes == WriteRule.Follow && r.Ready && !r.WithdrawAcks);
        rules.Alerts.Should().Equal("dst_unreachable");
        rules.Acting.Should().Be(new ActedState("S1", "fwd", 5, "id", 0, false));
    }

    [Fact]
    public async Task A_stale_dst_with_npop_zero_keeps_the_rules_and_a_single_server_group_still_goes_through_the_stale_row()
    {
        var instance = await MonitorAtAsync(State("S1", 5, nPop: 0));
        var plain = Monitor(instance: false);
        _reader.DstDown = false;
        await plain.StepAsync(Group, TestContext.Current.CancellationToken);
        _reader.DstDown = true;
        await instance.StepAsync(Group, TestContext.Current.CancellationToken);
        await plain.StepAsync(Group, TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromSeconds(70));

        instance.RulesFor(Group).Alerts.Should().Equal("dst_unreachable");
        instance.RulesFor(Group).Writes.Should().Be(WriteRule.Follow);
        plain.RulesFor(Group).Alerts.Should().Equal(["snapshot_stale"], "the single-server table is unchanged");
    }

    [Fact]
    public async Task A_state_that_holds_data_on_the_dst_fails_closed_when_the_dst_is_stale()
    {
        var monitor = await MonitorAtAsync(State("S3", 5, nPop: 1));
        _reader.DstDown = true;

        await monitor.StepAsync(Group, TestContext.Current.CancellationToken);

        monitor.RulesFor(Group).Should().Match<GroupRules>(r => r.Reads == ReadRule.UnionNativeFirst && r.Writes == WriteRule.FailClosed && r.WithdrawAcks);
    }

    [Theory]
    [InlineData(0, true, "Follow", "Follow")]
    [InlineData(1, true, "UnionNativeFirst", "FailClosed")]
    public async Task A_newer_src_sample_with_dur_one_is_adopted_for_np_zero_and_closed_for_np_one(int np, bool durable, string reads, string writes)
    {
        var monitor = await MonitorAtAsync(State("S0", 5, nPop: 0, direction: "none"));
        _reader.DstDown = true;
        _reader.SampledRecord = Sample(6, "n", np == 0 ? "S1" : "S3", durable, np);

        await monitor.StepAsync(Group, TestContext.Current.CancellationToken);

        var rules = monitor.RulesFor(Group);
        rules.Reads.ToString().Should().Be(reads);
        rules.Writes.ToString().Should().Be(writes);
        if (np == 0)
        {
            rules.Acting.Should().Be(new ActedState("S1", "fwd", 6, "n", 0, false), "the exact identity of the Src record");
        }
        else
        {
            rules.Acting!.Ep.Should().Be(5, "np=1 keeps the old identity in view");
        }
    }

    [Fact]
    public async Task A_newer_src_sample_with_dur_zero_waits_the_transition_wait_then_fails_closed_while_reads_follow_the_last_known_identity()
    {
        var monitor = await MonitorAtAsync(State("S0", 5, nPop: 0, direction: "none"));
        _reader.DstDown = true;
        _reader.SampledRecord = Sample(6, "n", "S3", durable: false, np: 1);

        await monitor.StepAsync(Group, TestContext.Current.CancellationToken);

        var waiting = monitor.RulesFor(Group);
        waiting.Should().Match<GroupRules>(r => r.Reads == ReadRule.Follow && r.Writes == WriteRule.WaitThenFailClosed);
        waiting.WriteWait.Should().BeCloseTo(_options.TransitionWait, TimeSpan.FromSeconds(1));
        waiting.Acting!.Ep.Should().Be(5);

        _time.Advance(_options.TransitionWait + TimeSpan.FromSeconds(1));

        monitor.RulesFor(Group).Should().Match<GroupRules>(r => r.Reads == ReadRule.Follow && r.Writes == WriteRule.FailClosed);
    }

    [Theory]
    [InlineData(false, true, 0, false)]
    [InlineData(true, false, 0, false)]
    [InlineData(true, true, 1, false)]
    [InlineData(true, true, -1, false)]
    [InlineData(true, true, 0, true)]
    public void The_np_zero_src_fallback_needs_a_src_sample_dur_one_and_np_zero(bool srcHome, bool srcDurable, int np, bool allowed)
    {
        var sample = new ControlSample(6, "n", "S1", "fwd", true, NPop: np, SrcHome: srcHome, SrcDurable: srcDurable);

        GroupRuleEvaluator.SrcFallbackAdopts(sample, new ActedState("S0", "none", 5, "id", 0, false)).Should().Be(allowed);
    }

    [Fact]
    public void The_fallback_is_refused_for_records_that_disagree_and_for_an_identity_that_is_not_newer()
    {
        var sample = new ControlSample(6, "n", "S1", "fwd", true, NPop: 0, SrcHome: true, SrcDurable: true);

        GroupRuleEvaluator.SrcFallbackAdopts(sample with { Conflicting = true }, null).Should().BeFalse();
        GroupRuleEvaluator.SrcFallbackAdopts(sample, new ActedState("S1", "fwd", 6, "n", 0, false)).Should().BeFalse();
        GroupRuleEvaluator.SrcFallbackAdopts(sample, null).Should().BeTrue();
    }

    [Fact]
    public async Task A_sample_whose_identity_only_the_dst_holds_leaves_the_last_known_rules_in_force()
    {
        var monitor = await MonitorAtAsync(State("S0", 5, nPop: 0, direction: "none"));
        _reader.DstDown = true;
        _reader.SampledRecord = new ControlSample(6, "n", "S1", "fwd", false, NPop: 0);

        await monitor.StepAsync(Group, TestContext.Current.CancellationToken);

        monitor.RulesFor(Group).Should().Match<GroupRules>(r => r.Writes == WriteRule.Follow && r.Acting!.Ep == 5);
    }

    [Fact]
    public async Task Startup_with_no_dst_and_no_record_on_the_src_is_s0_and_with_a_blind_src_fails_closed()
    {
        var monitor = Monitor(instance: true);
        _reader.DstDown = true;

        await monitor.StepAsync(Group, TestContext.Current.CancellationToken);
        monitor.RulesFor(Group).Should().Match<GroupRules>(r => r.Writes == WriteRule.Follow && r.Acting == null && r.Ready);
        monitor.RulesFor(Group).Alerts.Should().Equal("dst_unreachable");

        var blind = Monitor(instance: true);
        _reader.SrcDown = true;
        await blind.StepAsync(Group, TestContext.Current.CancellationToken);
        blind.RulesFor(Group).Should().Match<GroupRules>(r => r.Writes == WriteRule.FailClosed && r.Alerts.Contains("no_snapshot"));
    }

    [Fact]
    public async Task Startup_with_no_dst_and_no_last_known_state_adopts_a_matching_durable_src_record()
    {
        var monitor = Monitor(instance: true);
        _reader.DstDown = true;
        _reader.SampledRecord = Sample(6, "n", "S1", durable: true, np: 0);

        await monitor.StepAsync(Group, TestContext.Current.CancellationToken);

        monitor.RulesFor(Group).Should().Match<GroupRules>(r => r.Reads == ReadRule.Follow && r.Writes == WriteRule.Follow && r.Acting!.Ep == 6);
    }

    [Fact]
    public async Task A_fresh_dst_read_with_a_lower_ep_than_the_sample_stays_identity_conflict()
    {
        var monitor = Monitor(instance: true);
        _reader.StoredState = State("S1", 5, nPop: 0);
        _reader.SampledRecord = Sample(9, "other", "S1", durable: true, np: 0);

        await monitor.StepAsync(Group, TestContext.Current.CancellationToken);

        monitor.RulesFor(Group).Should().Match<GroupRules>(r => r.Writes == WriteRule.FailClosed && r.Alerts.Contains("identity_conflict"));
    }

    [Fact]
    public async Task The_dst_coming_back_replaces_the_adopted_identity_with_its_own_state()
    {
        var monitor = await MonitorAtAsync(State("S0", 5, nPop: 0, direction: "none"));
        _reader.DstDown = true;
        _reader.SampledRecord = Sample(6, "n", "S1", durable: true, np: 0);
        await monitor.StepAsync(Group, TestContext.Current.CancellationToken);

        _reader.DstDown = false;
        _reader.StoredState = State("S1", 6, nPop: 0, id: "n");
        await monitor.StepAsync(Group, TestContext.Current.CancellationToken);

        monitor.RulesFor(Group).Should().Match<GroupRules>(r => r.Writes == WriteRule.Follow && r.Alerts.Count == 0 && r.Acting!.Ep == 6);
    }

    private static StateRecord State(string state, long ep, int nPop, string id = "id", string direction = "fwd") =>
        FakeStateReader.Record(state, ep, id, direction: direction, nPop: nPop);

    private static ControlSample Sample(long ep, string id, string state, bool durable, int np) =>
        new(ep, id, state, "fwd", durable, NPop: np, SrcHome: true, SrcDurable: durable);

    private async Task<GroupStateMonitor> MonitorAtAsync(StateRecord state)
    {
        var monitor = Monitor(instance: true);
        _reader.StoredState = state;
        await monitor.StepAsync(Group, TestContext.Current.CancellationToken);
        return monitor;
    }

    private GroupStateMonitor Monitor(bool instance)
    {
        var monitor = new GroupStateMonitor(_reader, _options, _time);
        monitor.Register([new GroupRegistration(Group, false, true, InstanceMove: instance)]);
        return monitor;
    }

    // The Dst state and the Src sample as two servers that fail separately.
    private sealed class InstanceReader : IStateReader
    {
        public StateRecord? StoredState { get; set; }

        public ControlSample? SampledRecord { get; set; }

        public bool DstDown { get; set; }

        public bool SrcDown { get; set; }

        public Task<StateRecord?> ReadAsync(string group, CancellationToken cancellationToken) =>
            DstDown ? Task.FromException<StateRecord?>(new TimeoutException("the Dst is down")) : Task.FromResult(StoredState);

        public Task<ControlSample?> ProbeAsync(string group, CancellationToken cancellationToken) => Answer();

        public Task<ControlSample?> SampleAsync(string group, CancellationToken cancellationToken) => Answer();

        private Task<ControlSample?> Answer() =>
            SrcDown ? Task.FromException<ControlSample?>(new TimeoutException("the Src is down")) : Task.FromResult(SampledRecord);
    }
}
