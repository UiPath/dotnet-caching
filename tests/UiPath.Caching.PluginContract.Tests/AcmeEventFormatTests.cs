using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using UiPath.Caching.Broadcast;
using UiPath.Caching.CloudEvents;
using UiPath.Caching.Config;
using UiPath.Caching.PluginContract.Tests.Acme;
using UiPath.Caching.PluginContract.Tests.Support;
using UiPath.Caching.Redis;

namespace UiPath.Caching.PluginContract.Tests;

public class AcmeEventFormatTests
{
    private static readonly DateTimeOffset Moment = new(2026, 11, 17, 8, 30, 0, TimeSpan.Zero);

    [Fact]
    public void A_node_that_forces_the_plugin_writes_it_and_the_body_starts_with_its_id()
    {
        using var node = Node(FormatNegotiationMode.Force, write: true);
        var formatter = node.GetRequiredService<IEventFormatterProxy<ICacheEvent>>();

        var body = formatter.Encode(Event("k"));

        body.Span[0].Should().Be(AcmeFormats.Event);
        formatter.Decode(body)!.Key.Should().Be("k");
    }

    [Fact]
    public void A_node_that_has_not_confirmed_the_format_writes_json_and_still_reads_the_plugin()
    {
        using var node = Node(FormatNegotiationMode.Auto, write: true);
        var formatter = node.GetRequiredService<IEventFormatterProxy<ICacheEvent>>();

        var json = formatter.Encode(Event("k"));
        var acme = new AcmeEventFormat().Encode(Event("peer"));

        json.Span[0].Should().Be((byte)'{', "no node list has said every node reads it");
        formatter.Decode(json)!.Key.Should().Be("k");
        formatter.Decode(acme)!.Key.Should().Be("peer");
    }

