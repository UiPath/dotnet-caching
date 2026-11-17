using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using UiPath.Caching.Broadcast;
using UiPath.Caching.Config;
using UiPath.Caching.MessagePack;
using UiPath.Caching.Negotiation;
using UiPath.Caching.Tests.Fakes;

namespace UiPath.Caching.Tests.Negotiation;

public class EventNegotiationWiringTests
{
    private static readonly Uri Source = new("urn:node-a");

    [Fact]
    public void A_node_with_the_binary_decoder_advertises_both_event_formats_and_one_without_advertises_json()
    {
        using var plain = Provider(b => b.AddValueSerializer("fast", [0x10], _ => new FakeValueSerializer(0x10)));
        using var decoder = Provider(b =>
        {
            b.AddValueSerializer("fast", [0x10], _ => new FakeValueSerializer(0x10));
            b.AddMessagePackEvents(write: false);
        });

        plain.GetRequiredService<FormatNegotiation>().Advertisement!.EventReads.Should().Equal("json");
        decoder.GetRequiredService<FormatNegotiation>().Advertisement!.EventReads.Should().Equal("json", "02");
        NodeAdvertisement.TryParse(plain.GetRequiredService<FormatNegotiation>().Advertisement!.Encode(), out var parsed).Should().BeTrue();
        parsed!.EventReads.Should().Equal("json");
    }

    [Fact]
    public void A_message_pack_node_advertises_the_literal_tokens()
    {
        using var provider = Provider(b =>
        {
            b.AddMessagePackSerializer();
            b.AddMessagePackEvents(write: false);
        });

        var advertisement = provider.GetRequiredService<FormatNegotiation>().Advertisement!;

        advertisement.ValueReads.Should().Equal("json", "02", "03");
        advertisement.EventReads.Should().Equal("json", "02");
        advertisement.Encode().Should().Contain("vr=json,02,03").And.Contain("er=json,02");
    }

    [Fact]
    public void An_app_with_only_binary_events_advertises_decides_and_starts_the_service()
    {
        using var writes = Provider(b => b.AddMessagePackEvents(write: true));
        using var reads = Provider(b => b.AddMessagePackEvents(write: false));

        var deciding = writes.GetRequiredService<FormatNegotiation>();
        deciding.IsActive.Should().BeTrue();
        deciding.Advertisement!.ValueReads.Should().Equal("json");
        deciding.Advertisement.EventReads.Should().Equal("json", "02");
        deciding.Negotiator.Should().NotBeNull();
        writes.GetRequiredService<IEventWrites>().Writes.Should().BeFalse("Auto writes JSON until a node list says otherwise");
        writes.GetServices<IHostedService>().Should().Contain(s => s is FormatNegotiationService);

        var reader = reads.GetRequiredService<FormatNegotiation>();
        reader.IsActive.Should().BeTrue();
        reader.Negotiator.Should().BeNull("a node set to write JSON has nothing to decide, so it only advertises");
        reads.GetRequiredService<IEventWrites>().Writes.Should().BeFalse();
    }

    [Fact]
    public void Force_writes_binary_events_at_once()
    {
        using var forced = Provider(b => b.AddMessagePackEvents(write: true), force: true);

        forced.GetRequiredService<IEventWrites>().Writes.Should().BeTrue();
        forced.GetRequiredService<FormatNegotiation>().Negotiator.Should().BeNull();
        forced.GetRequiredService<FormatNegotiation>().IsActive.Should().BeTrue("it still tells the nodes that decide what it reads");
        forced.GetRequiredService<IEventFormatterProxy<ICacheEvent>>().Encode(Event()).Span[0].Should().Be(MessagePackFormats.Event);
    }

    [Fact]
    public void With_no_redis_connection_there_is_no_node_list_and_binary_events_wait_for_the_operator()
    {
        using var automatic = NoRedis(write: true, mode: FormatNegotiationMode.Auto);
        using var attested = NoRedis(write: true, mode: FormatNegotiationMode.Auto, assume: true);
        using var forced = NoRedis(write: true, mode: FormatNegotiationMode.Force);
        using var json = NoRedis(write: false, mode: FormatNegotiationMode.Auto, assume: true);

        automatic.GetRequiredService<IEventWrites>().Writes.Should().BeFalse("silent until proven");
        automatic.GetRequiredService<IEventFormatterProxy<ICacheEvent>>().Encode(Event()).Span[0].Should().Be((byte)'{');
        forced.GetRequiredService<IEventWrites>().Writes.Should().BeTrue();
        forced.GetRequiredService<IEventFormatterProxy<ICacheEvent>>().Encode(Event()).Span[0].Should().Be(MessagePackFormats.Event);
        json.GetRequiredService<IEventWrites>().Writes.Should().BeFalse();
        attested.GetRequiredService<IEventWrites>().Writes.Should().BeFalse("the operator's word counts once the service has started, and the hold has passed");
    }

