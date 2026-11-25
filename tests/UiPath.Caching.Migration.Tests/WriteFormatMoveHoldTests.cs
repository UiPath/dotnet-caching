using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging.Abstractions;
using UiPath.Caching.KeyMove;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests;

// A group whose keys move keeps its write format: the switch it asks for waits until the move is over, and the wait is counted and alerted.
public sealed class WriteFormatMoveHoldTests
{
    private static readonly ManualTimeProvider Clock = new(new DateTimeOffset(2026, 11, 25, 0, 0, 0, TimeSpan.Zero));

    [Theory]
    [InlineData("S1", "fwd")]
    [InlineData("S2", "fwd")]
    [InlineData("S3", "fwd")]
    [InlineData("S4", "fwd")]
    [InlineData("S1", "rev")]
    [InlineData("S3", "rev")]
    [InlineData("S4", "rev")]
    public void A_group_that_asks_for_messagepack_while_its_keys_move_keeps_the_envelope_and_does_not_ask_for_the_switch(string state, string direction)
    {
        var (selector, pin, alerts) = Build(_ => Acting(state, direction));

        using var lease = selector.Select(pin);

        lease.Policy.Should().Be(WriteFormats.Envelope);
        pin.Wanted.Should().BeFalse("the switch is not asked for while the keys move");
        var alert = alerts.Should().ContainSingle().Which;
        (alert.Group, alert.Code, alert.Detail).Should().Be(("g", "format_switch_held", "messagepack"), "the alert names the group and the pending format");
    }

    [Theory]
    [InlineData("S0")]
    [InlineData("S5")]
    public void A_group_whose_move_has_not_started_or_is_over_asks_for_the_switch(string state)
    {
        var (selector, pin, alerts) = Build(_ => Acting(state, "fwd"));

        using var lease = selector.Select(pin);

        pin.Wanted.Should().BeTrue();
        alerts.Should().BeEmpty();
    }

    [Fact]
    public void A_group_that_takes_no_part_in_a_move_asks_for_the_switch()
    {
        var (selector, pin, alerts) = Build(_ => GroupRules.Unaffected);

        using var lease = selector.Select(pin);

        pin.Wanted.Should().BeTrue();
        alerts.Should().BeEmpty();
    }

    [Fact]
    public void The_hold_is_counted_per_write_alerted_once_and_ends_with_the_move_so_the_next_write_asks_without_a_restart()
    {
        var rules = Acting("S3", "fwd");
        var group = $"hold-{Guid.NewGuid():N}";
        var (selector, pin, alerts) = Build(_ => rules, group);
        var (other, otherPin, _) = Build(_ => rules);
        var held = new List<(string Group, string Pending)>();
        using var listener = Listen(held, group);

        selector.Select(pin).Dispose();
        other.Select(otherPin).Dispose();
        selector.Select(pin).Dispose();

        held.Should().Equal((group, "messagepack"), (group, "messagepack"));
        alerts.Should().ContainSingle("a held group is alerted once");
        pin.Wanted.Should().BeFalse();

        rules = Acting("S0", "fwd");
        selector.Select(pin).Dispose();

        pin.Wanted.Should().BeTrue("the move is over, so the same node asks for the switch on its next write");
        held.Should().HaveCount(2);

        rules = Acting("S3", "fwd");
        selector.Select(pin).Dispose();

        alerts.Should().HaveCount(2, "a new move holds the group again and alerts again");
    }

    [Theory]
    [InlineData("json", false)]
    [InlineData(null, false)]
    [InlineData("unknown", false)]
    [InlineData(null, true)]
    public void A_later_move_alerts_again_after_a_write_stops_asking_for_messagepack(string? answer, bool throws)
    {
        var rules = Acting("S3", "fwd");
        string? format = "messagepack";
        var unavailable = false;
        var (selector, pin, alerts) = Build(_ => rules, format: () => unavailable ? throw new InvalidOperationException("setting unavailable") : format);

        selector.Select(pin).Dispose();
        alerts.Should().ContainSingle();

        rules = Acting("S0", "fwd");
        format = answer;
        unavailable = throws;
        selector.Select(pin).Dispose();

        rules = Acting("S3", "fwd");
        format = "messagepack";
        unavailable = false;
        selector.Select(pin).Dispose();

        alerts.Should().HaveCount(2);
    }

    [Fact]
    public void A_node_without_key_move_never_holds_the_switch()
    {
        var pins = new PinCoordinator(new RouterCore(Clock, _ => true, NullLogger.Instance), _ => true, new FakePinStore(), null, TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(2), NullLogger.Instance);
        var selector = new WriteFormatSelector(_ => "messagepack", pins, NullLogger.Instance);
        var pin = pins.GroupFor("g");

        selector.Select(pin).Dispose();

        pin.Wanted.Should().BeTrue();
    }

