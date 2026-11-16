using UiPath.Caching.Broadcast;
using UiPath.Caching.MessagePack;
using UiPath.Caching.Negotiation;
using UiPath.Caching.Tests.Fakes;
using UiPath.Caching.Tests.Telemetry;

namespace UiPath.Caching.Tests.Negotiation;

public class EventFormatNegotiationTests
{
    private static readonly string[] Binary = ["02"];
    private static readonly string[] Json = ["json"];
    private static readonly string[] Both = ["json", "02"];
    private static readonly string[] Value = ["10"];

    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly RecordingTelemetryProvider _telemetry = new();
    private readonly FakeValueSerializer _writer = new(0x10);
    private readonly FormatNegotiationOptions _options = new();

    [Fact]
    public void Binary_only_when_all_can_read_events()
    {
        var sut = Negotiator();
        sut.Writes.Should().BeFalse("no node list has been read");

        Settle(sut, View(Node("a", Both), Node("b", Both)));
        sut.Writes.Should().BeTrue();

        sut.Observe(View(Node("a", Both), Node("b", Both), Node("c", Json)));
        sut.Writes.Should().BeFalse("one node that reads only JSON events");
        _telemetry.Metrics.Should().Contain(m => m.Name == Metrics.FormatFallback && m.Properties!["reason"] == FormatNegotiator.ReasonMissingFormat && m.Properties["kind"] == "event");
        _telemetry.Metrics.Where(m => m.Name == Metrics.FormatFallback && m.Properties!["kind"] == "event").Should().OnlyContain(m => !m.Properties!.ContainsKey("policy"), "an event has no policy, and an empty tag is a tag value all the same");
    }

    [Fact]
    public void The_switch_waits_for_the_hold_and_the_fall_back_does_not()
    {
        var sut = Negotiator();
        var view = View(Node("a", Both));

        sut.Observe(view);
        sut.Writes.Should().BeFalse("the node list has only just agreed");
        _clock.Advance(_options.UpgradeHold - TimeSpan.FromSeconds(1));
        sut.Observe(view);
        sut.Writes.Should().BeFalse();
        _clock.Advance(TimeSpan.FromSeconds(1));
        sut.Observe(view);
        sut.Writes.Should().BeTrue();

        sut.Observe(View(Node("a", Both), Node("old", Json)));

        sut.Writes.Should().BeFalse("the first node that cannot read them stops the writes at once");
    }

    [Fact]
    public void Legacy_group_flips_event_writes_to_json()
    {
        var sut = Negotiator();
        Settle(sut, View(Node("a", Both)));
        sut.Writes.Should().BeTrue();

        sut.Observe(new NodeListView([Stored(Node("a", Both))], ["urn:legacy"], Complete: true));

        sut.Writes.Should().BeFalse();
        _telemetry.Metrics.Should().Contain(m => m.Name == Metrics.FormatFallback && m.Properties!["reason"] == FormatNegotiator.ReasonUnaccountedGroup && m.Properties["kind"] == "event");
    }

    [Fact]
    public void A_node_without_the_decoder_blocks_binary_events_and_not_values()
    {
        var sut = Negotiator(values: true);
        var withoutDecoder = Node("plain", Json, valueReads: ["json", "10"]);

        Settle(sut, View(Node("a", Both, valueReads: ["json", "10"]), withoutDecoder));

        sut.Writes.Should().BeFalse("it lists er=json");
        sut.Allows(_writer).Should().BeTrue("it reads the value format, and the two decisions are apart");
    }

    [Fact]
    public void An_ad_that_lists_no_event_formats_blocks_binary_events()
    {
        var sut = Negotiator();
        var ad = new NodeAdvertisement("3.2.0", "pre", ["json"], new Dictionary<string, IReadOnlyList<string>>());

        Settle(sut, View(Node("a", Both), ("pre", ad)));

        sut.Writes.Should().BeFalse("an advertisement that says nothing about events is a build that does not know them");
    }

    [Fact]
    public void A_node_that_is_not_set_to_write_binary_events_never_does()
    {
        var sut = new FormatNegotiator([], _options, _clock, _telemetry);

        Settle(sut, View(Node("a", Both)));

        sut.Writes.Should().BeFalse();
    }

    [Fact]
    public void A_view_that_stops_refreshing_ends_binary_events_after_the_max_view_age()
    {
        var sut = Negotiator();
        Settle(sut, View(Node("a", Both)));
        sut.Writes.Should().BeTrue();

        _clock.Advance(_options.MaxViewAge + TimeSpan.FromSeconds(1));

        sut.Writes.Should().BeFalse();
    }

    [Fact]
    public void The_effective_event_format_is_reported_with_its_kind_and_no_node_id()
    {
        var sut = Negotiator();
        Settle(sut, View(Node("node-id-that-must-not-be-a-tag", Both)));

        _telemetry.Metrics.Should().Contain(m => m.Name == Metrics.FormatEffective && m.Properties!["kind"] == "event" && m.Properties["format"] == "02" && m.Value == 1);
        _telemetry.Metrics.Where(m => m.Name.StartsWith("cache.format.", StringComparison.Ordinal))
            .SelectMany(m => (IEnumerable<string>?)m.Properties?.Values ?? [])
            .Should().NotContain(value => value.Contains("node-id", StringComparison.Ordinal));
    }

