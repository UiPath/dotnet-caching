using System.Net;
using UiPath.Caching.Negotiation;
using UiPath.Caching.Tests.Fakes;
using UiPath.Caching.Tests.Telemetry;

namespace UiPath.Caching.Tests.Negotiation;

public class PubSubNodeListTests
{
    private const string Channel = "app:ps:orders";

    private static readonly string[] Both = ["json", "02"];
    private static readonly FakeValueSerializer Writer = new(0x10);

    private readonly FakeSubscribers _source = new();
    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly RecordingTelemetryProvider _telemetry = new();
    private readonly FormatNegotiationOptions _options = new() { UpgradeHold = TimeSpan.Zero };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Sum_over_all_endpoints()
    {
        _source.Servers("a", "b", "c");
        _source.Count("a", Channel, 1);
        _source.Count("b", Channel, 1);
        _source.Count("c", Channel, 0);
        var nodeList = new PubSubNodeList(_source);

        // Two subscribers, each on a different server, and one advertised: one is surplus. Asking one server would see one and miss it.
        await nodeList.ReadAsync([Channel], Accounted(1), Ct);
        var reading = await nodeList.ReadAsync([Channel], Accounted(1), Ct);

        reading.Known.Should().BeTrue();
        reading.Surplus.Should().Be(1);
        reading.Channels.Should().Equal(Channel);
        reading.Unaccounted.Should().BeTrue();
        _source.Asked.Should().BeEquivalentTo(["a", "b", "c", "a", "b", "c"], "every server, every view");
    }

    [Fact]
    public async Task A_subscriber_that_is_accounted_for_is_no_surplus_and_no_deficit()
    {
        _source.Servers("a", "b");
        _source.Count("a", Channel, 1);
        _source.Count("b", Channel, 1);
        var nodeList = new PubSubNodeList(_source);

        var reading = await nodeList.ReadAsync([Channel], Accounted(2), Ct);

        reading.Surplus.Should().Be(0);
        reading.Short.Should().BeFalse();
        reading.ShortChannels.Should().BeEmpty();
    }

    [Fact]
    public async Task Fewer_subscribers_than_are_claimed_is_a_deficit_only_in_two_views_running()
    {
        _source.Servers("a", "b");
        _source.Count("a", Channel, 1);
        _source.Count("b", Channel, 0);
        var nodeList = new PubSubNodeList(_source);

        var first = await nodeList.ReadAsync([Channel], Accounted(2), Ct);
        var second = await nodeList.ReadAsync([Channel], Accounted(2), Ct);

        first.Surplus.Should().Be(0, "a deficit is no surplus");
        first.Short.Should().BeFalse("one view is no proof: a node resubscribing is briefly missing");
        first.Settled.Should().BeFalse();
        second.Short.Should().BeTrue();
        second.Settled.Should().BeFalse();
        second.ShortChannels.Should().Equal(Channel);
        second.Unaccounted.Should().BeFalse();
    }

    [Fact]
    public async Task A_deficit_that_is_gone_for_one_view_has_not_cleared()
    {
        _source.Servers("a");
        _source.Count("a", Channel, 1);
        var nodeList = new PubSubNodeList(_source);
        await nodeList.ReadAsync([Channel], Accounted(2), Ct);
        await nodeList.ReadAsync([Channel], Accounted(2), Ct);

        var firstClean = await nodeList.ReadAsync([Channel], Accounted(1), Ct);
        var secondClean = await nodeList.ReadAsync([Channel], Accounted(1), Ct);

        firstClean.Short.Should().BeFalse();
        firstClean.Settled.Should().BeFalse("the view before it was short");
        secondClean.Settled.Should().BeTrue();
    }

