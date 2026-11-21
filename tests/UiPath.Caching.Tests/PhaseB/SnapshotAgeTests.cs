using UiPath.Caching.PhaseB;
using static UiPath.Caching.Tests.PhaseB.PlannerKit;

namespace UiPath.Caching.Tests.PhaseB;

// G7 and the snap token: the node reports the age of its oldest snapshot, bucketed up to 5 seconds, and a node that reports none fails the gate.
public class SnapshotAgeTests
{
    private readonly FakeStateReader _reader = new() { State = FakeStateReader.Record("S1", 5) };
    private readonly ManualTimeProvider _time = new(DateTimeOffset.UnixEpoch);

    [Theory]
    [InlineData(0, "snap=0")]
    [InlineData(0.2, "snap=5")]
    [InlineData(5, "snap=5")]
    [InlineData(5.1, "snap=10")]
    [InlineData(12.2, "snap=15")]
    [InlineData(60, "snap=60")]
    public void The_token_rounds_the_age_up_to_the_next_five_seconds_so_it_never_understates(double seconds, string expected) =>
        NodeAdvert.Snapshot(TimeSpan.FromSeconds(seconds)).Should().Be(expected);

    [Fact]
    public async Task The_age_is_null_until_a_relevant_group_has_read_and_then_follows_the_clock()
    {
        var monitor = new GroupStateMonitor(_reader, new PhaseBStateOptions(), _time);
        monitor.Register([new GroupRegistration("g1", false, true)]);
        monitor.SnapshotAge().Should().BeNull();

        await monitor.StepAsync("g1", CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(12));

        monitor.SnapshotAge().Should().Be(TimeSpan.FromSeconds(12));
    }

    [Fact]
    public async Task The_oldest_snapshot_counts_and_a_relevant_group_that_never_read_means_no_snapshot()
    {
        var monitor = new GroupStateMonitor(_reader, new PhaseBStateOptions(), _time);
        monitor.Register([new GroupRegistration("g1", false, true), new GroupRegistration("g2", false, true)]);
        await monitor.StepAsync("g1", CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(20));
        monitor.SnapshotAge().Should().BeNull("g2 has not read");

        await monitor.StepAsync("g2", CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(3));

        monitor.SnapshotAge().Should().Be(TimeSpan.FromSeconds(23));
    }

    [Fact]
    public async Task A_group_that_is_not_relevant_does_not_age_the_node()
    {
        _reader.State = null;
        var monitor = new GroupStateMonitor(_reader, new PhaseBStateOptions(), _time);
        monitor.Register([new GroupRegistration("quiet", false, false)]);
        await monitor.StepAsync("quiet", CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(10));

        monitor.SnapshotAge().Should().BeNull();
    }

    [Fact]
    public async Task The_advert_carries_the_bucketed_age_and_carries_none_while_the_node_has_no_snapshot()
    {
        var monitor = new GroupStateMonitor(_reader, new PhaseBStateOptions(), _time);
        var registrations = new[] { new GroupRegistration("g1", false, true) };
        monitor.Register(registrations);
        var tokens = new PhaseBAdvertTokens(registrations, _ => ["a"], new StageFlagPoller(new FixedStage(), _ => true, new PhaseBStateOptions(), _time), monitor, new EpochGate(), monitor);

        var before = new List<string>();
        tokens.AddTo(before);
        await monitor.StepAsync("g1", CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(7));
        var after = new List<string>();
        tokens.AddTo(after);

        before.Should().NotContain(t => t.StartsWith("snap=", StringComparison.Ordinal));
        after.Should().Contain("snap=10");
    }

    [Fact]
    public async Task G7_fails_a_node_with_a_stale_snapshot_and_a_node_that_reports_none_and_passes_one_at_the_limit()
    {
        var state = ReadyOff();
        (await Decide(state, [Advert("n1"), Advert("n2", snap: 60)])).AlertList.Select(a => a.Code).Should().NotContain("start_blocked");
        Blocked(await Decide(state, [Advert("n1"), Advert("n2", snap: 65)]), "G7", "n2");
        Blocked(await Decide(state, [Advert("n1"), Advert("n2", snap: null)]), "G7", "n2");
    }

    [Fact]
    public async Task G7_adds_the_age_of_the_advert_to_the_snapshot_age_it_carries()
    {
        var state = ReadyOff();
        (await Decide(state, [Advert("n1"), Advert("n2", snap: 40) with { Age = TimeSpan.FromSeconds(20) }])).AlertList.Select(a => a.Code).Should().NotContain("start_blocked");
        Blocked(await Decide(state, [Advert("n1"), Advert("n2", snap: 60) with { Age = TimeSpan.FromSeconds(5) }]), "G7", "n2");
    }

    [Fact]
    public async Task A_slow_polled_group_does_not_age_the_node_while_a_fast_one_still_counts()
    {
        var reader = new PerGroupReader(new Dictionary<string, StateRecord>
        {
            ["fast"] = FakeStateReader.Record("S1", 5, "id1"),
            ["closed"] = FakeStateReader.Record("S5", 6, "id2", swept: "id2"),
        });
        var monitor = new GroupStateMonitor(reader, new PhaseBStateOptions(), _time);
        monitor.Register([new GroupRegistration("fast", false, true), new GroupRegistration("closed", false, true)]);
        await monitor.StepAsync("closed", CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(50));
        monitor.SnapshotAge().Should().BeNull("the fast group has not read yet");
        await monitor.StepAsync("fast", CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(4));

        monitor.SnapshotAge().Should().Be(TimeSpan.FromSeconds(4), "the closed group's 54 s is within its own 180 s limit and is left out");
    }

    private static Task<StepPlan> Decide(StateRecord state, IReadOnlyList<NodeAdvert> fresh) => StepPlanner.DecideAsync(Input(state, fresh), Defaults);

    private static void Blocked(StepPlan plan, string gate, string node)
    {
        plan.Kind.Should().Be(StepKind.None);
        var alert = plan.AlertList.Single(a => a.Code == "start_blocked");
        alert.Detail.Should().Contain(gate).And.Contain(node);
    }

    private sealed class FixedStage : IMigrationStageSource
    {
        public ValueTask<int?> GetStageAsync(string group, CancellationToken cancellationToken) => ValueTask.FromResult<int?>(3);
    }

    private sealed class PerGroupReader(Dictionary<string, StateRecord> states) : IStateReader
    {
        public Task<StateRecord?> ReadAsync(string group, CancellationToken cancellationToken) => Task.FromResult(states.GetValueOrDefault(group));

        public Task<ControlSample?> ProbeAsync(string group, CancellationToken cancellationToken) => Task.FromResult<ControlSample?>(null);

        public Task<ControlSample?> SampleAsync(string group, CancellationToken cancellationToken) => Task.FromResult<ControlSample?>(null);
    }
}