    [Fact]
    public async Task With_no_redis_connection_the_operators_word_switches_after_the_hold()
    {
        using var attested = NoRedis(write: true, mode: FormatNegotiationMode.Auto, assume: true);
        var service = attested.GetServices<IHostedService>().OfType<FormatNegotiationService>().Single();

        await service.StartAsync(TestContext.Current.CancellationToken);

        attested.GetRequiredService<IEventWrites>().Writes.Should().BeTrue("the hold is zero here");
        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public void A_writer_with_id_11_advertises_it_and_waits_for_nodes_that_read_11_not_02()
    {
        using var provider = Provider(b => b.AddEventFormat("acme", 0x11, _ => new MessagePackEventFormatter(), write: true));
        var negotiation = provider.GetRequiredService<FormatNegotiation>();
        var sut = negotiation.Negotiator!;

        negotiation.Advertisement!.EventReads.Should().Equal("json", "11");
        negotiation.Advertisement.Encode().Should().EndWith("er=json,11");
        provider.GetRequiredService<EventFormatRegistry>().WriteToken.Should().Be("11");

        Settle(sut, View(Node("a", ["json", "02"])));
        sut.Writes.Should().BeFalse("the node reads 02, not the 11 this node would write");

        Settle(sut, View(Node("a", ["json", "11"])));
        sut.Writes.Should().BeTrue();

        sut.Observe(View(Node("a", ["json", "11"]), Node("b", ["json", "02"])));
        sut.Writes.Should().BeFalse();
    }

    [Fact]
    public void With_no_redis_connection_a_writer_with_id_11_still_waits_for_nodes_that_read_11()
    {
        using var provider = NoRedis(b => b.AddEventFormat("acme", 0x11, _ => new MessagePackEventFormatter(), write: true));
        var sut = provider.GetRequiredService<FormatNegotiation>().Negotiator!;

        Settle(sut, View(Node("a", ["json", "02"])));
        sut.Writes.Should().BeFalse();

        Settle(sut, View(Node("a", ["json", "11"])));
        sut.Writes.Should().BeTrue();
    }

    [Fact]
    public void An_app_with_nothing_registered_adds_nothing_and_encodes_as_it_always_did()
    {
        using var plain = Provider(_ => { });
        var services = new ServiceCollection();
        var builder = new CachingBuilder(services);
        builder.Complete();

        services.Should().NotContain(d => d.ServiceType == typeof(FormatNegotiation) || d.ServiceType == typeof(IEventWrites) || d.ServiceType == typeof(EventFormatRegistry));
        var formatter = plain.GetRequiredService<IEventFormatterProxy<ICacheEvent>>();
        formatter.Should().BeOfType<CacheEventFormatter>("no wrapper, so no decision to ask for");
        var direct = new CacheEventFormatter();
        var @event = Event();

        var viaContainer = Allocated(() => formatter.Encode(@event));
        var baseline = Allocated(() => direct.Encode(@event));

        viaContainer.Should().Be(baseline, "an app that registers nothing pays nothing for encoding an event");
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

    private static CacheEvent Event() => new()
    {
        Id = Guid.NewGuid().ToString(),
        Type = KnownEventTypes.CacheSet,
        Source = Source,
        Data = new CacheEventData("the-key", new Dictionary<string, object?> { [KnownFieldNames.VersionKey] = 3L }),
    };

    private static (string Id, NodeAdvertisement Ad) Node(string id, string[] events) =>
        (id, new("3.2.0", id, ["json"], new Dictionary<string, IReadOnlyList<string>>(), EventReads: events));

    private static NodeListView View(params (string Id, NodeAdvertisement Ad)[] nodes) =>
        new([.. nodes.Select(n => new StoredAdvertisement(n.Id, n.Ad, TimeSpan.Zero))], [], Complete: true);

    private static void Settle(FormatNegotiator sut, NodeListView view)
    {
        sut.Observe(view);
        sut.Observe(view);
    }

    private static ServiceProvider NoRedis(bool write, FormatNegotiationMode mode, bool assume = false) =>
        NoRedis(b => b.AddMessagePackEvents(write), mode, assume);

    private static ServiceProvider NoRedis(Action<ICachingBuilder> configure, FormatNegotiationMode mode = FormatNegotiationMode.Auto, bool assume = false) =>
        new ServiceCollection()
            .AddLogging()
            .Configure<RedisCacheOptions>(o =>
            {
                o.FormatNegotiation.Mode = mode;
                o.FormatNegotiation.AssumeAllNodesCanRead = assume;
                o.FormatNegotiation.UpgradeHold = TimeSpan.Zero;
            })
            .AddCaching(configure, o => o.AppShortName = "wiring")
            .BuildServiceProvider();

    private static ServiceProvider Provider(Action<ICachingBuilder> configure, bool force = false) =>
        new ServiceCollection()
            .AddLogging()
            .AddCaching(
                b =>
                {
                    b.AddRedisConnection(o => o.ConnectionString = "127.0.0.1:1");
                    b.AddRedis(o =>
                    {
                        o.FormatNegotiation.Mode = force ? FormatNegotiationMode.Force : FormatNegotiationMode.Auto;
                        o.FormatNegotiation.UpgradeHold = TimeSpan.Zero;
                    });
                    b.AddBroadcast();
                    b.AddInMemoryRedis();
                    configure(b);
                },
                o =>
                {
                    o.AppShortName = "wiring";
                    o.SourceUri = Source;
                })
            .BuildServiceProvider();
}