    [Fact]
    public async Task Deficit_blocks_and_recovery_does_not_restart_the_hold()
    {
        _options.UpgradeHold = TimeSpan.FromHours(1);
        _source.Servers("a");
        _source.Count("a", Channel, 2);
        var nodeList = new PubSubNodeList(_source);
        var sut = Negotiator();
        Observe(sut, await nodeList.ReadAsync([Channel], Accounted(2), Ct));
        Observe(sut, await nodeList.ReadAsync([Channel], Accounted(2), Ct));
        _clock.Advance(TimeSpan.FromHours(1));
        Observe(sut, await nodeList.ReadAsync([Channel], Accounted(2), Ct));
        sut.Writes.Should().BeTrue();
        sut.Allows(Writer).Should().BeTrue();

        // A node claimed a subscription and is gone: its claim outlives it.
        _source.Count("a", Channel, 1);
        Observe(sut, await nodeList.ReadAsync([Channel], Accounted(2), Ct));
        sut.Writes.Should().BeTrue("one view is absorbed");
        Observe(sut, await nodeList.ReadAsync([Channel], Accounted(2), Ct));
        sut.Writes.Should().BeFalse("the count does not add up, so it is unknown");
        sut.Allows(Writer).Should().BeFalse("values as well");
        _telemetry.Metrics.Should().Contain(m => m.Name == Metrics.FormatFallback && m.Properties!["reason"] == FormatNegotiator.ReasonSubscriberDeficit && m.Properties["kind"] == "event");

        // Its claim expires, and the counts match again, for two views: what was written before resumes with no new hold.
        _clock.Advance(TimeSpan.FromMinutes(3));
        Observe(sut, await nodeList.ReadAsync([Channel], Accounted(1), Ct));
        sut.Writes.Should().BeFalse("the first clean view is not enough");
        Observe(sut, await nodeList.ReadAsync([Channel], Accounted(1), Ct));

        sut.Writes.Should().BeTrue("a departure is not a join, so the hour it had already waited stands");
        sut.Allows(Writer).Should().BeTrue();
    }

    [Fact]
    public async Task A_node_that_joins_while_the_count_is_short_restarts_the_hold()
    {
        _options.UpgradeHold = TimeSpan.FromHours(1);
        _source.Servers("a");
        _source.Count("a", Channel, 2);
        var nodeList = new PubSubNodeList(_source);
        var sut = Negotiator();
        Observe(sut, await nodeList.ReadAsync([Channel], Accounted(2), Ct));
        Observe(sut, await nodeList.ReadAsync([Channel], Accounted(2), Ct));
        _clock.Advance(TimeSpan.FromHours(1));
        Observe(sut, await nodeList.ReadAsync([Channel], Accounted(2), Ct));
        _source.Count("a", Channel, 1);
        Observe(sut, await nodeList.ReadAsync([Channel], Accounted(2), Ct));
        Observe(sut, await nodeList.ReadAsync([Channel], Accounted(2), Ct));
        sut.Writes.Should().BeFalse();

        // The count adds up again, with a node that was not there before.
        sut.Observe(new NodeListView([Stored("a", Both), Stored("b", Both)], [], Complete: true, await nodeList.ReadAsync([Channel], Accounted(1), Ct)));
        sut.Observe(new NodeListView([Stored("a", Both), Stored("b", Both)], [], Complete: true, await nodeList.ReadAsync([Channel], Accounted(1), Ct)));
        sut.Writes.Should().BeFalse("a join waits for the hold");

        _clock.Advance(TimeSpan.FromHours(1));
        Observe(sut, await nodeList.ReadAsync([Channel], Accounted(1), Ct));
        sut.Writes.Should().BeTrue();
    }

    [Fact]
    public async Task One_command_per_server_carries_every_channel()
    {
        _source.Servers("a", "b", "c");
        var nodeList = new PubSubNodeList(_source);

        await nodeList.ReadAsync(["one", "two", "three"], new Dictionary<string, long>(), Ct);
        await nodeList.ReadAsync(["one", "two", "three"], new Dictionary<string, long>(), Ct);

        _source.Commands.Should().HaveCount(6, "three servers, two views, one command each");
        _source.Commands.Should().OnlyContain(command => command.SequenceEqual(new[] { "one", "two", "three" }));
    }

