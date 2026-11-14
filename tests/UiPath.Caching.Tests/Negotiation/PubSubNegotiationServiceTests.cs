using System.Net;
using Microsoft.Extensions.Logging;
using UiPath.Caching.Negotiation;
using UiPath.Caching.Tests.Fakes;

namespace UiPath.Caching.Tests.Negotiation;

public class PubSubNegotiationServiceTests
{
    private const string Channel = "app:ps:orders";

    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly FakeNodeListServer _server = new();
    private readonly FormatNegotiationOptions _options = new() { UpgradeHold = TimeSpan.Zero };
    private readonly FakeSubscribers _subscribers;
    private readonly FakePubSub _pubSub;

    public PubSubNegotiationServiceTests()
    {
        _subscribers = new FakeSubscribers(_server.Events);
        _pubSub = new FakePubSub(_server.Events) { Channels = [Channel] };
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Subscribe_then_advertise_order()
    {
        var sut = Service(out _);

        await sut.StartAsync(Ct);
        _server.Hash["me"].Should().NotContain("sub=", "the subscription is not in place, so there is nothing to claim");
        _pubSub.Confirm(Channel);
        await UntilAsync(() => _server.Hash["me"].Contains("sub=", StringComparison.Ordinal));

        // The claim is written after the server confirmed the subscription, and at once, not at the next interval.
        var subscribed = _server.Events.IndexOf("subscribed");
        subscribed.Should().BeGreaterThan(0);
        _server.Events.Skip(subscribed + 1).Should().StartWith("write");
        NodeAdvertisement.TryParse(_server.Hash["me"].Split('|', 2)[1], out var ad).Should().BeTrue();
        ad!.Subscriptions.Should().BeEquivalentTo(new Dictionary<string, int> { [Channel] = 1 });
        await sut.StopAsync(Ct);
    }

    [Fact]
    public async Task A_subscription_that_is_not_confirmed_is_not_claimed()
    {
        var sut = Service(out _);

        await sut.StartAsync(Ct);
        await sut.AdvertiseAsync(Ct);
        await sut.TickAsync(Ct);

        _server.Hash["me"].Should().NotContain("sub=");
        await sut.StopAsync(Ct);
    }

    [Fact]
    public async Task A_subscriber_nobody_advertises_blocks_after_two_views_and_goes_when_it_does()
    {
        var sut = Service(out var negotiator);
        _pubSub.Confirm(Channel);
        _subscribers.Count(Channel, 1);
        await sut.StartAsync(Ct);
        await sut.AdvertiseAsync(Ct);
        await sut.RefreshAsync(Ct);
        negotiator.Writes.Should().BeTrue("this node is the only subscriber, and it says so");

        _subscribers.Count(Channel, 2);
        await sut.RefreshAsync(Ct);
        negotiator.Writes.Should().BeTrue("one view is no proof");
        await sut.RefreshAsync(Ct);
        negotiator.Writes.Should().BeFalse("a second subscriber that no advertisement claims");

        _server.Put("urn:other", _server.NowMs + $"|lib=3.2;inst=o;vr=json,10;er=json,02;sub={Channel.Replace(":", "%3A", StringComparison.Ordinal)}:1");
        await sut.RefreshAsync(Ct);
        await sut.RefreshAsync(Ct);
        negotiator.Writes.Should().BeTrue("that node advertised its subscription");
        await sut.StopAsync(Ct);
    }

    [Fact]
    public async Task A_node_that_stops_keeps_the_fleet_on_the_format_whether_its_connection_closes_at_once_or_views_later()
    {
        _options.UpgradeHold = TimeSpan.FromHours(1);
        var claim = Channel.Replace(":", "%3A", StringComparison.Ordinal);
        var sut = Service(out var negotiator);
        _pubSub.Confirm(Channel);
        _subscribers.Count(Channel, 2);
        _server.Put("urn:stopping", _server.NowMs + $"|lib=3.2;inst=s;vr=json;er=json,02;sub={claim}:1");

        // Driven by hand: started, its timer would tick whenever the test moves the clock.
        await sut.AdvertiseAsync(Ct);
        await sut.RefreshAsync(Ct);
        await sut.RefreshAsync(Ct);
        _clock.Advance(TimeSpan.FromHours(1));
        await sut.RefreshAsync(Ct);
        negotiator.Writes.Should().BeTrue();

        // It writes left=1 and its connection stays open for three views, as a slow shutdown leaves it: the server still counts a subscriber no reader claims.
        _server.Put("urn:stopping", _server.NowMs + $"|lib=3.2;inst=s;vr=json;er=json,02;sub={claim}:1;left=1");
        for (var view = 0; view < 3; view++)
        {
            await sut.RefreshAsync(Ct);
            negotiator.Writes.Should().BeTrue($"the leaving node may still hold its subscription, view {view + 1}");
        }

        // Then it closes, and the count falls to what the nodes that read claim.
        _subscribers.Count(Channel, 1);
        for (var view = 0; view < 3; view++)
        {
            await sut.RefreshAsync(Ct);
            negotiator.Writes.Should().BeTrue($"a leaving node's claim is optional, view {view + 1}");
        }
    }

    [Fact]
    public async Task A_departing_advertisement_lists_the_subscriptions_still_held()
    {
        var sut = Service(out _);
        _pubSub.Confirm(Channel);
        await sut.StartAsync(Ct);

        await sut.StopAsync(Ct);

        NodeAdvertisement.TryParse(_server.Hash["me"].Split('|', 2)[1], out var ad).Should().BeTrue();
        ad!.Left.Should().BeTrue();
        ad.Subscriptions.Should().BeEquivalentTo(new Dictionary<string, int> { [Channel] = 1 });
    }

    [Fact]
    public async Task A_subscriber_nobody_claims_after_a_node_left_is_still_a_surplus()
    {
        var sut = Service(out var negotiator);
        _pubSub.Confirm(Channel);
        _subscribers.Count(Channel, 3);
        _server.Put("urn:gone", _server.NowMs + $"|lib=3.2;inst=g;vr=json;er=json,02;sub={Channel.Replace(":", "%3A", StringComparison.Ordinal)}:1;left=1");
        await sut.AdvertiseAsync(Ct);
        await sut.RefreshAsync(Ct);
        await sut.RefreshAsync(Ct);
        await sut.RefreshAsync(Ct);

        negotiator.Writes.Should().BeFalse("this node and the one that left account for two of the three");
    }

    [Fact]
    public async Task No_advertisement_that_is_not_leaving_is_written_after_the_node_stopped()
    {
        var sut = Service(out _);
        _pubSub.Confirm(Channel);
        await sut.StartAsync(Ct);
        await sut.StopAsync(Ct);
        _server.Hash["me"].Should().Contain("left=1");

        // The confirmation of a subscription, or a tick, that was already on its way.
        await sut.AdvertiseAsync(Ct);
        _pubSub.Confirm(Channel);
        await Task.Delay(100, Ct);

        _server.Hash["me"].Should().Contain("left=1", "a node that said it was leaving must not look alive again");
    }

    [Fact]
    public async Task Advertisements_are_written_one_at_a_time()
    {
        var sut = Service(out _);
        _pubSub.Confirm(Channel);
        _server.WriteDelay = () => Task.Delay(30);

        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => sut.AdvertiseAsync(Ct)));

