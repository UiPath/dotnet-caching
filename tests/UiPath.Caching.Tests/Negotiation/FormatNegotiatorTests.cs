using Microsoft.Extensions.Configuration;
using UiPath.Caching.Negotiation;
using UiPath.Caching.Tests.Fakes;
using UiPath.Caching.Tests.Telemetry;

namespace UiPath.Caching.Tests.Negotiation;

public class FormatNegotiatorTests
{
    private static readonly string[] Binary = ["10"];

    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly RecordingTelemetryProvider _telemetry = new();
    private readonly FakeValueSerializer _writer = new(0x10);
    private readonly FormatNegotiationOptions _options = new();

    [Fact]
    public void No_view_means_json()
    {
        var sut = Negotiator();

        sut.Allows(_writer).Should().BeFalse();
        sut.ObservationFailed();
        sut.Allows(_writer).Should().BeFalse();
    }

    [Fact]
    public void All_fresh_ads_list_format_gives_desired_once_the_hold_has_passed()
    {
        var sut = Negotiator();
        var view = View(Node("a", Binary), Node("b", Binary));

        sut.Observe(view);
        sut.Allows(_writer).Should().BeFalse("the node list has only just agreed");
        _clock.Advance(_options.UpgradeHold);
        sut.Observe(view);

        sut.Allows(_writer).Should().BeTrue();
    }

    [Fact]
    public void One_ad_without_format_gives_json_immediately()
    {
        var sut = Negotiator();
        Settle(sut, View(Node("a", Binary), Node("b", Binary)));
        sut.Allows(_writer).Should().BeTrue();

        sut.Observe(View(Node("a", Binary), Node("b", Binary), Node("old", [])));

        sut.Allows(_writer).Should().BeFalse();
        _telemetry.Metrics.Should().Contain(m => m.Name == Metrics.FormatFallback && m.Properties!["reason"] == FormatNegotiator.ReasonMissingFormat);
    }

    [Fact]
    public void One_ad_without_format_blocks_even_when_the_rest_list_it()
    {
        var sut = Negotiator();

        Settle(sut, View(Node("a", Binary), Node("b", Binary), Node("old", ["json"])));

        sut.Allows(_writer).Should().BeFalse();
    }

    [Fact]
    public void The_upgrade_hold_defaults_to_an_hour()
    {
        new FormatNegotiationOptions().UpgradeHold.Should().Be(TimeSpan.FromHours(1));
    }