    [Fact]
    public async Task Surplus_on_one_channel_and_then_on_another_is_not_confirmed()
    {
        _source.Servers("a");
        var nodeList = new PubSubNodeList(_source);
        _source.Count("a", "one", 1);
        await nodeList.ReadAsync(["one", "two"], new Dictionary<string, long>(), Ct);
        _source.Count("a", "one", 0);
        _source.Count("a", "two", 1);

        var moved = await nodeList.ReadAsync(["one", "two"], new Dictionary<string, long>(), Ct);
        var stayed = await nodeList.ReadAsync(["one", "two"], new Dictionary<string, long>(), Ct);

        moved.Unaccounted.Should().BeFalse("the surplus is on another channel than the one before it");
        stayed.Unaccounted.Should().BeTrue("the same channel in two views running");
        stayed.Channels.Should().Equal("two");
    }

    [Fact]
    public async Task A_deficit_on_one_channel_and_then_on_another_is_not_confirmed()
    {
        _source.Servers("a");
        var nodeList = new PubSubNodeList(_source);
        var claims = new Dictionary<string, long> { ["one"] = 1, ["two"] = 1 };
        _source.Count("a", "one", 0);
        _source.Count("a", "two", 1);
        await nodeList.ReadAsync(["one", "two"], claims, Ct);
        _source.Count("a", "one", 1);
        _source.Count("a", "two", 0);

        (await nodeList.ReadAsync(["one", "two"], claims, Ct)).Short.Should().BeFalse();
        (await nodeList.ReadAsync(["one", "two"], claims, Ct)).Short.Should().BeTrue();
    }

    [Fact]
    public async Task A_leaving_node_may_or_may_not_still_hold_its_subscription()
    {
        _source.Servers("a");
        var nodeList = new PubSubNodeList(_source);
        var claims = new Dictionary<string, long> { [Channel] = 1 };
        var leaving = new Dictionary<string, long> { [Channel] = 1 };

        // Held, and closed: both are what a stop looks like, over the two or three views its connection takes to close.
        _source.Count("a", Channel, 2);
        await nodeList.ReadAsync([Channel], claims, leaving, Ct);
        var held = await nodeList.ReadAsync([Channel], claims, leaving, Ct);
        _source.Count("a", Channel, 1);
        await nodeList.ReadAsync([Channel], claims, leaving, Ct);
        var closed = await nodeList.ReadAsync([Channel], claims, leaving, Ct);
        _source.Count("a", Channel, 3);
        await nodeList.ReadAsync([Channel], claims, leaving, Ct);
        var surplus = await nodeList.ReadAsync([Channel], claims, leaving, Ct);

        held.Surplus.Should().Be(0);
        held.Unaccounted.Should().BeFalse();
        closed.Short.Should().BeFalse("a leaving node's claim is not one that has to be held");
        surplus.Surplus.Should().Be(1, "one more than the nodes that read and the one that is leaving can hold");
        surplus.Unaccounted.Should().BeTrue();
    }

    [Fact]
    public async Task A_one_view_blip_late_in_the_hold_does_not_restart_it()
    {
        _options.UpgradeHold = TimeSpan.FromHours(1);
        _source.Servers("a");
        _source.Count("a", Channel, 1);
        var nodeList = new PubSubNodeList(_source);
        var sut = Negotiator();
        Observe(sut, await nodeList.ReadAsync([Channel], Accounted(1), Ct));
        Observe(sut, await nodeList.ReadAsync([Channel], Accounted(1), Ct));
        _clock.Advance(TimeSpan.FromMinutes(55));

        // A replica reconnects for one view: a surplus that is gone by the next.
        _source.Count("a", Channel, 2);
        Observe(sut, await nodeList.ReadAsync([Channel], Accounted(1), Ct));
        _source.Count("a", Channel, 1);
        Observe(sut, await nodeList.ReadAsync([Channel], Accounted(1), Ct));
        Observe(sut, await nodeList.ReadAsync([Channel], Accounted(1), Ct));
        sut.Writes.Should().BeFalse("five minutes of the hour are left");
        _clock.Advance(TimeSpan.FromMinutes(5));
        Observe(sut, await nodeList.ReadAsync([Channel], Accounted(1), Ct));

        sut.Writes.Should().BeTrue("a blip pauses the hold, it does not start it again");
    }

