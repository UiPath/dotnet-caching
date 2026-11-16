using StackExchange.Redis;
using UiPath.Caching.Negotiation;
using UiPath.Caching.Tests.Fakes;

namespace UiPath.Caching.Tests.Negotiation;

public class FormatNegotiationServiceTests
{
    private static readonly string[] Binary = ["10"];
    private static readonly RedisKey Stream = "app:st:orders";

    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly FakeNodeListServer _server = new();
    private readonly FormatNegotiationOptions _options = new() { UpgradeHold = TimeSpan.Zero };
    private readonly FakeValueSerializer _writer = new(0x10);
    private readonly List<string> _events;
    private readonly TestGroups _groups;

    public FormatNegotiationServiceTests()
    {
        _events = _server.Events;
        _groups = new TestGroups(_events);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task New_pod_advertises_before_it_reads_the_node_list()
    {
        var sut = Service(streams: [Stream]);

        await sut.StartAsync(Ct);

        // The write comes before the first read and before the first look at any group, so this node is never an unaccounted one.
        _events.Should().StartWith(["write", "read", "groups"]);
        await sut.StopAsync(Ct);
    }

    [Fact]
    public async Task A_graceful_stop_writes_a_departing_ad_instead_of_deleting_it_and_is_idempotent()
    {
        var sut = Service(streams: [Stream]);
        await sut.StartAsync(Ct);

        await sut.StopAsync(Ct);
        await sut.StopAsync(Ct);

        _server.Hash["me"].Should().Contain("left=1").And.Contain("vr=json,10");
    }

    [Fact]
    public async Task A_stop_with_redis_down_does_not_throw()
    {
        var sut = Service(streams: [Stream]);
        await sut.StartAsync(Ct);
        _server.Failure = new TimeoutException();

        await FluentActions.Awaiting(() => sut.StopAsync(Ct)).Should().NotThrowAsync();
    }

    [Fact]
    public async Task A_node_that_registers_nothing_touches_neither_redis_nor_a_timer()
    {
        var sut = new FormatNegotiationService(FormatNegotiation.Inactive);

        await sut.StartAsync(Ct);
        await sut.StopAsync(Ct);

        _server.Database.ReceivedCalls().Should().BeEmpty();
        _events.Should().BeEmpty();
    }

    [Fact]
    public async Task A_legacy_group_keeps_the_writer_on_json_until_it_goes_idle()
    {
        _groups.Add("legacy", TimeSpan.FromSeconds(10));
        var sut = Service(streams: [Stream], out var negotiator);
        await sut.StartAsync(Ct);

        negotiator.Allows(_writer).Should().BeFalse();

        _groups.Add("legacy", _options.LivenessWindow + TimeSpan.FromSeconds(1));
        await sut.RefreshAsync(Ct);

        negotiator.Allows(_writer).Should().BeTrue();
        await sut.StopAsync(Ct);
    }

    [Fact]
    public async Task A_group_no_consumer_ever_read_is_nobody()
    {
        _groups.Add("made-by-tooling", idle: null);
        var sut = Service(streams: [Stream], out var negotiator);

        await sut.StartAsync(Ct);

        negotiator.Allows(_writer).Should().BeTrue();
        await sut.StopAsync(Ct);
    }

    [Fact]
    public async Task A_crashed_node_whose_ad_outlives_its_group_never_makes_the_fleet_fall_back()
    {
        _options.UpgradeHold = TimeSpan.FromMinutes(1);
        var sut = Service(streams: [Stream], out var negotiator);
        _server.Put("urn:dead", _server.NowMs + "|lib=3.2;inst=d;vr=json,10");
        _groups.Add("urn:dead", TimeSpan.FromSeconds(1));
        await sut.StartAsync(Ct);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await sut.RefreshAsync(Ct);
        negotiator.Allows(_writer).Should().BeTrue();

        // 60 s on: the crashed node's ad is a minute old and its group has been silent for a minute. Neither the ad, which has not
        // expired, nor the group, which is not yet idle long enough to be nobody, may count it as unaccounted for.
        _server.NowMs += 60_000;
        _groups.Add("urn:dead", TimeSpan.FromSeconds(60));
        await sut.AdvertiseAsync(Ct);
        await sut.RefreshAsync(Ct);

        negotiator.Allows(_writer).Should().BeTrue();
        await sut.StopAsync(Ct);
    }

    [Fact]
    public async Task A_restart_under_the_same_id_is_not_hidden_by_the_previous_runs_departing_ad()
    {
        var sut = Service(streams: [Stream], out var negotiator);
        _server.Put("urn:x", _server.NowMs - 100_000 + "|lib=3.2;inst=x;vr=json,10;left=1");
        _groups.Add("urn:x", TimeSpan.FromSeconds(100));
        await sut.StartAsync(Ct);
        negotiator.Allows(_writer).Should().BeTrue();

        _groups.Add("urn:x", TimeSpan.FromSeconds(1));
        await sut.RefreshAsync(Ct);

        negotiator.Allows(_writer).Should().BeFalse("a node read under that id after its ad was written, and may be a build that cannot read the format");
        await sut.StopAsync(Ct);
    }

    [Fact]
    public async Task A_node_that_left_gracefully_is_neither_a_reader_nor_unaccounted_for()
    {
        _options.UpgradeHold = TimeSpan.FromMinutes(1);
        _server.Put("urn:leaver", _server.NowMs + "|lib=3.2;inst=l;vr=json;left=1");
        _groups.Add("urn:leaver", TimeSpan.FromSeconds(3));
        var sut = Service(streams: [Stream], out var negotiator);
        await sut.StartAsync(Ct);
        _clock.Advance(TimeSpan.FromMinutes(1));

        await sut.RefreshAsync(Ct);

        // Its ad lists no format, but a node that has left reads nothing, so it must not hold the format back either.
        negotiator.Allows(_writer).Should().BeTrue();
        await sut.StopAsync(Ct);
    }

    [Fact]
    public async Task A_departure_does_not_restart_the_hold()
    {
        _options.UpgradeHold = TimeSpan.FromMinutes(1);
        _server.Put("urn:other", _server.NowMs + "|lib=3.2;inst=o;vr=json,10");
        var sut = Service(streams: [Stream], out var negotiator);
        await sut.StartAsync(Ct);
        _clock.Advance(TimeSpan.FromSeconds(50));
        _server.NowMs += 50_000;
        _server.Put("urn:other", _server.NowMs - 50_000 + "|lib=3.2;inst=o;vr=json,10;left=1");
        await sut.RefreshAsync(Ct);
        _clock.Advance(TimeSpan.FromSeconds(11));

        await sut.RefreshAsync(Ct);

        negotiator.Allows(_writer).Should().BeTrue();
        await sut.StopAsync(Ct);
    }

    [Fact]
    public async Task After_an_outage_longer_than_the_ad_lifetime_the_decision_is_kept_while_peers_advertise_again()
    {
        _options.UpgradeHold = TimeSpan.FromMinutes(1);
        _server.Put("urn:peer", _server.NowMs + "|lib=3.2;inst=p;vr=json,10");
        var sut = Service(streams: [Stream], out var negotiator);
        await sut.StartAsync(Ct);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await sut.RefreshAsync(Ct);
        negotiator.Allows(_writer).Should().BeTrue();

        _server.Failure = new TimeoutException();
        await sut.TickAsync(Ct);
        _clock.Advance(TimeSpan.FromSeconds(60));
        _server.NowMs += 200_000;
        _server.Failure = null;
        _groups.Add("urn:peer", TimeSpan.FromSeconds(70));

        // Redis is back after longer than an ad lives. This node advertises first; the peer has not yet.
        await sut.TickAsync(Ct);

        negotiator.Allows(_writer).Should().BeTrue();
        await sut.StopAsync(Ct);
    }

    [Fact]
    public async Task A_stale_ad_is_ignored_and_a_long_departed_one_is_removed()
    {
        _server.Put("old-no-format", _server.NowMs - 1_000_000 + "|lib=1;inst=x;vr=json");
        _server.Put("departed", _server.NowMs - 1_000_000_000 + "|lib=1;inst=x;vr=json");
        var sut = Service(streams: [Stream], out var negotiator);

        await sut.StartAsync(Ct);

        negotiator.Allows(_writer).Should().BeTrue();
        _server.Hash.Keys.Should().NotContain("departed");
        await sut.StopAsync(Ct);
    }

    [Fact]
    public async Task A_fresh_ad_without_the_format_stops_the_writes_at_the_next_refresh()
    {
        var sut = Service(streams: [Stream], out var negotiator);
        await sut.StartAsync(Ct);
        negotiator.Allows(_writer).Should().BeTrue();

        _server.Put("old", _server.NowMs + "|lib=3.1;inst=x;vr=json");
        await sut.RefreshAsync(Ct);

        negotiator.Allows(_writer).Should().BeFalse();
        await sut.StopAsync(Ct);
    }

    [Fact]
    public async Task Without_a_streams_transport_the_node_list_is_blind_and_only_the_operators_word_switches()
    {
        var sut = Service(streams: null, out var negotiator);
        await sut.StartAsync(Ct);
        negotiator.Allows(_writer).Should().BeFalse();
        _server.Database.ReceivedCalls().Should().BeEmpty("a blind node list has nothing to read or write");

        _options.AssumeAllNodesCanRead = true;
        sut = Service(streams: null, out negotiator);
        await sut.StartAsync(Ct);
        negotiator.Allows(_writer).Should().BeTrue("the hold is zero here");
        _server.Database.ReceivedCalls().Should().BeEmpty();
        await sut.StopAsync(Ct);

        _options.UpgradeHold = TimeSpan.FromMinutes(1);
        sut = Service(streams: null, out negotiator);
        await sut.StartAsync(Ct);
        negotiator.Allows(_writer).Should().BeFalse("the hold starts at the first decision even when the operator attests");
        await sut.StopAsync(Ct);
    }

    [Fact]
    public async Task A_node_list_that_cannot_be_read_does_not_fail_start()
    {
        _server.Failure = new TimeoutException();
        var sut = Service(streams: [Stream], out var negotiator);

        await sut.StartAsync(Ct);

        negotiator.Allows(_writer).Should().BeFalse();
        await sut.StopAsync(Ct);
    }

    [Fact]
    public async Task A_reader_only_node_advertises_what_it_reads_and_never_reads_the_node_list()
    {
        var sut = ReaderOnly();

        await sut.StartAsync(Ct);

        _server.Hash.Should().ContainKey("me").WhoseValue.Should().Contain("vr=json,10");
        _events.Should().Equal("write");
        await sut.StopAsync(Ct);
    }

    [Fact]
    public async Task A_writer_and_a_reader_only_node_switch_after_the_hold()
    {
        _options.UpgradeHold = TimeSpan.FromMinutes(1);
        _server.Put("reader", _server.NowMs + "|lib=3.2;inst=r;vr=json,10");
        var sut = Service(streams: [Stream], out var negotiator);
        await sut.StartAsync(Ct);
        negotiator.Allows(_writer).Should().BeFalse();

        _clock.Advance(TimeSpan.FromMinutes(1));
        _server.Put("reader", _server.NowMs + "|lib=3.2;inst=r;vr=json,10");
        await sut.RefreshAsync(Ct);

        negotiator.Allows(_writer).Should().BeTrue();
        await sut.StopAsync(Ct);
    }

    [Fact]
    public async Task A_node_in_force_mode_still_advertises_what_it_reads()
    {
        var sut = ReaderOnly();

        await sut.StartAsync(Ct);

        _server.Hash.Should().ContainKey("me");
        await sut.StopAsync(Ct);
    }

    private FormatNegotiationService ReaderOnly() => new(new FormatNegotiation
    {
        Advertisement = new("3.2.0", "me", ["json", "10"], new Dictionary<string, IReadOnlyList<string>>()),
        NodeId = "me",
        Store = new NodeListStore(_server.Connector, "ads"),
        NodeList = new StreamNodeList(_groups, _options),
        StreamKeys = () => [Stream],
        Options = _options,
        Clock = _clock,
    });

    private FormatNegotiationService Service(RedisKey[]? streams) => Service(streams, out _);

    private FormatNegotiationService Service(RedisKey[]? streams, out FormatNegotiator negotiator)
    {
        negotiator = new FormatNegotiator([new DesiredWrite(_writer, Binary, ["default"], [])], _options, _clock);
        return new FormatNegotiationService(new FormatNegotiation
        {
            Negotiator = negotiator,
            Advertisement = new("3.2.0", "me", ["json", "10"], new Dictionary<string, IReadOnlyList<string>>()),
            NodeId = "me",
            Store = new NodeListStore(_server.Connector, "ads"),
            NodeList = new StreamNodeList(_groups, _options),
            StreamKeys = streams is null ? null : () => streams,
            Options = _options,
            Clock = _clock,
        });
    }

    private sealed class TestGroups(List<string> events) : IStreamGroupSource
    {
        private readonly Dictionary<string, TimeSpan?> _idle = [];

        public void Add(string group, TimeSpan? idle) => _idle[group] = idle;

        public Task<IReadOnlyList<string>> GroupsAsync(RedisKey stream, CancellationToken cancellationToken)
        {
            events.Add("groups");
            return Task.FromResult<IReadOnlyList<string>>([.. _idle.Keys]);
        }

        public Task<TimeSpan?> IdleAsync(RedisKey stream, string group, CancellationToken cancellationToken) => Task.FromResult(_idle[group]);
    }
}