    [Fact]
    public void A_read_only_plugin_never_writes_but_decodes_by_its_first_byte()
    {
        using var node = Node(FormatNegotiationMode.Force, write: false);
        var formatter = node.GetRequiredService<IEventFormatterProxy<ICacheEvent>>();

        var written = formatter.Encode(Event("k"));
        var decoded = formatter.Decode(new AcmeEventFormat().Encode(Event("peer")));

        written.Span[0].Should().Be((byte)'{');
        decoded!.Key.Should().Be("peer");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public void The_json_decoder_stays_in_either_order_with_cloud_events(bool cloudEvents, bool cloudEventsFirst)
    {
        var format = new AcmeEventFormat();
        using (var node = Node(FormatNegotiationMode.Force, write: false, format, cloudEvents ? cloudEventsFirst : null))
        {
            var formatter = node.GetRequiredService<IEventFormatterProxy<ICacheEvent>>();

            var sent = cloudEvents
                ? node.GetRequiredService<ICacheEventFactory>().Create("cache", KnownEventTypes.CacheSet, new CacheEventData("jk"))
                : Event("jk");
            var json = formatter.Encode(sent);
            var decodedJson = formatter.Decode(json);
            var decodedAcme = formatter.Decode(new AcmeEventFormat().Encode(Event("peer")));

            json.Span[0].Should().Be((byte)'{');
            Encoding.UTF8.GetString(json.Span).Contains("specversion", StringComparison.Ordinal).Should().Be(cloudEvents, "the JSON formatter is the one AddCloudEvents registered, if it was called");
            decodedJson!.Key.Should().Be("jk");
            decodedAcme!.Key.Should().Be("peer");
            format.Disposals.Should().Be(0);
        }

        format.Disposals.Should().Be(1, "the container disposes the plugin's formatter once");
    }

    [Fact]
    public void A_writer_whose_body_does_not_start_with_its_declared_id_goes_out_as_json_with_one_error()
    {
        var logs = new RecordingLoggerProvider();
        using var node = Node(FormatNegotiationMode.Force, write: true, new AcmeEventFormat(0x12), cloudEventsFirst: null, logs);
        var formatter = node.GetRequiredService<IEventFormatterProxy<ICacheEvent>>();

        var first = formatter.Encode(Event("a"));
        var second = formatter.Encode(Event("b"));

        first.Span[0].Should().Be((byte)'{');
        second.Span[0].Should().Be((byte)'{');
        logs.Entries.Where(e => e.Level == LogLevel.Error).Should().ContainSingle().Which.Message.Should().Contain("0x11");
    }

    [Fact]
    public void A_header_byte_nobody_registered_is_not_decoded_by_the_plugin()
    {
        using var node = Node(FormatNegotiationMode.Force, write: true);
        var formatter = node.GetRequiredService<IEventFormatterProxy<ICacheEvent>>();
        var foreign = new AcmeEventFormat(0x12).Encode(Event("k"));

        var act = () => formatter.Decode(foreign);

        act.Should().Throw<JsonException>("0x12 falls to the JSON decoder, which cannot read it");
    }

    [Fact]
    public void Typed_properties_come_back_as_the_types_the_cache_reads()
    {
        var format = new AcmeEventFormat();
        var properties = new Dictionary<string, object?>
        {
            [CacheEventPropertyNames.Version] = 7L,
            [CacheEventPropertyNames.Expiration] = Moment,
            [CacheEventPropertyNames.PeerExpiration] = Moment.AddMinutes(5),
            [CacheEventPropertyNames.Metadata] = new Dictionary<string, string?> { ["m"] = "v", ["n"] = null },
            [CacheEventPropertyNames.PeerMetadata] = new Dictionary<string, string?> { ["p"] = "q" },
            [CacheEventPropertyNames.PeerFields] = new Dictionary<string, byte[]?> { ["f"] = [1, 2, 3], ["g"] = null },
            ["custom"] = 12,
        };

        var back = format.Decode(format.Encode(Event("k", properties)))!.Data!.Properties!;

        back[CacheEventPropertyNames.Version].Should().Be(7L);
        back[CacheEventPropertyNames.Expiration].Should().Be(Moment);
        back[CacheEventPropertyNames.PeerExpiration].Should().Be(Moment.AddMinutes(5));
        ((IEnumerable<KeyValuePair<string, string?>>)back[CacheEventPropertyNames.Metadata]!).Should().BeEquivalentTo(new Dictionary<string, string?> { ["m"] = "v", ["n"] = null });
        ((IEnumerable<KeyValuePair<string, string?>>)back[CacheEventPropertyNames.PeerMetadata]!).Should().BeEquivalentTo(new Dictionary<string, string?> { ["p"] = "q" });
        ((IEnumerable<KeyValuePair<string, byte[]?>>)back[CacheEventPropertyNames.PeerFields]!).Should().BeEquivalentTo(new Dictionary<string, byte[]?> { ["f"] = [1, 2, 3], ["g"] = null });
        back.Should().ContainKey("custom");
    }

    [Fact]
    public void An_event_survives_the_composite_with_its_envelope()
    {
        using var node = Node(FormatNegotiationMode.Force, write: true);
        var formatter = node.GetRequiredService<IEventFormatterProxy<ICacheEvent>>();
        var sent = Event("k", new Dictionary<string, object?> { [CacheEventPropertyNames.Version] = 3L });

        var received = formatter.Decode(formatter.Encode(sent))!;

        received.Id.Should().Be(sent.Id);
        received.Type.Should().Be(sent.Type);
        received.Source.Should().Be(sent.Source);
        received.Data!.Properties![CacheEventPropertyNames.Version].Should().Be(3L);
    }

    private static CacheEvent Event(string key, IDictionary<string, object?>? properties = null) => new()
    {
        Id = Guid.NewGuid().ToString(),
        Type = KnownEventTypes.CacheSet,
        Source = new Uri("urn:node-a"),
        Data = new CacheEventData(key, properties),
    };

    private static ServiceProvider Node(FormatNegotiationMode mode, bool write, AcmeEventFormat? format = null, bool? cloudEventsFirst = null, ILoggerProvider? logs = null) =>
        new ServiceCollection()
            .AddLogging(l =>
            {
                if (logs is not null)
                {
                    l.AddProvider(logs);
                }
            })
            .AddCaching(
                b =>
                {
                    b.AddRedisConnection(o => o.ConnectionString = "127.0.0.1:1,abortConnect=false");
                    b.AddRedis(o => o.FormatNegotiation.Mode = mode);
                    if (cloudEventsFirst == true)
                    {
                        b.AddCloudEvents();
                    }

                    b.AddEventFormat(AcmeFormats.Name, AcmeFormats.Event, _ => format ?? new AcmeEventFormat(), write);
                    if (cloudEventsFirst == false)
                    {
                        b.AddCloudEvents();
                    }
                },
                o =>
                {
                    o.AppShortName = "contract";
                    o.SourceUri = new Uri("urn:node-a");
                })
            .BuildServiceProvider();
}