    [Fact]
    public async Task A_confirmed_surplus_does_restart_the_hold()
    {
        _options.UpgradeHold = TimeSpan.FromHours(1);
        _source.Servers("a");
        _source.Count("a", Channel, 1);
        var nodeList = new PubSubNodeList(_source);
        var sut = Negotiator();
        Observe(sut, await nodeList.ReadAsync([Channel], Accounted(1), Ct));
        Observe(sut, await nodeList.ReadAsync([Channel], Accounted(1), Ct));
        _clock.Advance(TimeSpan.FromMinutes(55));
        _source.Count("a", Channel, 2);
        Observe(sut, await nodeList.ReadAsync([Channel], Accounted(1), Ct));
        Observe(sut, await nodeList.ReadAsync([Channel], Accounted(1), Ct));
        _source.Count("a", Channel, 1);
        Observe(sut, await nodeList.ReadAsync([Channel], Accounted(1), Ct));
        Observe(sut, await nodeList.ReadAsync([Channel], Accounted(1), Ct));

        _clock.Advance(TimeSpan.FromMinutes(5));
        Observe(sut, await nodeList.ReadAsync([Channel], Accounted(1), Ct));

        sut.Writes.Should().BeFalse("a subscriber nobody advertised was there for two views");
    }

    [Fact]
    public async Task A_deficit_that_clears_while_the_claim_is_still_fresh_is_a_join_and_restarts_the_hold()
    {
        _options.UpgradeHold = TimeSpan.FromHours(1);
        _source.Servers("a");
        _source.Count("a", Channel, 2);
        var nodeList = new PubSubNodeList(_source);
        var sut = Negotiator();
        Observe(sut, await nodeList.ReadAsync([Channel], Accounted(2), Ct));
        Observe(sut, await nodeList.ReadAsync([Channel], Accounted(2), Ct));
        _clock.Advance(TimeSpan.FromHours(1));
        Observe(sut, await nodeList.ReadAsync([Channel], Accounted(2), Ct));
        _source.Count("a", Channel, 1);
        Observe(sut, await nodeList.ReadAsync([Channel], Accounted(2), Ct));
        Observe(sut, await nodeList.ReadAsync([Channel], Accounted(2), Ct));
        sut.Writes.Should().BeFalse();

        // Two claims, two subscribers again, and the claim that was behind the deficit has not expired: something subscribed.
        _source.Count("a", Channel, 2);
        var replaced = await nodeList.ReadAsync([Channel], Accounted(2), Ct);
        Observe(sut, replaced);
        Observe(sut, await nodeList.ReadAsync([Channel], Accounted(2), Ct));
        replaced.Replaced.Should().BeTrue();
        sut.Writes.Should().BeFalse("it waits for the hold like a join");

        _clock.Advance(TimeSpan.FromHours(1));
        Observe(sut, await nodeList.ReadAsync([Channel], Accounted(2), Ct));
        sut.Writes.Should().BeTrue();
    }

    [Fact]
    public async Task A_deficit_that_clears_because_the_claim_expired_is_not_a_join()
    {
        _source.Servers("a");
        _source.Count("a", Channel, 1);
        var nodeList = new PubSubNodeList(_source);
        await nodeList.ReadAsync([Channel], Accounted(2), Ct);
        await nodeList.ReadAsync([Channel], Accounted(2), Ct);

        var expired = await nodeList.ReadAsync([Channel], Accounted(1), Ct);

        expired.Replaced.Should().BeFalse("the claim went, and the subscriber count did not rise");
    }