    [Fact]
    public void A_configured_hold_is_bound_from_configuration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["FormatNegotiation:UpgradeHold"] = "00:20:00", ["FormatNegotiation:Mode"] = "Force" })
            .Build();
        var options = new RedisCacheOptions();

        configuration.Bind(options);

        options.FormatNegotiation.UpgradeHold.Should().Be(TimeSpan.FromMinutes(20));
        options.FormatNegotiation.Mode.Should().Be(FormatNegotiationMode.Force);
    }

    [Fact]
    public void No_upgrade_before_the_configured_hold_and_an_upgrade_after_it()
    {
        _options.UpgradeHold = TimeSpan.FromMinutes(20);
        var sut = Negotiator();
        var view = View(Node("a", Binary));

        sut.Observe(view);
        _clock.Advance(TimeSpan.FromMinutes(20) - TimeSpan.FromSeconds(1));
        sut.Observe(view);
        sut.Allows(_writer).Should().BeFalse();

        _clock.Advance(TimeSpan.FromSeconds(1));
        sut.Observe(view);
        sut.Allows(_writer).Should().BeTrue();
    }

    [Fact]
    public void A_zero_hold_switches_at_once_and_a_negative_one_is_rejected()
    {
        _options.UpgradeHold = TimeSpan.Zero;
        var sut = Negotiator();
        sut.Observe(View(Node("a", Binary)));
        sut.Allows(_writer).Should().BeTrue();

        _options.UpgradeHold = TimeSpan.FromSeconds(-1);
        FluentActions.Invoking(() => Negotiator()).Should().Throw<InvalidOperationException>().WithMessage("*UpgradeHold*negative*");
    }

    [Fact]
    public void Upgrade_waits_for_hold_since_the_last_node_joined()
    {
        var sut = Negotiator();
        sut.Observe(View(Node("a", Binary)));
        _clock.Advance(_options.UpgradeHold - TimeSpan.FromSeconds(1));

        // A node joins late in the hold: the clock starts again.
        sut.Observe(View(Node("a", Binary), Node("b", Binary)));
        _clock.Advance(TimeSpan.FromSeconds(30));
        sut.Observe(View(Node("a", Binary), Node("b", Binary)));
        sut.Allows(_writer).Should().BeFalse();

        _clock.Advance(_options.UpgradeHold);
        sut.Observe(View(Node("a", Binary), Node("b", Binary)));
        sut.Allows(_writer).Should().BeTrue();
    }

    [Fact]
    public void A_node_that_leaves_never_resets_the_hold()
    {
        var sut = Negotiator();
        sut.Observe(View(Node("a", Binary), Node("b", Binary)));
        _clock.Advance(_options.UpgradeHold - TimeSpan.FromSeconds(1));

        sut.Observe(View(Node("a", Binary)));
        _clock.Advance(TimeSpan.FromSeconds(2));
        sut.Observe(View(Node("a", Binary)));

        sut.Allows(_writer).Should().BeTrue();
    }

    [Fact]
    public void A_node_that_restarts_under_the_same_id_is_a_join()
    {
        var sut = Negotiator();
        sut.Observe(View(Node("a", Binary, instance: "first")));
        _clock.Advance(_options.UpgradeHold - TimeSpan.FromSeconds(1));

        sut.Observe(View(Node("a", Binary, instance: "second")));
        _clock.Advance(TimeSpan.FromSeconds(2));
        sut.Observe(View(Node("a", Binary, instance: "second")));

        sut.Allows(_writer).Should().BeFalse();
    }

    [Fact]
    public void A_blocker_resets_the_hold()
    {
        var sut = Negotiator();
        sut.Observe(View(Node("a", Binary)));
        _clock.Advance(_options.UpgradeHold - TimeSpan.FromSeconds(1));
        sut.Observe(View([Node("a", Binary)], ["legacy"]));
        _clock.Advance(TimeSpan.FromSeconds(10));

        sut.Observe(View(Node("a", Binary)));

        sut.Allows(_writer).Should().BeFalse();
    }

    [Fact]
    public void Redis_failure_keeps_decision_until_max_view_age_then_json()
    {
        var sut = Negotiator();
        Settle(sut, View(Node("a", Binary)));

        _clock.Advance(_options.MaxViewAge - TimeSpan.FromSeconds(1));
        sut.ObservationFailed();
        sut.Allows(_writer).Should().BeTrue();

        _clock.Advance(TimeSpan.FromSeconds(2));
        sut.ObservationFailed();
        sut.Allows(_writer).Should().BeFalse();
        _telemetry.Metrics.Should().Contain(m => m.Name == Metrics.FormatFallback && m.Properties!["reason"] == FormatNegotiator.ReasonViewStale);
    }

    [Fact]
    public void A_view_that_stops_refreshing_without_failing_decays_to_json_on_its_own()
    {
        var sut = Negotiator();
        Settle(sut, View(Node("a", Binary)));
        sut.Allows(_writer).Should().BeTrue();

        _clock.Advance(_options.MaxViewAge + TimeSpan.FromSeconds(1));

        sut.Allows(_writer).Should().BeFalse();
    }

    [Fact]
    public void After_an_outage_the_decision_is_kept_while_peers_advertise_again_and_then_judged()
    {
        var sut = Negotiator();
        Settle(sut, View(Node("a", Binary), Node("b", Binary)));
        _clock.Advance(TimeSpan.FromSeconds(60));
        sut.ObservationFailed();

        // Redis is back but nobody has advertised yet, and the consumers read nothing meanwhile.
        sut.Observe(View([], ["b"]));
        sut.Allows(_writer).Should().BeTrue("peers get two advertise intervals to say what they read");
        sut.Observe(View([Node("a", Binary)], ["b"]));
        sut.Allows(_writer).Should().BeTrue();

        _clock.Advance(TimeSpan.FromTicks(_options.AdvertiseInterval.Ticks * 2));
        sut.Observe(View([Node("a", Binary)], ["b"]));

        sut.Allows(_writer).Should().BeFalse("a node still unaccounted for once the grace is over");
    }

    [Fact]
    public void After_an_outage_a_fresh_ad_without_the_format_still_stops_the_writes_at_once()
    {
        var sut = Negotiator();
        Settle(sut, View(Node("a", Binary)));
        _clock.Advance(TimeSpan.FromSeconds(60));
        sut.ObservationFailed();

        sut.Observe(View(Node("a", Binary), Node("old", [])));

        sut.Allows(_writer).Should().BeFalse();
    }

    [Fact]
    public void After_an_outage_a_view_with_both_an_unaccounted_group_and_an_ad_lacking_the_format_goes_json_at_once()
    {
        var sut = Negotiator();
        Settle(sut, View(Node("a", Binary), Node("b", Binary)));
        _clock.Advance(TimeSpan.FromSeconds(60));
        sut.ObservationFailed();

        sut.Observe(View([Node("a", Binary), Node("old", [])], ["b"]));

        sut.Allows(_writer).Should().BeFalse("the grace excuses silence, not an advertisement that says no");
        _telemetry.Metrics.Should().Contain(m => m.Name == Metrics.FormatFallback && m.Properties!["reason"] == FormatNegotiator.ReasonMissingFormat);
    }

    [Fact]
    public void A_peer_that_does_not_look_for_a_header_keeps_the_writer_on_json_even_for_a_policy_the_writer_knows()
    {
        var sut = Negotiator(policies: ["orders"], others: ["default"]);
        var peer = new Dictionary<string, IReadOnlyList<string>> { ["default"] = ["json", "nosniff"] };

        Settle(sut, View(Node("a", Binary), Node("raw-peer", Binary, peer)));

        sut.Allows(_writer).Should().BeFalse();
    }

    [Fact]
    public void A_pending_upgrade_does_not_survive_a_failed_read()
    {
        var sut = Negotiator();
        sut.Observe(View(Node("a", Binary)));
        _clock.Advance(_options.UpgradeHold - TimeSpan.FromSeconds(1));
        sut.ObservationFailed();
        _clock.Advance(TimeSpan.FromSeconds(5));

        sut.Observe(View(Node("a", Binary)));

        sut.Allows(_writer).Should().BeFalse();
    }

    [Fact]
    public void Strict_read_list_blocks_a_format_it_excludes()
    {
        var sut = Negotiator(policies: ["orders"]);
        var strict = new Dictionary<string, IReadOnlyList<string>> { ["orders"] = ["json"] };

        Settle(sut, View(Node("a", Binary), Node("b", Binary, strict)));

        sut.Allows(_writer).Should().BeFalse();
    }

    [Fact]
    public void Strict_read_list_of_a_policy_that_writes_something_else_does_not_block()
    {
        var sut = Negotiator(policies: ["orders"], others: ["users"]);
        var strict = new Dictionary<string, IReadOnlyList<string>> { ["users"] = ["json"] };

        Settle(sut, View(Node("a", Binary), Node("b", Binary, strict)));

        sut.Allows(_writer).Should().BeTrue();
    }

    [Fact]
    public void Strict_read_list_of_a_policy_this_node_does_not_know_blocks()
    {
        var sut = Negotiator(policies: ["orders"]);
        var strict = new Dictionary<string, IReadOnlyList<string>> { ["elsewhere"] = ["json"] };

        Settle(sut, View(Node("a", Binary), Node("b", Binary, strict)));

        sut.Allows(_writer).Should().BeFalse();
    }

    [Fact]
    public void A_live_group_without_an_ad_blocks_unless_the_operator_attests()
    {
        var blocked = Negotiator();
        Settle(blocked, View([Node("a", Binary)], ["legacy"]));
        blocked.Allows(_writer).Should().BeFalse();

        _options.AssumeAllNodesCanRead = true;
        var attested = Negotiator();
        Settle(attested, View([Node("a", Binary)], ["legacy"]));
        attested.Allows(_writer).Should().BeTrue();
    }

    [Fact]
    public void The_attestation_never_covers_an_ad_that_lacks_the_format()
    {
        _options.AssumeAllNodesCanRead = true;
        var sut = Negotiator();

        Settle(sut, View([Node("a", Binary), Node("old", ["json"])], ["legacy"]));
        sut.Allows(_writer).Should().BeFalse();

        var blind = Negotiator();
        Settle(blind, View([Node("a", Binary), Node("old", ["json"])], [], complete: false));
        blind.Allows(_writer).Should().BeFalse();
    }

    [Fact]
    public void A_node_list_that_cannot_see_every_node_means_json_unless_the_operator_attests_and_the_hold_still_applies()
    {
        var blind = Negotiator();
        Settle(blind, View([Node("a", Binary)], [], complete: false));
        blind.Allows(_writer).Should().BeFalse();

        _options.AssumeAllNodesCanRead = true;
        var attested = Negotiator();
        var nobody = View([], [], complete: false);
        attested.Observe(nobody);
        attested.Allows(_writer).Should().BeFalse("the hold starts at the first decision");
        _clock.Advance(_options.UpgradeHold);
        attested.Observe(nobody);

        attested.Allows(_writer).Should().BeTrue();
    }

    [Fact]
    public void An_ad_naming_no_node_is_not_a_view()
    {
        var sut = Negotiator();

        Settle(sut, View());

        sut.Allows(_writer).Should().BeFalse();
    }

    [Fact]
    public void Every_registered_serializer_is_decided_whatever_policy_asks_for_it()
    {
        var other = new FakeValueSerializer(0x11);
        var sut = new FormatNegotiator(
            [
                new DesiredWrite(_writer, Binary, ["orders"], []),
                new DesiredWrite(other, ["11"], [], ["orders"]),
            ],
            _options,
            _clock,
            _telemetry);
        var view = View(Node("a", ["10", "11"]), Node("b", ["10"]));

        Settle(sut, view);

        sut.Allows(_writer).Should().BeTrue();
        sut.Allows(other).Should().BeFalse("one node does not read 11");
        sut.Allows(new FakeValueSerializer(0x12)).Should().BeFalse("a serializer nobody registered is never allowed");
    }

    [Fact]
    public void A_decision_is_read_without_allocating()
    {
        var sut = Negotiator();
        Settle(sut, View(Node("a", Binary)));
        sut.Allows(_writer).Should().BeTrue();

        // Steady state: after a warm-up, so the JIT and tiering do not count, the best of a few passes allocates nothing.
        var best = long.MaxValue;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 10_000; i++)
            {
                _ = sut.Allows(_writer);
            }

            best = Math.Min(best, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        best.Should().Be(0);
    }

    private static (string Id, NodeAdvertisement Ad) Node(string id, string[] formats, IReadOnlyDictionary<string, IReadOnlyList<string>>? strict = null, string? instance = null) =>
        (id, new("3.2.0", instance ?? id, ["json", .. formats], strict ?? new Dictionary<string, IReadOnlyList<string>>()));

    private static NodeListView View(params (string Id, NodeAdvertisement Ad)[] nodes) => View(nodes, [], true);

    private static NodeListView View((string Id, NodeAdvertisement Ad)[] nodes, string[] blockers, bool complete = true) =>
        new([.. nodes.Select(n => new StoredAdvertisement(n.Id, n.Ad, TimeSpan.Zero))], blockers, complete);

    private FormatNegotiator Negotiator(string[]? policies = null, string[]? others = null) =>
        new([new DesiredWrite(_writer, Binary, policies ?? ["default"], others ?? [])], _options, _clock, _telemetry);

    private void Settle(FormatNegotiator sut, NodeListView view)
    {
        sut.Observe(view);
        _clock.Advance(_options.UpgradeHold);
        sut.Observe(view);
    }
}
