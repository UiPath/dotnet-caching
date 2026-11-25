using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Tests.KeyMove;

// The node's acknowledgement: only for a durable identity, only after the older units drained, and withdrawn by the monitor's own rules.
public class AckCoordinatorTests
{
    private const string Group = "g1";

    private readonly FakeStateReader _reader = new();
    private readonly ManualTimeProvider _time = new(DateTimeOffset.UnixEpoch);
    private readonly EpochGate _gate = new();
    private readonly GroupStateMonitor _monitor;
    private readonly AckCoordinator _acks;

    public AckCoordinatorTests()
    {
        _monitor = new GroupStateMonitor(_reader, new KeyMoveStateOptions(), _time);
        _monitor.Register([new GroupRegistration(Group, false, true)]);
        _acks = new AckCoordinator(_monitor, _gate, new StageDriverOptions(), _time);
    }

    [Fact]
    public async Task A_node_acknowledges_the_durable_identity_when_no_unit_is_older()
    {
        _reader.State = FakeStateReader.Record("S1", 5);
        await _monitor.StepAsync(Group, CancellationToken.None);

        _acks.Tick(Group).Should().BeTrue();

        _monitor.AckedFor(Group).Should().Be(new AckedIdentity(5, "id", "S1", "fwd"));
    }

    [Fact]
    public async Task An_identity_at_dur_0_is_never_acknowledged()
    {
        _reader.State = FakeStateReader.Record("S1", 5, durable: false);
        await _monitor.StepAsync(Group, CancellationToken.None);

        _acks.Tick(Group).Should().BeFalse();
        _monitor.AckedFor(Group).Should().BeNull();
    }

    [Fact]
    public async Task The_ack_of_a_new_identity_waits_for_units_admitted_under_the_old_one_to_complete()
    {
        _reader.State = FakeStateReader.Record("S1", 5, "a");
        await _monitor.StepAsync(Group, CancellationToken.None);
        _acks.Tick(Group);
        var unit = _gate.Enter(Group)!;

        _reader.State = FakeStateReader.Record("S3", 6, "b", nPop: 1);
        await _monitor.StepAsync(Group, CancellationToken.None);

        _acks.Tick(Group).Should().BeFalse();
        _monitor.AckedFor(Group).Should().BeNull("the node withdraws the old ack and advertises <g>@0 until it has drained");

        unit.Dispose();

        _acks.Tick(Group).Should().BeTrue();
        _monitor.AckedFor(Group).Should().Be(new AckedIdentity(6, "b", "S3", "fwd"));
    }

    [Fact]
    public async Task A_unit_that_is_readmitted_under_the_new_identity_lets_the_node_acknowledge_it()
    {
        _reader.State = FakeStateReader.Record("S1", 5, "a");
        await _monitor.StepAsync(Group, CancellationToken.None);
        _acks.Tick(Group);
        var unit = _gate.Enter(Group)!;
        _reader.State = FakeStateReader.Record("S3", 6, "b", nPop: 1);
        await _monitor.StepAsync(Group, CancellationToken.None);
        _acks.Tick(Group).Should().BeFalse();

        unit.Readmit().Should().BeTrue();

        _acks.Tick(Group).Should().BeTrue();
        unit.Dispose();
    }

    [Fact]
    public async Task A_node_whose_snapshot_is_stale_advertises_no_identity_even_if_it_had_acknowledged_one()
    {
        _reader.State = FakeStateReader.Record("S1", 5);
        await _monitor.StepAsync(Group, CancellationToken.None);
        _acks.Tick(Group);

        _time.Advance(TimeSpan.FromSeconds(61));

        _monitor.AckedFor(Group).Should().BeNull();
    }

    [Fact]
    public void A_group_the_node_knows_nothing_about_has_no_ack()
    {
        _acks.Tick(Group).Should().BeFalse();
        _monitor.AckedFor(Group).Should().BeNull();
    }

    [Fact]
    public async Task The_ack_loop_acknowledges_on_its_own_ticks_and_stops_with_its_token()
    {
        _reader.State = FakeStateReader.Record("S1", 5);
        await _monitor.StepAsync(Group, CancellationToken.None);
        using var stop = new CancellationTokenSource();
        var loop = _acks.RunAsync([Group], stop.Token);

        await TimeSteps.ArmedAsync(_time);
        _monitor.AckedFor(Group).Should().NotBeNull();
        await stop.CancelAsync();

        await loop;
    }
}