    [Fact]
    public async Task A_confirmed_deficit_survives_a_failed_view_and_the_replacement_still_waits_for_the_hold()
    {
        _options.UpgradeHold = TimeSpan.FromHours(1);
        _source.Servers("a");
        _source.Count("a", Channel, 2);
        var nodeList = new PubSubNodeList(_source);
        var sut = Negotiator();
        Observe(sut, await nodeList.ReadAsync([Channel], Accounted(2), Ct));
        Observe(sut, await nodeList.ReadAsync([Channel], Accounted(2), Ct));
        _clock.Advance(TimeSpan.FromHours(1));
        Observe(sut, await nodeList.ReadAsync([Channel], Accounted(2), Ct));
        sut.Writes.Should().BeTrue();
        _source.Count("a", Channel, 1);
        Observe(sut, await nodeList.ReadAsync([Channel], Accounted(2), Ct));
        Observe(sut, await nodeList.ReadAsync([Channel], Accounted(2), Ct));
        sut.Writes.Should().BeFalse();

        _source.Failure = new TimeoutException();
        await FluentActions.Awaiting(() => nodeList.ReadAsync([Channel], Accounted(2), Ct)).Should().ThrowAsync<TimeoutException>();
        _source.Failure = null;
        _source.Count("a", Channel, 2);
        var replaced = await nodeList.ReadAsync([Channel], Accounted(2), Ct);
        Observe(sut, replaced);
        Observe(sut, await nodeList.ReadAsync([Channel], Accounted(2), Ct));

        replaced.Replaced.Should().BeTrue();
        sut.Writes.Should().BeFalse("a view that failed in between does not make the replacement a node that was always there");
    }

    [Fact]
    public async Task An_unknown_node_that_takes_a_departing_nodes_slot_stays_hidden_until_the_ad_expires()
    {
        _source.Servers("a");
        _source.Count("a", Channel, 2);
        var nodeList = new PubSubNodeList(_source);
        var sut = Negotiator();
        var claims = Accounted(2);
        var none = new Dictionary<string, long>();
        for (var i = 0; i < 3; i++)
        {
            Observe(sut, await nodeList.ReadAsync([Channel], claims, none, Ct));
        }

        sut.Writes.Should().BeTrue();

        // The departing ad lists its subscription as optional, and a node that advertises nothing takes the slot: the count never moves.
        var leaving = Accounted(1);
        for (var i = 0; i < 4; i++)
        {
            Observe(sut, await nodeList.ReadAsync([Channel], Accounted(1), leaving, Ct));
            sut.Writes.Should().BeTrue("the hidden node is covered by the departing ad's optional claim");
        }

        for (var i = 0; i < 2; i++)
        {
            Observe(sut, await nodeList.ReadAsync([Channel], Accounted(1), none, Ct));
        }

        sut.Writes.Should().BeFalse("the ad expired, and the node behind the slot is unaccounted");
    }

    [Fact]
    public async Task A_deficit_of_one_view_that_clears_is_not_a_join()
    {
        _source.Servers("a");
        _source.Count("a", Channel, 1);
        var nodeList = new PubSubNodeList(_source);
        await nodeList.ReadAsync([Channel], Accounted(2), Ct);
        _source.Count("a", Channel, 2);

        (await nodeList.ReadAsync([Channel], Accounted(2), Ct)).Replaced.Should().BeFalse("a node that resubscribed was never confirmed missing");
    }

    [Fact]
    public async Task An_unadvertised_pattern_subscriber_blocks()
    {
        _source.Servers("a", "b");
        _source.Count("a", Channel, 1);
        _source.Patterns("b", 1);
        var nodeList = new PubSubNodeList(_source);
        var sut = Negotiator();

        Observe(sut, await nodeList.ReadAsync([Channel], Accounted(1), Ct));
        Observe(sut, await nodeList.ReadAsync([Channel], Accounted(1), Ct));
        var reading = await nodeList.ReadAsync([Channel], Accounted(1), Ct);
        Observe(sut, reading);

        reading.Unaccounted.Should().BeTrue("NUMSUB does not count a pattern subscriber, NUMPAT does, and nobody advertised it");
        reading.Channels.Should().Equal(PubSubNodeList.PatternKey);
        sut.Writes.Should().BeFalse();
        _source.Commands.Should().HaveCount(6, "the pattern count comes with the channel counts, one round trip per server");
    }

    [Fact]
    public async Task An_advertised_pattern_subscriber_does_not()
    {
        _source.Servers("a", "b");
        _source.Count("a", Channel, 1);
        _source.Patterns("a", 1);
        _source.Patterns("b", 1);
        var nodeList = new PubSubNodeList(_source);
        var sut = Negotiator();
        var accounted = new Dictionary<string, long> { [Channel] = 1, [PubSubNodeList.PatternKey] = 2 };

        Observe(sut, await nodeList.ReadAsync([Channel], accounted, Ct));
        Observe(sut, await nodeList.ReadAsync([Channel], accounted, Ct));

        sut.Writes.Should().BeTrue("both pattern subscriptions are claimed");
    }