    [Fact]
    public void A_blocking_stream_node_is_named_on_the_blockers_metric_and_never_on_the_bounded_ones()
    {
        var sut = Negotiator();

        sut.Observe(new NodeListView([Stored(Node("a", Both))], ["urn:legacy"], Complete: true));

        _telemetry.Metrics.Should().Contain(m => m.Name == Metrics.FormatBlockers && m.Properties!["nodes"] == "urn:legacy");
        _telemetry.Metrics.Where(m => m.Name.StartsWith("cache.format.", StringComparison.Ordinal))
            .SelectMany(m => (IEnumerable<string>?)m.Properties?.Values ?? [])
            .Should().NotContain("urn:legacy");
    }

    [Fact]
    public void Force_writes_binary_events_at_once_and_a_node_set_to_json_never_does()
    {
        var forced = new FormatNegotiation { Negotiator = null, Advertisement = new("3.2.0", "me", ["json"], new Dictionary<string, IReadOnlyList<string>>()) };
        var automatic = new FormatNegotiation { Negotiator = Negotiator(), Advertisement = forced.Advertisement };

        forced.EventWrites(new BinaryEventSettings(true)).Writes.Should().BeTrue("Force writes what is configured, with no node list");
        automatic.EventWrites(new BinaryEventSettings(true)).Writes.Should().BeFalse("Auto waits for a node list");
        forced.EventWrites(new BinaryEventSettings(false)).Writes.Should().BeFalse();
        automatic.EventWrites(new BinaryEventSettings(false)).Writes.Should().BeFalse();
        FormatNegotiation.Inactive.EventWrites(new BinaryEventSettings(true)).Writes.Should().BeFalse("with no node list to ask, nothing proves binary is safe");
        new FormatNegotiation { Options = new() { Mode = FormatNegotiationMode.Force } }.EventWrites(new BinaryEventSettings(true)).Writes.Should().BeTrue("Force needs no node list");
        new FormatNegotiation { Options = new() { Mode = FormatNegotiationMode.Force } }.EventWrites(new BinaryEventSettings(false)).Writes.Should().BeFalse();
    }

    [Fact]
    public void Decoder_reads_both_after_fallback()
    {
        var sut = Negotiator();
        var json = new CacheEventFormatter();
        var composite = new CompositeEventFormatter(json, new MessagePackEventFormatter(), sut);
        var original = new CacheEvent { Id = Guid.NewGuid().ToString(), Type = KnownEventTypes.CacheSet, Source = new Uri("urn:node-a"), Data = new CacheEventData("the-key", new Dictionary<string, object?> { [KnownFieldNames.VersionKey] = 3L }) };
        Settle(sut, View(Node("a", Both)));
        var binary = composite.Encode(original);
        binary.Span[0].Should().Be(CacheValueFormats.MessagePack);

        sut.Observe(View(Node("a", Both), Node("old", Json)));
        var fallback = composite.Encode(original);

        fallback.Span[0].Should().Be((byte)'{', "the writes fell back to JSON");
        composite.Decode(binary)!.Id.Should().Be(original.Id, "a backlog written before the fall back is still read");
        composite.Decode(fallback)!.Id.Should().Be(original.Id);
    }

    [Fact]
    public void Encoding_asks_the_decision_on_every_event_and_allocates_no_more_than_a_fixed_one()
    {
        var sut = Negotiator();
        Settle(sut, View(Node("a", Both)));
        sut.Observe(View(Node("a", Both), Node("old", Json)));
        var original = new CacheEvent { Id = Guid.NewGuid().ToString(), Type = KnownEventTypes.CacheSet, Source = new Uri("urn:node-a"), Data = new CacheEventData("the-key", null) };
        var negotiated = new CompositeEventFormatter(new CacheEventFormatter(), new MessagePackEventFormatter(), sut);
        var fixedJson = new CompositeEventFormatter(new CacheEventFormatter(), new MessagePackEventFormatter(), FixedBinaryEvents.Never);
        var plain = new CacheEventFormatter();

        var viaNodeList = Allocated(() => negotiated.Encode(original));
        var viaFixed = Allocated(() => fixedJson.Encode(original));
        var direct = Allocated(() => plain.Encode(original));

        viaNodeList.Should().Be(viaFixed, "reading the decision costs nothing");
        viaFixed.Should().Be(direct, "and neither does the wrapper");
    }

    private static long Allocated(Action action)
    {
        action();
        action();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            action();
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static (string Id, NodeAdvertisement Ad) Node(string id, string[] events, string[]? valueReads = null) =>
        (id, new("3.2.0", id, valueReads ?? Json, new Dictionary<string, IReadOnlyList<string>>(), EventReads: events));

    private static StoredAdvertisement Stored((string Id, NodeAdvertisement Ad) node) => new(node.Id, node.Ad, TimeSpan.Zero);

    private static NodeListView View(params (string Id, NodeAdvertisement Ad)[] nodes) =>
        new([.. nodes.Select(Stored)], [], Complete: true);

    private FormatNegotiator Negotiator(bool values = false) =>
        new(values ? [new DesiredWrite(_writer, Value, ["default"], [])] : [], _options, _clock, _telemetry, events: new DesiredEvents(Binary));

    private void Settle(FormatNegotiator sut, NodeListView view)
    {
        sut.Observe(view);
        _clock.Advance(_options.UpgradeHold);
        sut.Observe(view);
    }
}