    // A node that restarts in the middle of a move has not read the group's state yet: it holds until its first read, and then by the state it read.
    [Fact]
    public async Task A_node_restarted_during_S3_does_not_switch_before_its_first_read_of_the_state()
    {
        var reader = new HeldReader();
        var monitor = new GroupStateMonitor(reader, new KeyMoveStateOptions { PollInterval = TimeSpan.FromMilliseconds(20) }, TimeProvider.System);
        monitor.Start([new GroupRegistration("g", false, true, ["c"])], TestContext.Current.CancellationToken);
        try
        {
            var pins = new PinCoordinator(new RouterCore(Clock, _ => true, NullLogger.Instance), _ => true, new FakePinStore(), null, TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(2), NullLogger.Instance);
            var selector = new WriteFormatSelector(_ => "messagepack", pins, NullLogger.Instance) { MoveHold = WriteFormatSelector.HoldFrom(monitor, new HashSet<string>(StringComparer.Ordinal) { "g" }) };
            var pin = pins.GroupFor("g");

            selector.Select(pin).Dispose();
            monitor.HasRead("g").Should().BeFalse();
            pin.Wanted.Should().BeFalse("the node has not read the state yet, so the group may be moving");

            reader.Answer(State("S3"));
            await UntilAsync(() => monitor.HasRead("g"));
            selector.Select(pin).Dispose();
            pin.Wanted.Should().BeFalse("the state the node read is a move");

            reader.Answer(State("S0"));
            await UntilAsync(() => monitor.RulesFor("g").Acting is { State: "S0" });
            selector.Select(pin).Dispose();
            pin.Wanted.Should().BeTrue("the move is over");
        }
        finally
        {
            await monitor.StopAsync();
        }
    }

    [Fact]
    public void A_group_that_is_not_one_of_the_nodes_key_move_groups_is_never_held()
    {
        var monitor = new GroupStateMonitor(new HeldReader(), new KeyMoveStateOptions(), TimeProvider.System);

        WriteFormatSelector.HoldFrom(monitor, new HashSet<string>(StringComparer.Ordinal) { "g" })("other").Should().BeFalse();
        monitor.HasRead("other").Should().BeFalse("the monitor does not track it");
    }

    private static (WriteFormatSelector Selector, GroupPin Pin, List<KeyMoveAlert> Alerts) Build(Func<string, GroupRules> rules, string group = "g", Func<string?>? format = null)
    {
        var alerts = new List<KeyMoveAlert>();
        var pins = new PinCoordinator(new RouterCore(Clock, _ => true, NullLogger.Instance), _ => true, new FakePinStore(), null, TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(2), NullLogger.Instance);
        var selector = new WriteFormatSelector(_ => format is null ? "messagepack" : format(), pins, NullLogger.Instance) { MoveHold = g => WriteFormatSelector.Moving(rules(g)), Alert = alerts.Add };
        return (selector, pins.GroupFor(group), alerts);
    }

    private static GroupRules Acting(string state, string direction) =>
        new(true, ReadRule.Follow, WriteRule.Follow, new ActedState(state, direction, 1, "id1", 1, false), null, false, true, []);

    private static MeterListener Listen(List<(string Group, string Pending)> held, string group)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == RouterTelemetry.MeterName && instrument.Name == "format_switch_held")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            string? measuredGroup = null;
            string? pending = null;
            foreach (var tag in tags)
            {
                measuredGroup = tag.Key == "group" ? (string?)tag.Value : measuredGroup;
                pending = tag.Key == "pending" ? (string?)tag.Value : pending;
            }

            if (measuredGroup == group)
            {
                lock (held)
                {
                    held.Add((group, pending!));
                }
            }
        });
        listener.Start();
        return listener;
    }

    private static StateRecord State(string state) =>
        new(state, "fwd", 1, "id1", true, 1, string.Empty, 7, "c", string.Empty, "id1", string.Empty, string.Empty, string.Empty);

    private static async Task UntilAsync(Func<bool> condition)
    {
        var started = TimeProvider.System.GetTimestamp();
        while (!condition())
        {
            TimeProvider.System.GetElapsedTime(started).Should().BeLessThan(TimeSpan.FromSeconds(10), "the monitor reads within its poll");
            await Task.Delay(TimeSpan.FromMilliseconds(20), TimeProvider.System);
        }
    }

    // A reader that gives no answer until the test sets one, as a node that has restarted and not read the state yet; each read then returns the state set last.
    private sealed class HeldReader : IStateReader
    {
        private readonly TaskCompletionSource _first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private StateRecord? _state;

        public void Answer(StateRecord state)
        {
            Volatile.Write(ref _state, state);
            _first.TrySetResult();
        }

        public async Task<StateRecord?> ReadAsync(string group, CancellationToken cancellationToken)
        {
            await _first.Task.WaitAsync(cancellationToken);
            return Volatile.Read(ref _state);
        }

        public Task<ControlSample?> ProbeAsync(string group, CancellationToken cancellationToken) => Task.FromResult<ControlSample?>(null);

        public Task<ControlSample?> SampleAsync(string group, CancellationToken cancellationToken) => Task.FromResult<ControlSample?>(null);
    }
}