    [Fact]
    public async Task Fewer_patterns_than_claimed_is_no_deficit_since_the_server_counts_unique_patterns()
    {
        _source.Servers("a");
        _source.Count("a", Channel, 1);
        var nodeList = new PubSubNodeList(_source);
        var accounted = new Dictionary<string, long> { [Channel] = 1, [PubSubNodeList.PatternKey] = 3 };

        await nodeList.ReadAsync([Channel], accounted, Ct);
        var reading = await nodeList.ReadAsync([Channel], accounted, Ct);

        reading.Short.Should().BeFalse();
        reading.Settled.Should().BeTrue();
    }

    [Fact]
    public async Task A_deficit_does_not_count_while_the_operator_attests()
    {
        _source.Servers("a");
        _source.Count("a", Channel, 0);
        _options.AssumeAllNodesCanRead = true;
        var nodeList = new PubSubNodeList(_source);
        var sut = Negotiator();

        Observe(sut, await nodeList.ReadAsync([Channel], Accounted(1), Ct));
        Observe(sut, await nodeList.ReadAsync([Channel], Accounted(1), Ct));

        sut.Writes.Should().BeTrue();
    }

    [Fact]
    public async Task Each_channel_is_counted_on_its_own()
    {
        _source.Servers("a");
        _source.Count("a", "one", 1);
        _source.Count("a", "two", 2);
        var nodeList = new PubSubNodeList(_source);

        var reading = await nodeList.ReadAsync(["one", "two"], new Dictionary<string, long> { ["one"] = 1, ["two"] = 1, ["elsewhere"] = 5 }, Ct);

        reading.Surplus.Should().Be(1);
        reading.Channels.Should().Equal("two");
    }

    [Fact]
    public async Task Numsub_denied_means_json()
    {
        _source.Servers("a", "b");
        _source.Count("a", Channel, 1);
        _source.Count("b", Channel, null);
        var nodeList = new PubSubNodeList(_source);
        var sut = Negotiator();

        var reading = await nodeList.ReadAsync([Channel], Accounted(1), Ct);
        reading.Known.Should().BeFalse("one server that refuses makes the sum a guess");
        Observe(sut, reading);
        Observe(sut, await nodeList.ReadAsync([Channel], Accounted(1), Ct));

        sut.Writes.Should().BeFalse();
        sut.Allows(Writer).Should().BeFalse();

        // The operator says the nodes it cannot count all read it.
        _options.AssumeAllNodesCanRead = true;
        var attested = Negotiator();
        Observe(attested, reading);
        attested.Writes.Should().BeTrue();
    }

    [Fact]
    public async Task Numsub_denied_is_reported_as_transport_unknown()
    {
        _source.Servers("a");
        _source.Count("a", Channel, null);
        _options.UpgradeHold = TimeSpan.Zero;
        var sut = Negotiator();
        Observe(sut, new SubscriberReading(true, false, true, 0, []));
        sut.Writes.Should().BeTrue();

        Observe(sut, await new PubSubNodeList(_source).ReadAsync([Channel], Accounted(1), Ct));

        sut.Writes.Should().BeFalse();
        _telemetry.Metrics.Should().Contain(m => m.Name == Metrics.FormatFallback && m.Properties!["reason"] == FormatNegotiator.ReasonTransportUnknown && m.Properties["kind"] == "event");
    }

