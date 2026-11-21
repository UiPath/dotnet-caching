using Microsoft.Extensions.Logging.Abstractions;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests;

public sealed class PinGateTests
{
    private static readonly TimeSpan Fresh = TimeSpan.FromSeconds(150);
    private static readonly TimeSpan MaxViewAge = TimeSpan.FromMinutes(2);

    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero));
    private readonly FakePinStore _store = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_gate_is_closed_until_asked_and_pinning_alone_never_records_a_switch()
    {
        var pins = Coordinator("a", _ => true);
        pins.EvaluatePins();
        _store.Nodes.AddRange([Node("a", "pin=g", "ap=app"), Node("b", "pin=g", "ap=app")]);

        await pins.RefreshAsync(Ct);

        _store.StateOf("g").Should().Be(SwitchRead.Unswitched, "nobody has asked to write a format that needs the switch");
        _store.Log.Should().NotContain("mark:g");
        pins.Hold().Should().NotBeNull();
    }

    [Fact]
    public async Task The_gate_opens_when_every_node_has_the_pin_and_the_switch_is_recorded_before_it_does()
    {
        var pins = Coordinator("a", _ => true);
        pins.EvaluatePins();
        _store.Nodes.AddRange([Node("a", "pin=g", "ap=app"), Node("b", "pin=g", "ap=app")]);
        pins.Hold().Should().Be("no_view", "asking is what makes the switch wanted");

        await pins.RefreshAsync(Ct);

        _store.Log.Should().ContainInOrder("nodes", "mark:g");
        _store.StateOf("g").Should().Be(SwitchRead.Switched);
        pins.Hold().Should().BeNull();
    }

    [Fact]
    public async Task A_switch_that_cannot_be_recorded_keeps_the_gate_closed()
    {
        var pins = Coordinator("a", _ => true);
        pins.EvaluatePins();
        _store.Nodes.AddRange([Node("a", "pin=g", "ap=app")]);
        _store.WritesDown = true;
        pins.Hold();

        (await pins.RefreshAsync(Ct)).Should().BeFalse();

        pins.Hold().Should().Be("state_not_written");
        _store.StateOf("g").Should().Be(SwitchRead.Unswitched);
    }

    [Fact]
    public async Task A_node_without_the_pin_that_joins_closes_the_gate_at_the_next_read_and_is_not_forgiven_when_it_leaves_before_it_ends()
    {
        var pins = Coordinator("a", _ => true);
        pins.EvaluatePins();
        _store.Nodes.AddRange([Node("a", "pin=g", "ap=app")]);
        pins.Hold();
        await pins.RefreshAsync(Ct);
        pins.Hold().Should().BeNull();

        _store.Nodes.Add(Node("late", "ap=app"));
        await pins.RefreshAsync(Ct);

        pins.Hold().Should().Be("node_without_pin");
        _store.Nodes.RemoveAll(entry => entry.Field.StartsWith("late", StringComparison.Ordinal));
        await pins.RefreshAsync(Ct);
        pins.Hold().Should().BeNull("with the node gone the list is complete again");
    }

    [Fact]
    public async Task A_node_that_is_leaving_or_past_its_liveness_does_not_hold_the_gate()
    {
        var pins = Coordinator("a", _ => true);
        pins.EvaluatePins();
        _store.Nodes.AddRange([Node("a", "pin=g", "ap=app"), Entry("left", ["ap=app"], TimeSpan.Zero, left: true), Entry("old", ["ap=app"], Fresh + TimeSpan.FromSeconds(1), left: false)]);
        pins.Hold();

        await pins.RefreshAsync(Ct);

        pins.Hold().Should().BeNull();
    }

    [Fact]
    public async Task A_library_advertisement_without_a_router_entry_of_its_node_holds_the_gate()
    {
        var pins = Coordinator("a", _ => true);
        pins.EvaluatePins();
        _store.Nodes.AddRange([Node("a", "pin=g", "ap=app"), Library("orphan")]);
        pins.Hold();

        await pins.RefreshAsync(Ct);

        pins.Hold().Should().Be(PinGateEvaluator.UnattributedNode);
        _store.StateOf("g").Should().Be(SwitchRead.Unswitched);
    }

    [Fact]
    public async Task A_node_list_without_this_nodes_own_entry_holds_the_gate()
    {
        var pins = Coordinator("a", _ => true);
        pins.EvaluatePins();
        _store.Nodes.AddRange([Node("b", "pin=g", "ap=app")]);
        pins.Hold();

        await pins.RefreshAsync(Ct);

        pins.Hold().Should().Be(PinGateEvaluator.OwnEntryMissing);
    }

    [Fact]
    public async Task A_group_whose_old_path_calls_have_not_drained_is_not_acknowledged_and_holds_the_gate()
    {
        var core = new RouterCore(_clock, _ => false, NullLogger.Instance);
        var pins = new PinCoordinator(core, _ => true, _store, "app", TimeSpan.FromSeconds(15), MaxViewAge, NullLogger.Instance) { OwnField = "a#router" };
        core.Pins = pins;
        var pin = pins.GroupFor("g");
        pin.TryBeginSource().Should().BeTrue();
        pins.EvaluatePins();
        _store.Nodes.Add(Node("a", "pin=g", "ap=app"));
        pins.Hold();

        await pins.RefreshAsync(Ct);

        pins.Hold().Should().Be("unacknowledged");
        pin.EndSource();
        pins.EvaluatePins();
        await pins.RefreshAsync(Ct);
        pins.Hold().Should().BeNull();
    }

    [Fact]
    public async Task An_unreadable_node_list_keeps_the_last_decision_until_it_is_too_old_and_then_closes_the_gate()
    {
        var pins = Coordinator("a", _ => true);
        pins.EvaluatePins();
        _store.Nodes.AddRange([Node("a", "pin=g", "ap=app")]);
        pins.Hold();
        await pins.RefreshAsync(Ct);
        pins.Hold().Should().BeNull();

        _store.Down = true;
        (await pins.RefreshAsync(Ct)).Should().BeFalse();
        _clock.Advance(MaxViewAge - TimeSpan.FromSeconds(1));
        pins.Hold().Should().BeNull("Redis is down, and the last view is still young");
        _clock.Advance(TimeSpan.FromSeconds(2));

        pins.Hold().Should().Be("view_stale");
        _store.Down = false;
        await pins.RefreshAsync(Ct);
        pins.Hold().Should().BeNull();
    }

    [Fact]
    public async Task Redis_down_before_the_first_view_leaves_the_gate_closed()
    {
        var pins = Coordinator("a", _ => true);
        pins.EvaluatePins();
        _store.Down = true;
        pins.Hold();

        await pins.RefreshAsync(Ct);

        pins.Hold().Should().NotBeNull();
    }

    [Fact]
    public async Task A_lost_switch_state_that_cannot_be_written_again_closes_the_gate()
    {
        var pins = Coordinator("a", _ => true);
        pins.EvaluatePins();
        _store.Nodes.AddRange([Node("a", "pin=g", "ap=app")]);
        pins.Hold();
        await pins.RefreshAsync(Ct);
        pins.Hold().Should().BeNull();

        _store.Lose("g");
        _store.WritesDown = true;
        await pins.RefreshAsync(Ct);

        pins.Hold().Should().Be("state_not_written");
        _store.WritesDown = false;
        await pins.RefreshAsync(Ct);
        pins.Hold().Should().BeNull();
        _store.StateOf("g").Should().Be(SwitchRead.Switched);
    }

    [Fact]
    public async Task Two_applications_on_one_node_hash_do_not_admit_each_others_pins_or_nodes()
    {
        var mine = Coordinator("a", _ => true, scope: "app1");
        mine.EvaluatePins();
        _store.Nodes.AddRange([Node("a", "pin=g", "ap=app1"), Node("other", "ap=app2"), Library("other"), Node("other2", "pin=g", "ap=app2")]);
        mine.Hold();

        await mine.RefreshAsync(Ct);

        mine.Hold().Should().BeNull("the other application's node has no pin, and it is not ours to wait for");
    }

    [Fact]
    public async Task Another_applications_pin_does_not_satisfy_this_one()
    {
        var mine = Coordinator("a", _ => true, scope: "app1");
        mine.EvaluatePins();
        _store.Nodes.AddRange([Node("a", "pin=g", "ap=app1"), Node("mine2", "ap=app1"), Node("foreign", "pin=g", "ap=app2")]);
        mine.Hold();

        await mine.RefreshAsync(Ct);

        mine.Hold().Should().Be("node_without_pin");
    }

    [Fact]
    public async Task Another_applications_switch_report_does_not_restore_this_applications_state()
    {
        var mine = Coordinator("a", _ => false, scope: "app1");
        mine.EvaluatePins();
        _store.Nodes.AddRange([Node("foreign", "pin=g", "sw=g", "ap=app2")]);

        await mine.RefreshAsync(Ct);

        _store.StateOf("g").Should().Be(SwitchRead.Unswitched);
        mine.GroupFor("g").Switched.Should().BeFalse();
    }

    [Fact]
    public void An_entry_that_names_no_application_cannot_be_attributed_and_holds_the_gate()
    {
        var nodes = new List<NodeEntry> { Node("a", "pin=g", "ap=app1"), Node("older") };

        var verdict = PinGateEvaluator.Evaluate(nodes, "app1", "a#router", ["g"], Fresh);

        verdict.Blocker.Should().BeNull();
        verdict.Satisfied.Should().BeEmpty("an entry of a build that names no application may be ours, and it has no pin");
    }

    [Fact]
    public void A_library_advertisement_of_another_application_with_its_own_router_entry_is_ignored()
    {
        var nodes = new List<NodeEntry> { Node("a", "pin=g", "ap=app1"), Library("a"), Node("x", "ap=app2"), Library("x") };

        var verdict = PinGateEvaluator.Evaluate(nodes, "app1", "a#router", ["g"], Fresh);

        verdict.Blocker.Should().BeNull();
        verdict.Satisfied.Should().Equal("g");
    }

    [Fact]
    public void A_departing_library_advertisement_does_not_need_a_router_entry()
    {
        var nodes = new List<NodeEntry> { Node("a", "pin=g", "ap=app1"), new("gone", "lib=3;inst=x;vr=json;left=1", TimeSpan.Zero) };

        PinGateEvaluator.Evaluate(nodes, "app1", "a#router", ["g"], Fresh).Blocker.Should().BeNull();
    }

    [Fact]
    public void The_scope_token_is_the_lowercase_application_name_and_survives_the_entry_encoding()
    {
        var scope = ScopeTokens.For("My+App;x=1");
        var tokens = new List<string>();
        new ScopeTokens(scope).AddTo(tokens);
        var written = new RouterAdvertisement("n", "b", tokens);

        RouterAdvertisement.TryParse(written.Encode(), out var read).Should().BeTrue();

        read!.Scope.Should().Be(scope);
        scope.Should().NotContain("+").And.NotContain(";").And.NotContain("=");
        ScopeTokens.For(" ").Should().BeNull();
    }

    private static NodeEntry Node(string node, params string[] tokens) => Entry(node, tokens, TimeSpan.Zero, left: false);

    private static NodeEntry Entry(string node, string[] tokens, TimeSpan age, bool left) =>
        new(node + RouterAdvertisement.FieldSuffix, new RouterAdvertisement(node, "b", tokens, left).Encode(), age);

    private static NodeEntry Library(string node) => new(node, "lib=3.2.0;inst=x;vr=json", TimeSpan.Zero);

    private PinCoordinator Coordinator(string node, Func<string, bool> pinWhen, string scope = "app")
    {
        var core = new RouterCore(_clock, _ => false, NullLogger.Instance);
        var pins = new PinCoordinator(core, pinWhen, _store, scope, TimeSpan.FromSeconds(15), MaxViewAge, NullLogger.Instance) { OwnField = node + RouterAdvertisement.FieldSuffix };
        core.Pins = pins;
        pins.GroupFor("g");
        return pins;
    }
}