        _server.MaxConcurrentWrites.Should().Be(1, "a write started earlier must not land over one started later");
    }

    [Fact]
    public async Task A_stop_waits_for_an_advertisement_in_flight_so_it_cannot_land_after_the_departing_one()
    {
        var sut = Service(out _);
        _pubSub.Confirm(Channel);
        var release = new TaskCompletionSource();
        _server.WriteDelay = () => release.Task;
        var inFlight = sut.AdvertiseAsync(Ct);
        await Task.Delay(50, Ct);
        _server.WriteDelay = null;

        var stopping = sut.StopAsync(Ct);
        await Task.Delay(50, Ct);
        stopping.IsCompleted.Should().BeFalse("it is behind the write in flight");
        release.SetResult();
        await Task.WhenAll(inFlight, stopping);

        _server.Hash["me"].Should().Contain("left=1");
    }

    [Fact]
    public async Task A_failed_ad_read_breaks_the_run_of_views()
    {
        var sut = Service(out var negotiator);
        _pubSub.Confirm(Channel);
        _subscribers.Count(Channel, 1);
        await sut.AdvertiseAsync(Ct);
        await sut.RefreshAsync(Ct);

        _server.Failure = new TimeoutException();
        await sut.RefreshAsync(Ct);
        _server.Failure = null;
        await sut.RefreshAsync(Ct);
        negotiator.Writes.Should().BeFalse("the view before the failure does not count, so this is the first of two");
        await sut.RefreshAsync(Ct);

        negotiator.Writes.Should().BeTrue();
    }

    [Fact]
    public async Task A_pattern_subscriber_nobody_advertises_blocks_and_one_that_is_advertised_does_not()
    {
        var logger = new RecordedLogger();
        var sut = Service(out var negotiator, logger);
        _pubSub.Confirm(Channel);
        _subscribers.Count(Channel, 1);
        _subscribers.Patterns = 1;
        await sut.AdvertiseAsync(Ct);
        await sut.RefreshAsync(Ct);
        await sut.RefreshAsync(Ct);
        await sut.RefreshAsync(Ct);
        negotiator.Writes.Should().BeFalse("a pattern subscriber on the server, say another app's, that no node of this one claims");
        logger.Records.Should().ContainSingle(r => r.Level == LogLevel.Warning && r.Message.Contains("(patterns)", StringComparison.Ordinal))
            .Which.Message.Should().Contain("whole server").And.Contain("AssumeAllNodesCanRead");

        _server.Put("urn:patterns", _server.NowMs + "|lib=3.2;inst=p;vr=json;er=json,02;psub=1");
        await sut.RefreshAsync(Ct);
        await sut.RefreshAsync(Ct);

        negotiator.Writes.Should().BeTrue("a node that reads advertised the pattern it holds");
    }

    [Fact]
    public async Task The_advertisement_lists_the_pattern_subscriptions_this_node_holds()
    {
        _pubSub.PatternCount = 2;
        var sut = Service(out _);

        await sut.AdvertiseAsync(Ct);

        NodeAdvertisement.TryParse(_server.Hash["me"].Split('|', 2)[1], out var ad).Should().BeTrue();
        ad!.PatternSubscriptions.Should().Be(2);
    }

    [Fact]
    public async Task A_view_with_no_channel_to_count_breaks_the_run_of_views()
    {
        var sut = Service(out var negotiator);
        _pubSub.Confirm(Channel);
        _subscribers.Count(Channel, 1);
        await sut.AdvertiseAsync(Ct);
        await sut.RefreshAsync(Ct);

        _pubSub.Channels = [];
        await sut.RefreshAsync(Ct);
        _pubSub.Channels = [Channel];
        await sut.RefreshAsync(Ct);
        negotiator.Writes.Should().BeFalse("the view before the gap does not count");
        await sut.RefreshAsync(Ct);

        negotiator.Writes.Should().BeTrue();
    }

    [Fact]
    public async Task One_numsub_command_per_server_per_view_whatever_the_channels()
    {
        _pubSub.Channels = ["one", "two", "three"];
        var sut = Service(out _);
        await sut.AdvertiseAsync(Ct);

        await sut.RefreshAsync(Ct);
        await sut.RefreshAsync(Ct);

        _server.Events.Count(e => e == "numsub").Should().Be(2, "one server, two views");
    }

    [Fact]
    public async Task A_refused_numsub_is_logged_once_with_the_fallback_and_again_when_it_is_counted_again()
    {
        var logger = new RecordedLogger();
        var sut = Service(out var negotiator, logger);
        _pubSub.Confirm(Channel);
        _subscribers.Denied = true;
        await sut.AdvertiseAsync(Ct);

        await sut.RefreshAsync(Ct);
        await sut.RefreshAsync(Ct);
        await sut.RefreshAsync(Ct);

        negotiator.Writes.Should().BeFalse();
        logger.Records.Where(r => r.Level == LogLevel.Warning && r.Message.Contains("cannot be counted", StringComparison.Ordinal)).Should().ContainSingle()
            .Which.Message.Should().Contain("refused PUBSUB NUMSUB").And.Contain("binary formats stay JSON").And.Contain("AssumeAllNodesCanRead");

        _subscribers.Denied = false;
        _subscribers.Count(Channel, 1);
        await sut.RefreshAsync(Ct);
        logger.Records.Should().Contain(r => r.Level == LogLevel.Information && r.Message.Contains("counted again", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_pattern_or_sharded_channel_cannot_be_counted_so_the_node_stays_on_json_unless_attested()
    {
        var logger = new RecordedLogger();
        _pubSub.Uncountable = ["app:ps:orders:*"];
        _subscribers.Count(Channel, 1);
        var sut = Service(out var negotiator, logger);
        _pubSub.Confirm(Channel);
        await sut.AdvertiseAsync(Ct);

        await sut.RefreshAsync(Ct);
        await sut.RefreshAsync(Ct);

        negotiator.Writes.Should().BeFalse();
        logger.Records.Should().ContainSingle(r => r.Level == LogLevel.Warning && r.Message.Contains("app:ps:orders:*", StringComparison.Ordinal) && r.Message.Contains("patterns or sharded", StringComparison.Ordinal));
        _server.Events.Should().NotContain("numsub", "it does not ask for a count it cannot read");

        _options.AssumeAllNodesCanRead = true;
        await sut.RefreshAsync(Ct);
        negotiator.Writes.Should().BeTrue("the operator's word, with a hold of zero here");
    }

    [Fact]
    public async Task A_node_that_crashed_leaves_a_deficit_until_its_advertisement_expires_and_then_the_decision_resumes_without_a_new_hold()
    {
        _options.UpgradeHold = TimeSpan.FromHours(1);
        var claim = $"|lib=3.2;inst=c;vr=json;er=json,02;sub={Channel.Replace(":", "%3A", StringComparison.Ordinal)}:1";
        var sut = Service(out var negotiator);
        _pubSub.Confirm(Channel);
        _subscribers.Count(Channel, 2);
        _server.Put("urn:crashing", _server.NowMs + claim);

        // Driven by hand: started, its timer would tick whenever the test moves the clock.
        await sut.AdvertiseAsync(Ct);
        await sut.RefreshAsync(Ct);
        await sut.RefreshAsync(Ct);
        _clock.Advance(TimeSpan.FromHours(1));
        await sut.RefreshAsync(Ct);
        negotiator.Writes.Should().BeTrue();

        // It crashes: its connection is gone, its advertisement is not.
        _subscribers.Count(Channel, 1);
        await sut.RefreshAsync(Ct);
        negotiator.Writes.Should().BeTrue("one view is absorbed");
        await sut.RefreshAsync(Ct);
        negotiator.Writes.Should().BeFalse("a claim with no subscriber behind it hides a subscriber that says nothing");

        // The advertisement is older than it lives; this node's own is fresh.
        _server.NowMs += 151_000;
        await sut.AdvertiseAsync(Ct);
        await sut.RefreshAsync(Ct);
        negotiator.Writes.Should().BeFalse("one clean view is not enough");
        await sut.RefreshAsync(Ct);

        negotiator.Writes.Should().BeTrue("it was a departure, not a join, so the hour already waited stands");
    }

    [Fact]
    public async Task An_app_with_only_a_pub_sub_transport_reads_the_node_list_and_is_not_blind()
    {
        var sut = Service(out var negotiator);
        _pubSub.Confirm(Channel);
        _subscribers.Count(Channel, 1);

        await sut.StartAsync(Ct);
        await sut.RefreshAsync(Ct);

        _server.Events.Should().Contain("read").And.Contain("numsub");
        negotiator.Writes.Should().BeTrue();
        await sut.StopAsync(Ct);
    }

    [Fact]
    public async Task A_node_with_no_channel_yet_has_no_transport_to_read_and_writes_json()
    {
        _pubSub.Channels = [];
        var sut = Service(out var negotiator);

        await sut.StartAsync(Ct);
        await sut.RefreshAsync(Ct);

        negotiator.Writes.Should().BeFalse("the node list cannot see a transport yet");
        _server.Events.Should().NotContain("numsub");
        await sut.StopAsync(Ct);
    }

    [Fact]
    public async Task A_subscriber_count_that_cannot_be_read_keeps_the_decision_until_it_is_too_old()
    {
        var sut = Service(out var negotiator);
        _pubSub.Confirm(Channel);
        _subscribers.Count(Channel, 1);
        await sut.StartAsync(Ct);
        await sut.RefreshAsync(Ct);
        negotiator.Writes.Should().BeTrue();

        _subscribers.Failure = new TimeoutException();
        await sut.RefreshAsync(Ct);
        negotiator.Writes.Should().BeTrue("a failed read is not an answer");
        _clock.Advance(_options.MaxViewAge + TimeSpan.FromSeconds(1));

        negotiator.Writes.Should().BeFalse();
        await sut.StopAsync(Ct);
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "The condition did not hold in time.");
            await Task.Delay(10, Ct);
        }
    }

    private FormatNegotiationService Service(out FormatNegotiator negotiator, ILogger? logger = null)
    {
        negotiator = new FormatNegotiator([], _options, _clock, logger: logger, events: new DesiredEvents(["02"]));
        return new FormatNegotiationService(new FormatNegotiation
        {
            Negotiator = negotiator,
            Advertisement = new("3.2.0", "me", ["json"], new Dictionary<string, IReadOnlyList<string>>(), EventReads: ["json", "02"]),
            NodeId = "me",
            Store = new NodeListStore(_server.Connector, "ads"),
            NodeList = new StreamNodeList(new NoGroups(), _options),
            PubSub = _pubSub,
            Subscribers = new PubSubNodeList(_subscribers),
            Options = _options,
            Clock = _clock,
        });
    }

    private sealed class NoGroups : IStreamGroupSource
    {
        public Task<IReadOnlyList<string>> GroupsAsync(StackExchange.Redis.RedisKey stream, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<TimeSpan?> IdleAsync(StackExchange.Redis.RedisKey stream, string group, CancellationToken cancellationToken) => Task.FromResult<TimeSpan?>(null);
    }

    private sealed class FakePubSub(List<string> events) : IPubSubSubscriptions
    {
        private readonly Dictionary<string, int> _confirmed = [];
        private Action? _handler;

        public IReadOnlyList<string> Channels { get; set; } = [];

        public IReadOnlyList<string> Uncountable { get; set; } = [];

        public int PatternCount { get; set; }

        public void Confirm(string channel)
        {
            lock (_confirmed)
            {
                _confirmed[channel] = 1;
            }

            events.Add("subscribed");
            _handler?.Invoke();
        }

        IReadOnlyList<string> IPubSubSubscriptions.Channels() => Channels;

        IReadOnlyList<string> IPubSubSubscriptions.Uncountable() => Uncountable;

        int IPubSubSubscriptions.Patterns() => PatternCount;

        IReadOnlyDictionary<string, int> IPubSubSubscriptions.Confirmed()
        {
            lock (_confirmed)
            {
                return new Dictionary<string, int>(_confirmed);
            }
        }

        void IPubSubSubscriptions.WhenConfirmed(Action handler) => _handler = handler;
    }

    private sealed class FakeSubscribers(List<string> events) : ISubscriberSource
    {
        private readonly Dictionary<string, long> _counts = [];

        public Exception? Failure { get; set; }

        public bool Denied { get; set; }

        public long Patterns { get; set; }

        public void Count(string channel, long count) => _counts[channel] = count;

        public IReadOnlyList<EndPoint> Endpoints() => [new DnsEndPoint("a", 6379)];

        public Task<ServerCount?> CountAsync(EndPoint endpoint, IReadOnlyList<string> channels, CancellationToken cancellationToken)
        {
            events.Add("numsub");
            return Failure is { } failure
                ? Task.FromException<ServerCount?>(failure)
                : Task.FromResult<ServerCount?>(Denied ? null : new([.. channels.Select(channel => _counts.GetValueOrDefault(channel))], Patterns));
        }
    }
}