    [Fact]
    public async Task Surplus_subscriber_blocks()
    {
        _source.Servers("a");
        _source.Count("a", Channel, 2);
        var nodeList = new PubSubNodeList(_source);
        var sut = Negotiator();

        Observe(sut, await nodeList.ReadAsync([Channel], Accounted(2), Ct));
        Observe(sut, await nodeList.ReadAsync([Channel], Accounted(2), Ct));
        sut.Writes.Should().BeTrue("both subscribers are accounted for");
        _source.Count("a", Channel, 3);
        Observe(sut, await nodeList.ReadAsync([Channel], Accounted(2), Ct));
        Observe(sut, await nodeList.ReadAsync([Channel], Accounted(2), Ct));

        sut.Writes.Should().BeFalse("a third subscriber that no advertisement claims");
        sut.Allows(Writer).Should().BeFalse();
        _telemetry.Metrics.Should().Contain(m => m.Name == Metrics.FormatFallback && m.Properties!["reason"] == FormatNegotiator.ReasonUnaccountedSubscriber);
        _telemetry.Metrics.Should().Contain(m => m.Name == Metrics.FormatBlockers && m.Properties!["channels"] == Channel && m.Value == 1);
    }

    [Fact]
    public async Task Debounce_two_views_for_surplus()
    {
        _source.Servers("a");
        _source.Count("a", Channel, 1);
        var nodeList = new PubSubNodeList(_source);
        var sut = Negotiator();
        Observe(sut, await nodeList.ReadAsync([Channel], Accounted(1), Ct));
        Observe(sut, await nodeList.ReadAsync([Channel], Accounted(1), Ct));
        sut.Writes.Should().BeTrue();

        // A node that has subscribed and not yet advertised is surplus for one view only.
        _source.Count("a", Channel, 2);
        Observe(sut, await nodeList.ReadAsync([Channel], Accounted(1), Ct));
        sut.Writes.Should().BeTrue("one view is not enough to take the format away");
        _source.Count("a", Channel, 2);
        Observe(sut, await nodeList.ReadAsync([Channel], Accounted(2), Ct));
        sut.Writes.Should().BeTrue("it advertised, and the surplus is gone");

        Observe(sut, await nodeList.ReadAsync([Channel], Accounted(1), Ct));
        Observe(sut, await nodeList.ReadAsync([Channel], Accounted(1), Ct));
        sut.Writes.Should().BeFalse("two views in a row");
    }

    [Fact]
    public async Task An_upgrade_needs_two_clean_views_over_the_same_servers()
    {
        _source.Servers("a", "b");
        _source.Count("a", Channel, 1);
        _source.Count("b", Channel, 0);
        var nodeList = new PubSubNodeList(_source);
        var sut = Negotiator();

        Observe(sut, await nodeList.ReadAsync([Channel], Accounted(1), Ct));
        sut.Writes.Should().BeFalse("the hold is zero, but one view is a guess");
        _source.Servers("a", "b", "c");
        Observe(sut, await nodeList.ReadAsync([Channel], Accounted(1), Ct));
        sut.Writes.Should().BeFalse("the servers changed between the two views");
        Observe(sut, await nodeList.ReadAsync([Channel], Accounted(1), Ct));
        sut.Writes.Should().BeTrue();
    }

    [Fact]
    public async Task A_surplus_between_two_clean_views_starts_the_upgrade_again()
    {
        _source.Servers("a");
        _source.Count("a", Channel, 1);
        var nodeList = new PubSubNodeList(_source);
        var sut = Negotiator();
        Observe(sut, await nodeList.ReadAsync([Channel], Accounted(1), Ct));
        _source.Count("a", Channel, 2);
        Observe(sut, await nodeList.ReadAsync([Channel], Accounted(1), Ct));
        _source.Count("a", Channel, 1);

        Observe(sut, await nodeList.ReadAsync([Channel], Accounted(1), Ct));
        sut.Writes.Should().BeFalse("the clean view after a surplus is the first of two");
        Observe(sut, await nodeList.ReadAsync([Channel], Accounted(1), Ct));

        sut.Writes.Should().BeTrue();
    }

    [Fact]
    public async Task An_ad_that_lacks_the_format_still_stops_the_writes_at_once()
    {
        _source.Servers("a");
        _source.Count("a", Channel, 1);
        var nodeList = new PubSubNodeList(_source);
        var sut = Negotiator();
        Observe(sut, await nodeList.ReadAsync([Channel], Accounted(1), Ct));
        Observe(sut, await nodeList.ReadAsync([Channel], Accounted(1), Ct));
        sut.Writes.Should().BeTrue();

        sut.Observe(new NodeListView([Stored("a", Both), Stored("old", ["json"])], [], Complete: true, await nodeList.ReadAsync([Channel], Accounted(1), Ct)));

        sut.Writes.Should().BeFalse("no debounce for a node that says what it cannot read");
    }

