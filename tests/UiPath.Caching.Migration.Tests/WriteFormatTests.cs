using Microsoft.Extensions.Logging.Abstractions;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests;

public sealed class WriteFormatTests
{
    private static readonly TimeSpan MaxViewAge = TimeSpan.FromMinutes(2);

    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero));
    private readonly FakePinStore _store = new();
    private readonly Dictionary<string, string?> _format = [];
    private readonly Dictionary<string, bool> _pinned = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(true, false, false, false, null, "A wants it and nothing else does: a group that does not want it and is not pinned holds nothing")]
    [InlineData(true, false, true, false, null, "an unwanted group that is pinned but not acknowledged holds nothing either")]
    [InlineData(true, false, true, true, null, "an unwanted group that is pinned and acknowledged is not needed and not recorded")]
    [InlineData(true, true, false, false, "unpinned_group", "a wanted group that is not pinned holds every wanted group")]
    [InlineData(true, true, true, false, "unacknowledged", "a wanted group that is pinned but not acknowledged holds every wanted group")]
    [InlineData(true, true, true, true, null, "every wanted group is pinned and acknowledged")]
    [InlineData(false, true, true, true, null, "only B wanted: A needs nothing")]
    [InlineData(false, true, false, false, "unpinned_group", "only B wanted and it is not pinned")]
    [InlineData(false, false, true, true, "format_not_wanted", "no group wants it")]
    public async Task The_gate_is_over_the_wanted_groups_only(bool aWanted, bool bWanted, bool bPinned, bool bAcked, string? hold, string because)
    {
        var pins = Coordinator("a", "A", "B");
        var a = pins.GroupFor("A");
        var b = pins.GroupFor("B");
        _pinned["A"] = true;
        _pinned["B"] = bPinned;
        if (bPinned && !bAcked)
        {
            // A call that started on the old path before the pin applied is still running, so the group is applied and not yet acknowledged.
            b.TryBeginSource().Should().BeTrue();
        }

        pins.EvaluatePins();
        Advertise("a", pins);
        pins.Want(a, aWanted);
        pins.Want(b, bWanted);

        await pins.RefreshAsync(Ct);

        pins.Hold().Should().Be(hold, because);
        _store.StateOf("A").Should().Be(hold is null && aWanted ? SwitchRead.Switched : SwitchRead.Unswitched, "the switch is recorded only when the gate opens");
        _store.StateOf("B").Should().Be(hold is null && bWanted ? SwitchRead.Switched : SwitchRead.Unswitched, "an unwanted group is never recorded as switched");
    }

    [Fact]
    public async Task Nothing_wanted_keeps_the_gate_closed_and_records_nothing()
    {
        var pins = Coordinator("a", "A");
        pins.GroupFor("A");
        _pinned["A"] = true;
        pins.EvaluatePins();
        Advertise("a", pins);

        await pins.RefreshAsync(Ct);

        pins.Hold().Should().Be("format_not_wanted");
        _store.Log.Should().NotContain("commit:A");
    }

    [Fact]
    public async Task One_closed_wanted_group_holds_the_others_and_the_group_that_is_ready_writes_the_envelope()
    {
        var pins = Coordinator("a", "A", "B");
        var a = pins.GroupFor("A");
        var b = pins.GroupFor("B");
        _pinned["A"] = true;
        _pinned["B"] = false;
        _format["A"] = "messagepack";
        _format["B"] = "messagepack";
        pins.EvaluatePins();
        Advertise("a", pins);
        var selector = new WriteFormatSelector(g => _format[g], pins, NullLogger.Instance);

        selector.PolicyFor(a);
        selector.PolicyFor(b);
        await pins.RefreshAsync(Ct);

        pins.Hold().Should().Be("unpinned_group");
        selector.PolicyFor(a).Should().BeSameAs(WriteFormats.Envelope, "A is ready by itself, but the gate is one for every wanted group");
        _store.StateOf("A").Should().Be(SwitchRead.Unswitched);

        _pinned["B"] = true;
        pins.EvaluatePins();
        Advertise("a", pins);
        await pins.RefreshAsync(Ct);

        pins.Hold().Should().BeNull();
        selector.PolicyFor(a).Should().BeSameAs(WriteFormats.MessagePackPolicy);
        selector.PolicyFor(b).Should().BeSameAs(WriteFormats.MessagePackPolicy);
    }

    [Fact]
    public async Task A_wanted_group_that_goes_back_to_json_stops_holding_the_others_without_a_write()
    {
        var pins = Coordinator("a", "A", "B");
        var a = pins.GroupFor("A");
        var b = pins.GroupFor("B");
        _pinned["A"] = true;
        _pinned["B"] = false;
        _format["A"] = "messagepack";
        _format["B"] = "messagepack";
        pins.EvaluatePins();
        Advertise("a", pins);
        pins.Want(a, true);
        pins.Want(b, true);
        await pins.RefreshAsync(Ct);
        pins.Hold().Should().Be("unpinned_group");

        _format["B"] = "json";
        pins.EvaluatePins();
        await pins.RefreshAsync(Ct);

        b.Wanted.Should().BeFalse();
        pins.Hold().Should().BeNull();
    }

    [Fact]
    public async Task A_group_that_asks_after_the_gate_was_computed_is_not_covered_until_the_next_pass()
    {
        var pins = Coordinator("a", "A", "B");
        var a = pins.GroupFor("A");
        var b = pins.GroupFor("B");
        _pinned["A"] = true;
        _pinned["B"] = true;
        pins.EvaluatePins();
        Advertise("a", pins);
        pins.Want(a, true);
        await pins.RefreshAsync(Ct);
        pins.Hold().Should().BeNull();

        pins.Want(b, true);

        pins.Hold().Should().Be("group_not_evaluated", "B was not part of the view the gate was opened from, and its switch is not recorded");
        await pins.RefreshAsync(Ct);
        pins.Hold().Should().BeNull();
        _store.StateOf("B").Should().Be(SwitchRead.Switched);
    }

    [Fact]
    public async Task A_node_that_joins_without_the_pin_closes_the_gate_and_the_group_writes_the_envelope_again()
    {
        var pins = Coordinator("a", "A");
        var a = pins.GroupFor("A");
        _pinned["A"] = true;
        _format["A"] = "messagepack";
        pins.EvaluatePins();
        Advertise("a", pins);
        var selector = new WriteFormatSelector(g => _format[g], pins, NullLogger.Instance);
        selector.PolicyFor(a);
        await pins.RefreshAsync(Ct);
        selector.PolicyFor(a).Should().BeSameAs(WriteFormats.MessagePackPolicy);

        _store.Nodes.Add(Node("late", "ap=app"));
        await pins.RefreshAsync(Ct);

        pins.Hold().Should().Be("node_without_pin");
        selector.PolicyFor(a).Should().BeSameAs(WriteFormats.Envelope);
        a.Switched.Should().BeTrue("the switch is not undone, only the write format is held");
    }

    [Theory]
    [InlineData("messagepack", true)]
    [InlineData("MessagePack", true)]
    [InlineData("json", false)]
    [InlineData("JSON", false)]
    [InlineData(null, false)]
    public async Task A_group_writes_messagepack_only_for_that_answer_once_it_is_switched_and_the_gate_is_open(string? answer, bool messagePack)
    {
        var pins = ReadyCoordinator(out var a);
        var selector = new WriteFormatSelector(_ => answer, pins, NullLogger.Instance);
        selector.PolicyFor(a);
        await pins.RefreshAsync(Ct);

        var policy = selector.PolicyFor(a);

        policy.Should().BeSameAs(messagePack ? WriteFormats.MessagePackPolicy : WriteFormats.Envelope);
        a.Wanted.Should().Be(messagePack);
    }

    [Fact]
    public void A_group_that_wants_messagepack_writes_the_envelope_until_it_is_switched_and_asking_makes_it_wanted()
    {
        var pins = Coordinator("a", "A");
        var a = pins.GroupFor("A");
        _pinned["A"] = true;
        pins.EvaluatePins();
        var selector = new WriteFormatSelector(_ => "messagepack", pins, NullLogger.Instance);

        selector.PolicyFor(a).Should().BeSameAs(WriteFormats.Envelope);

        a.Wanted.Should().BeTrue();
    }

    [Fact]
    public async Task A_setting_that_throws_writes_the_envelope_even_when_the_gate_is_open_and_never_throws_to_the_caller()
    {
        var pins = ReadyCoordinator(out var a);
        var throwing = false;
        var selector = new WriteFormatSelector(_ => throwing ? throw new InvalidOperationException("no such tenant") : "messagepack", pins, NullLogger.Instance);
        selector.PolicyFor(a);
        await pins.RefreshAsync(Ct);
        selector.PolicyFor(a).Should().BeSameAs(WriteFormats.MessagePackPolicy);

        throwing = true;
        var policy = () => selector.PolicyFor(a);

        policy.Should().NotThrow().Subject.Should().BeSameAs(WriteFormats.Envelope);
        throwing = false;
        selector.PolicyFor(a).Should().BeSameAs(WriteFormats.MessagePackPolicy, "the next answer that can be read is used");
    }

    [Fact]
    public async Task An_answer_that_names_no_known_format_writes_the_envelope_and_does_not_count_as_wanted()
    {
        var pins = ReadyCoordinator(out var a);
        var selector = new WriteFormatSelector(_ => "xml", pins, NullLogger.Instance);

        selector.PolicyFor(a).Should().BeSameAs(WriteFormats.Envelope);

        a.Wanted.Should().BeFalse();
        await pins.RefreshAsync(Ct);
        pins.Hold().Should().Be("format_not_wanted");
    }

    [Fact]
    public async Task Without_a_write_format_setting_the_gate_is_over_every_group_as_before()
    {
        var core = new RouterCore(_clock, _ => false, NullLogger.Instance);
        var pins = new PinCoordinator(core, g => g == "A", _store, "app", TimeSpan.FromSeconds(15), MaxViewAge, NullLogger.Instance) { OwnField = "n1" + RouterAdvertisement.FieldSuffix };
        core.Pins = pins;
        pins.GroupFor("A");
        pins.GroupFor("B");
        pins.EvaluatePins();
        Advertise("a", pins);
        pins.Hold().Should().Be("no_view");

        await pins.RefreshAsync(Ct);

        pins.Hold().Should().Be("unpinned_group", "the unwanted group B holds the gate when no setting says which groups want it");
    }

    private static NodeEntry Node(string node, params string[] tokens) =>
        new(node + RouterAdvertisement.FieldSuffix, new RouterAdvertisement(node, "b", tokens, false).Encode(), TimeSpan.Zero);

    private PinCoordinator ReadyCoordinator(out GroupPin a)
    {
        var pins = Coordinator("a", "A");
        a = pins.GroupFor("A");
        _pinned["A"] = true;
        pins.EvaluatePins();
        Advertise("a", pins);
        return pins;
    }

    private PinCoordinator Coordinator(string node, params string[] groups)
    {
        var core = new RouterCore(_clock, _ => false, NullLogger.Instance);
        var pins = new PinCoordinator(core, group => _pinned.GetValueOrDefault(group), _store, "app", TimeSpan.FromSeconds(15), MaxViewAge, NullLogger.Instance)
        {
            OwnField = node + RouterAdvertisement.FieldSuffix,
            WriteFormat = group => _format.GetValueOrDefault(group),
        };
        core.Pins = pins;
        return pins;
    }

    // The only node: its own entry carries the pin of every group it has acknowledged.
    private void Advertise(string node, PinCoordinator pins)
    {
        var tokens = new List<string> { "ap=app" };
        pins.AddTo(tokens);
        _store.Nodes.RemoveAll(entry => entry.Field.StartsWith(node, StringComparison.Ordinal));
        _store.Nodes.Add(Node(node, [.. tokens]));
    }
}