    [Fact]
    public async Task A_failed_read_is_not_a_view_and_starts_the_debounce_again()
    {
        _source.Servers("a");
        _source.Count("a", Channel, 1);
        var nodeList = new PubSubNodeList(_source);
        await nodeList.ReadAsync([Channel], Accounted(1), Ct);
        _source.Failure = new TimeoutException();

        await FluentActions.Awaiting(() => nodeList.ReadAsync([Channel], Accounted(1), Ct)).Should().ThrowAsync<TimeoutException>();
        _source.Failure = null;

        (await nodeList.ReadAsync([Channel], Accounted(1), Ct)).Settled.Should().BeFalse("the view before the failure does not count");
        (await nodeList.ReadAsync([Channel], Accounted(1), Ct)).Settled.Should().BeTrue();
    }

    [Fact]
    public async Task No_connected_server_is_unknown_and_so_is_no_answer_from_one_that_cannot_count()
    {
        var nodeList = new PubSubNodeList(_source);

        (await nodeList.ReadAsync([Channel], Accounted(0), Ct)).Known.Should().BeFalse("nothing to ask");
    }

    [Fact]
    public async Task A_surplus_does_not_count_while_the_operator_attests()
    {
        _source.Servers("a");
        _source.Count("a", Channel, 5);
        _options.AssumeAllNodesCanRead = true;
        var nodeList = new PubSubNodeList(_source);
        var sut = Negotiator();

        Observe(sut, await nodeList.ReadAsync([Channel], Accounted(1), Ct));
        Observe(sut, await nodeList.ReadAsync([Channel], Accounted(1), Ct));

        sut.Writes.Should().BeTrue();
    }

    private static StoredAdvertisement Stored(string id, string[] events, string[]? values = null) =>
        new(id, new("3.2.0", id, values ?? ["json", "10"], new Dictionary<string, IReadOnlyList<string>>(), EventReads: events), TimeSpan.Zero);

    private static Dictionary<string, long> Accounted(long count) => new() { [Channel] = count };

    private FormatNegotiator Negotiator() =>
        new([new DesiredWrite(Writer, ["10"], ["default"], [])], _options, _clock, _telemetry, events: new DesiredEvents(["02"]));

    private void Observe(FormatNegotiator sut, SubscriberReading reading) =>
        sut.Observe(new NodeListView([Stored("a", Both)], [], Complete: true, reading));

    private sealed class FakeSubscribers : ISubscriberSource
    {
        private readonly Dictionary<(string Server, string Channel), long?> _counts = [];
        private readonly Dictionary<string, long> _patterns = [];
        private List<EndPoint> _endpoints = [];

        public Exception? Failure { get; set; }

        public List<string> Asked { get; } = [];

        public List<string[]> Commands { get; } = [];

        public void Servers(params string[] names) => _endpoints = [.. names.Select(n => (EndPoint)new DnsEndPoint(n, 6379))];

        public void Count(string server, string channel, long? count) => _counts[(server, channel)] = count;

        public void Patterns(string server, long count) => _patterns[server] = count;

        public IReadOnlyList<EndPoint> Endpoints() => _endpoints;

        public Task<ServerCount?> CountAsync(EndPoint endpoint, IReadOnlyList<string> channels, CancellationToken cancellationToken)
        {
            var server = ((DnsEndPoint)endpoint).Host;
            Asked.Add(server);
            Commands.Add([.. channels]);
            if (Failure is { } failure)
            {
                return Task.FromException<ServerCount?>(failure);
            }

            var counts = channels.Select(channel => _counts.GetValueOrDefault((server, channel), 0L)).ToList();
            return Task.FromResult<ServerCount?>(counts.Any(count => count is null) ? null : new([.. counts.Select(count => count!.Value)], _patterns.GetValueOrDefault(server)));
        }
    }
}
