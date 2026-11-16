using System.Buffers;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using CloudNative.CloudEvents;
using CloudNative.CloudEvents.SystemTextJson;
using MessagePack;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using UiPath.Caching.CloudEvents;
using UiPath.Caching.Config;
using UiPath.Caching.MessagePack;

namespace UiPath.Caching.Tests.MessagePack;

public class MessagePackEventFormatterTests
{
    private static readonly Uri Source = new("urn:node-a");
    private static readonly DateTimeOffset Expiry = new(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private readonly CacheEventFormatter _json = new();
    private readonly MessagePackEventFormatter _binary = new();

    private delegate void WriteBody(ref MessagePackWriter writer);

    public static TheoryData<string, byte[], bool> SourceBodies()
    {
        static byte[] Body(WriteBody write) => Raw(write).ToArray();
        return new()
        {
            { "the source", Body((ref MessagePackWriter w) => { w.WriteMapHeader(1); w.Write(2); w.Write("urn:node-a"); }), true },
            { "after a nested value it skips", Body((ref MessagePackWriter w) => { w.WriteMapHeader(2); w.Write(99); w.WriteArrayHeader(1); w.WriteMapHeader(1); w.Write(1); w.Write("x"); w.Write(2); w.Write("urn:node-a"); }), true },
            { "another source", Body((ref MessagePackWriter w) => { w.WriteMapHeader(1); w.Write(2); w.Write("urn:node-b"); }), false },
            { "a prefix of the source", Body((ref MessagePackWriter w) => { w.WriteMapHeader(1); w.Write(2); w.Write("urn:node-"); }), false },
            { "no source", Body((ref MessagePackWriter w) => { w.WriteMapHeader(1); w.Write(3); w.Write("k"); }), false },
            { "a nil source", Body((ref MessagePackWriter w) => { w.WriteMapHeader(1); w.Write(2); w.WriteNil(); }), false },
            { "a source that is not text", Body((ref MessagePackWriter w) => { w.WriteMapHeader(1); w.Write(2); w.Write(7); }), false },
            { "a second source, which would win the decode", Body((ref MessagePackWriter w) => { w.WriteMapHeader(2); w.Write(2); w.Write("urn:node-a"); w.Write(2); w.Write("urn:node-b"); }), false },
            { "a key that is not a number", Body((ref MessagePackWriter w) => { w.WriteMapHeader(1); w.Write("2"); w.Write("urn:node-a"); }), false },
            { "a body that is not a map", Body((ref MessagePackWriter w) => w.WriteArrayHeader(0)), false },
            { "trailing bytes", Body((ref MessagePackWriter w) => { w.WriteMapHeader(1); w.Write(2); w.Write("urn:node-a"); w.WriteNil(); }), false },
            { "a truncated map", Body((ref MessagePackWriter w) => { w.WriteMapHeader(2); w.Write(2); w.Write("urn:node-a"); }), false },
            { "a count beyond an int", Body((ref MessagePackWriter w) => w.WriteRaw(new byte[] { 0xDF, 0xFF, 0xFF, 0xFF, 0xFF })), false },
            { "a count the body cannot hold", Body((ref MessagePackWriter w) => w.WriteMapHeader(100_000_000)), false },
        };
    }

    [Fact]
    public void Event_round_trips_with_all_properties()
    {
        var fields = new Dictionary<string, string?> { ["a"] = Convert.ToBase64String([1, 2, 3]), ["b"] = null, ["c"] = Convert.ToBase64String([]) };
        var original = Event(KnownEventTypes.CacheSet, new Dictionary<string, object?>
        {
            [KnownFieldNames.VersionKey] = 42L,
            [KnownFieldNames.PeerFieldsKey] = fields,
            [KnownFieldNames.PeerExpirationKey] = Expiry,
            [KnownFieldNames.PeerMetadataKey] = new Dictionary<string, string?> { ["m"] = "v", ["n"] = null },
            [KnownFieldNames.ExpirationKey] = Expiry.AddMinutes(1),
            [KnownFieldNames.MetadataKey] = new Dictionary<string, string?> { ["x"] = "y" },
            ["custom"] = new { Name = "n", Count = 3 },
            ["nothing"] = null,
        });

        var decoded = (CacheEvent)_binary.Decode(_binary.Encode(original))!;

        decoded.Id.Should().Be(original.Id);
        decoded.Type.Should().Be(KnownEventTypes.CacheSet);
        decoded.Source.Should().Be(Source);
        decoded.Key.Should().Be("the-key");
        var p = decoded.Data!.Properties!;
        p[KnownFieldNames.VersionKey].Should().Be(42L);
        p[KnownFieldNames.PeerExpirationKey].Should().Be(Expiry);
        p[KnownFieldNames.ExpirationKey].Should().Be(Expiry.AddMinutes(1));
        p[KnownFieldNames.PeerMetadataKey].Should().BeEquivalentTo(new Dictionary<string, string?> { ["m"] = "v", ["n"] = null });
        p[KnownFieldNames.MetadataKey].Should().BeEquivalentTo(new Dictionary<string, string?> { ["x"] = "y" });
        var peerFields = p[KnownFieldNames.PeerFieldsKey].Should().BeAssignableTo<IReadOnlyDictionary<string, byte[]?>>().Subject;
        peerFields["a"].Should().Equal(1, 2, 3);
        peerFields["b"].Should().BeNull();
        peerFields["c"].Should().BeEmpty();
        ((JsonElement)p["custom"]!).GetProperty("Count").GetInt32().Should().Be(3);
        p.Should().ContainKey("nothing").WhoseValue.Should().BeNull();
        p.Should().HaveCount(8);
    }

    [Theory]
    [InlineData("not-a-guid", "CacheSet")]
    [InlineData("0B7D6F44-4AE1-4B38-A6E3-0F1F2A0F2C11", "CacheSet")]
    [InlineData("0b7d6f44-4ae1-4b38-a6e3-0f1f2a0f2c11", "SomethingElse")]
    [InlineData("0b7d6f44-4ae1-4b38-a6e3-0f1f2a0f2c11", "cacheset")]
    public void An_id_or_type_the_compact_forms_do_not_cover_still_round_trips(string id, string type)
    {
        var original = Event(type, null);
        original.Id = id;

        var decoded = _binary.Decode(_binary.Encode(original))!;

        decoded.Id.Should().Be(id);
        decoded.Type.Should().Be(type);
        ((CacheEvent)decoded).Data!.Properties.Should().BeNull();
    }

    [Fact]
    public void Peer_fields_given_as_bytes_round_trip_without_a_base64_form()
    {
        var fields = new Dictionary<string, byte[]?> { ["a"] = [1, 2, 3], ["b"] = null, ["c"] = [], ["d"] = [255, 0, 254] };
        var original = Event(KnownEventTypes.CacheSet, new Dictionary<string, object?>
        {
            [KnownFieldNames.VersionKey] = 7L,
            [KnownFieldNames.PeerFieldsKey] = fields,
        });

        var decoded = ((CacheEvent)_binary.Decode(_binary.Encode(original))!).Data!.Properties!;

        var peerFields = decoded[KnownFieldNames.PeerFieldsKey].Should().BeAssignableTo<IReadOnlyDictionary<string, byte[]?>>().Subject;
        peerFields.Should().HaveCount(4);
        peerFields["a"].Should().Equal(1, 2, 3);
        peerFields["b"].Should().BeNull();
        peerFields["c"].Should().BeEmpty();
        peerFields["d"].Should().Equal(255, 0, 254);
        decoded.Should().HaveCount(2, "the fields did not travel as another property too");
    }

    [Fact]
    public void Peer_fields_given_as_bytes_and_as_base64_encode_to_the_same_binary_event()
    {
        var id = Guid.NewGuid().ToString();
        var asBytes = Event(KnownEventTypes.CacheSet, new Dictionary<string, object?> { [KnownFieldNames.PeerFieldsKey] = new Dictionary<string, byte[]?> { ["a"] = [1, 2, 3], ["b"] = null } });
        var asText = Event(KnownEventTypes.CacheSet, new Dictionary<string, object?> { [KnownFieldNames.PeerFieldsKey] = new Dictionary<string, string?> { ["a"] = Convert.ToBase64String([1, 2, 3]), ["b"] = null } });
        asBytes.Id = asText.Id = id;

        _binary.Encode(asBytes).ToArray().Should().Equal(_binary.Encode(asText).ToArray());
    }

    [Fact]
    public void The_json_of_peer_fields_given_as_bytes_is_the_json_given_as_base64()
    {
        var id = Guid.NewGuid().ToString();
        var asBytes = Event(KnownEventTypes.CacheSet, new Dictionary<string, object?> { [KnownFieldNames.VersionKey] = 3L, [KnownFieldNames.PeerFieldsKey] = PeerBytes() });
        var asText = Event(KnownEventTypes.CacheSet, new Dictionary<string, object?> { [KnownFieldNames.VersionKey] = 3L, [KnownFieldNames.PeerFieldsKey] = PeerText() });
        asBytes.Id = asText.Id = id;

        var json = _json.Encode(asBytes).ToArray();

        System.Text.Encoding.UTF8.GetString(json).Should().Be(System.Text.Encoding.UTF8.GetString(_json.Encode(asText).Span));
        System.Text.Encoding.UTF8.GetString(json).Should().Contain("\"_peerFields_\":{\"a\":\"AQID\",\"b\":null,\"c\":\"\",\"d\":\"/wD\\u002B\"}");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_cloud_events_json_of_peer_fields_given_as_bytes_is_the_json_given_as_base64(bool fastCodec)
    {
        var codec = fastCodec ? CacheEventFormatterProxy.CreateDefault() : new CacheEventFormatterProxy(new JsonEventFormatter<CacheEventData>());
        var id = Guid.NewGuid().ToString();
        ICacheEvent Wrap(object peerFields) => new CacheCloudEventWrapper(new CloudEvent
        {
            Id = id,
            Type = KnownEventTypes.CacheSet,
            Source = Source,
            DataContentType = "application/json",
            Data = new CacheEventData("the-key", new Dictionary<string, object?> { [KnownFieldNames.VersionKey] = 3L, [KnownFieldNames.PeerFieldsKey] = peerFields }),
        });

        var json = codec.Encode(Wrap(PeerBytes())).ToArray();

        System.Text.Encoding.UTF8.GetString(json).Should().Be(System.Text.Encoding.UTF8.GetString(codec.Encode(Wrap(PeerText())).Span));
        System.Text.Encoding.UTF8.GetString(json).Should().Contain("\"_peerFields_\":{\"a\":\"AQID\",\"b\":null,\"c\":\"\",\"d\":\"/wD\\u002B\"}");
    }

    [Fact]
    public void A_peer_field_event_costs_less_to_publish_as_binary_than_as_json_and_than_it_did_through_base64()
    {
        HashEntry[] entries = [.. Enumerable.Range(0, 5).Select(i => new HashEntry($"field{i}", Enumerable.Range(0, 64).Select(b => (byte)(b + i)).ToArray()))];
        var limits = new PeerFieldLimits(16, 4096);
        var expiry = new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);

        CacheEvent Event(object fields) => new()
        {
            Id = "0b7d6f44-4ae1-4b38-a6e3-0f1f2a0f2c11",
            Type = KnownEventTypes.CacheSet,
            Source = Source,
            Data = new CacheEventData("the-key", new Dictionary<string, object?>
            {
                [KnownFieldNames.VersionKey] = 7L,
                [KnownFieldNames.PeerFieldsKey] = fields,
                [KnownFieldNames.PeerExpirationKey] = expiry,
            }),
        };

        // The capture as it was: each value as base64 text.
        object CaptureAsText() => entries.ToDictionary(e => (string)e.Name!, e => (string?)Convert.ToBase64String((byte[])e.Value!));
        object CaptureAsBytes()
        {
            var report = new WriteReport(limits);
            report.Capture(entries, HashCacheSetOption.HashReplace);
            return report.Fields!;
        }

        var jsonBefore = Allocated(() => _json.Encode(Event(CaptureAsText())));
        var jsonNow = Allocated(() => _json.Encode(Event(CaptureAsBytes())));
        var binaryBefore = Allocated(() => _binary.Encode(Event(CaptureAsText())));
        var binaryNow = Allocated(() => _binary.Encode(Event(CaptureAsBytes())));

        TestContext.Current.SendDiagnosticMessage($"Allocated bytes to capture and encode a 5-field event: json before {jsonBefore}, json now {jsonNow}, binary before {binaryBefore}, binary now {binaryNow}");
        binaryNow.Should().BeLessThan(jsonNow, "the binary event is the cheaper one to publish");
        binaryNow.Should().BeLessThan(binaryBefore, "the fields are not base64 encoded and decoded again");
        jsonNow.Should().BeLessThanOrEqualTo(jsonBefore, "the JSON event costs no more than it did");
    }

    [Theory]
    [InlineData(KnownEventTypes.CacheSet, "00")]
    [InlineData(KnownEventTypes.CacheRefreshed, "01")]
    [InlineData(KnownEventTypes.CacheRemoved, "02")]
    [InlineData(KnownEventTypes.TopicInvalidated, "03")]
    public void The_wire_value_of_each_known_type_is_pinned(string type, string code)
    {
        var @event = new CacheEvent { Id = "00000000-0000-0000-0000-000000000001", Type = type, Source = new Uri("urn:a"), Data = new CacheEventData("k") };
        var expected = "02" + "84" + "00C410" + new string('0', 30) + "01" + "01" + code + "02A5" + "75726E3A61" + "03A16B";

        Convert.ToHexString(_binary.Encode(@event).Span).Should().Be(expected);
        _binary.Decode(Convert.FromHexString(expected))!.Type.Should().Be(type);
    }

    [Fact]
    public void The_wire_keys_of_the_event_and_its_properties_are_pinned()
    {
        var properties = new Dictionary<string, object?>
        {
            [KnownFieldNames.VersionKey] = 5L,
            [KnownFieldNames.ExpirationKey] = new DateTimeOffset(1, TimeSpan.Zero),
            [KnownFieldNames.PeerExpirationKey] = new DateTimeOffset(2, TimeSpan.Zero),
            [KnownFieldNames.MetadataKey] = new Dictionary<string, string?> { ["m"] = "v" },
            [KnownFieldNames.PeerMetadataKey] = new Dictionary<string, string?> { ["n"] = null },
            [KnownFieldNames.PeerFieldsKey] = new Dictionary<string, byte[]?> { ["a"] = [1, 2] },
            ["o"] = 7,
        };
        var @event = new CacheEvent { Id = "00000000-0000-0000-0000-000000000001", Type = KnownEventTypes.CacheSet, Source = new Uri("urn:a"), Data = new CacheEventData("k", properties) };
        var expected = "02" + "86"
            + "00C410" + new string('0', 30) + "01"
            + "0100"
            + "02A575726E3A61"
            + "03A16B"
            + "04" + "86" + "0005" + "0101" + "0202" + "0381A16DA176" + "0481A16EC0" + "0581A161C4020102"
            + "05" + "81A16FC40137";

        Convert.ToHexString(_binary.Encode(@event).Span).Should().Be(expected);
        var decoded = ((CacheEvent)_binary.Decode(Convert.FromHexString(expected))!).Data!.Properties!;
        decoded[KnownFieldNames.VersionKey].Should().Be(5L);
        decoded[KnownFieldNames.ExpirationKey].Should().Be(new DateTimeOffset(1, TimeSpan.Zero));
        decoded[KnownFieldNames.PeerExpirationKey].Should().Be(new DateTimeOffset(2, TimeSpan.Zero));
        decoded[KnownFieldNames.MetadataKey].Should().BeEquivalentTo(new Dictionary<string, string?> { ["m"] = "v" });
        decoded[KnownFieldNames.PeerMetadataKey].Should().BeEquivalentTo(new Dictionary<string, string?> { ["n"] = null });
        ((IReadOnlyDictionary<string, byte[]?>)decoded[KnownFieldNames.PeerFieldsKey]!)["a"].Should().Equal(1, 2);
        ((JsonElement)decoded["o"]!).GetInt32().Should().Be(7);
    }

    [Fact]
    public void An_event_with_more_properties_than_fit_the_stack_buffer_round_trips()
    {
        var properties = Enumerable.Range(0, 40).ToDictionary(i => $"p{i}", i => (object?)i);
        properties[KnownFieldNames.VersionKey] = 9L;
        properties[KnownFieldNames.PeerFieldsKey] = PeerBytes();

        var decoded = ((CacheEvent)_binary.Decode(_binary.Encode(Event(KnownEventTypes.CacheSet, properties)))!).Data!.Properties!;

        decoded.Should().HaveCount(42);
        decoded[KnownFieldNames.VersionKey].Should().Be(9L);
        ((JsonElement)decoded["p39"]!).GetInt32().Should().Be(39);
        ((IReadOnlyDictionary<string, byte[]?>)decoded[KnownFieldNames.PeerFieldsKey]!)["d"].Should().Equal(255, 0, 254);
    }

    [Fact]
    public void An_event_type_this_version_does_not_know_by_number_is_refused()
    {
        var body = Raw((ref MessagePackWriter w) =>
        {
            w.WriteMapHeader(1);
            w.Write(1);
            w.Write(99);
        });

        var act = () => _binary.Decode(body);

        act.Should().Throw<MessagePackSerializationException>().WithMessage("*99*");
    }

    [Theory]
    [InlineData(15)]
    [InlineData(17)]
    [InlineData(0)]
    public void A_binary_id_that_is_not_sixteen_bytes_is_refused(int length)
    {
        var body = Raw((ref MessagePackWriter w) =>
        {
            w.WriteMapHeader(1);
            w.Write(0);
            w.Write(new byte[length]);
        });

        var act = () => _binary.Decode(body);

        act.Should().Throw<MessagePackSerializationException>();
    }

    [Theory]
    [InlineData(-1L, true)]
    [InlineData(long.MinValue, true)]
    [InlineData(3_155_378_975_999_999_999L + 1, true)]
    [InlineData(long.MaxValue, true)]
    [InlineData(-1L, false)]
    [InlineData(3_155_378_975_999_999_999L + 1, false)]
    [InlineData(3_155_378_975_999_999_999L, true)]
    [InlineData(0L, false)]
    public void An_expiry_outside_the_range_of_a_date_is_refused_and_the_ends_of_the_range_are_not(long ticks, bool peer)
    {
        var body = Raw((ref MessagePackWriter w) =>
        {
            w.WriteMapHeader(1);
            w.Write(4);
            w.WriteMapHeader(1);
            w.Write(peer ? 2 : 1);
            w.Write(ticks);
        });
        var inRange = ticks is >= 0 and <= 3_155_378_975_999_999_999L;

        var act = () => _binary.Decode(body);

        if (inRange)
        {
            act.Should().NotThrow();
        }
        else
        {
            act.Should().Throw<MessagePackSerializationException>();
        }
    }

    [Fact]
    public void A_null_metadata_map_reads_as_null_and_a_null_one_is_written_back_as_null()
    {
        var body = Raw((ref MessagePackWriter w) =>
        {
            w.WriteMapHeader(1);
            w.Write(4);
            w.WriteMapHeader(2);
            w.Write(3);
            w.WriteNil();
            w.Write(4);
            w.WriteNil();
        });

        var read = ((CacheEvent)_binary.Decode(body)!).Data!.Properties!;
        var written = ((CacheEvent)_binary.Decode(_binary.Encode(Event(KnownEventTypes.CacheRefreshed, new Dictionary<string, object?>
        {
            [KnownFieldNames.MetadataKey] = null,
            [KnownFieldNames.PeerMetadataKey] = null,
        })))!).Data!.Properties!;

        read.Should().ContainKey(KnownFieldNames.MetadataKey).WhoseValue.Should().BeNull();
        read.Should().ContainKey(KnownFieldNames.PeerMetadataKey).WhoseValue.Should().BeNull();
        written.Should().ContainKey(KnownFieldNames.MetadataKey).WhoseValue.Should().BeNull();
        written.Should().ContainKey(KnownFieldNames.PeerMetadataKey).WhoseValue.Should().BeNull();
    }

    [Fact]
    public void A_known_event_is_small_and_starts_with_the_format_id()
    {
        var encoded = _binary.Encode(Event(KnownEventTypes.CacheSet, null));

        encoded.Span[0].Should().Be(CacheValueFormats.MessagePack);
        encoded.Length.Should().BeLessThan(_json.Encode(Event(KnownEventTypes.CacheSet, null)).Length / 2);
    }

    [Fact]
    public void Peer_fields_are_raw_bytes()
    {
        var raw = Enumerable.Range(0, 3000).Select(i => (byte)(i * 7)).ToArray();
        var properties = new Dictionary<string, object?>
        {
            [KnownFieldNames.VersionKey] = 1L,
            [KnownFieldNames.PeerFieldsKey] = new Dictionary<string, string?> { ["f"] = Convert.ToBase64String(raw) },
        };
        var original = Event(KnownEventTypes.CacheSet, properties);

        var binary = _binary.Encode(original);
        var json = _json.Encode(original);

        var base64Overhead = Convert.ToBase64String(raw).Length - raw.Length;
        (json.Length - binary.Length).Should().BeGreaterThanOrEqualTo(base64Overhead);
        binary.Span.IndexOf(raw).Should().BeGreaterThan(0, "the bytes are in the event as they are");
    }

    [Fact]
    public void Composite_reads_json_and_binary()
    {
        var composite = new CompositeEventFormatter(_json, _binary, writeBinary: true);
        var original = Event(KnownEventTypes.CacheRemoved, null);

        composite.Decode(_json.Encode(original))!.Id.Should().Be(original.Id);
        composite.Decode(_binary.Encode(original))!.Id.Should().Be(original.Id);
        composite.Decode(composite.Encode(original))!.Key.Should().Be("the-key");
    }

    [Fact]
    public void Read_only_mode_writes_json_and_write_mode_writes_binary()
    {
        var original = Event(KnownEventTypes.CacheSet, null);

        new CompositeEventFormatter(_json, _binary, writeBinary: false).Encode(original).Span[0].Should().Be((byte)'{');
        new CompositeEventFormatter(_json, _binary, writeBinary: true).Encode(original).Span[0].Should().Be(CacheValueFormats.MessagePack);
    }

    [Fact]
    public void The_json_bytes_are_the_ones_the_default_formatter_writes()
    {
        var original = Event(KnownEventTypes.CacheSet, new Dictionary<string, object?>
        {
            [KnownFieldNames.VersionKey] = 3L,
            [KnownFieldNames.PeerFieldsKey] = new Dictionary<string, string?> { ["a"] = Convert.ToBase64String([9]) },
        });
        var expected = _json.Encode(original).ToArray();

        var readOnly = Resolve(b => b.AddMessagePackEvents(write: false));
        var none = Resolve(_ => { });

        none.Should().BeOfType<CacheEventFormatter>("nothing is registered, so nothing changes");
        none.Encode(original).ToArray().Should().Equal(expected);
        readOnly.Should().BeOfType<CompositeEventFormatter>();
        readOnly.Encode(original).ToArray().Should().Equal(expected);
        Resolve(b => b.AddMessagePackEvents(write: true)).Encode(original).Span[0].Should().Be(CacheValueFormats.MessagePack);
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, false, false)]
    public void The_cloud_events_formatter_is_the_json_side_in_either_registration_order(bool cloudEventsFirst, bool beforeInMemoryRedis, bool write)
    {
        using var provider = ResolveProvider(
            b =>
            {
                if (cloudEventsFirst)
                {
                    b.AddCloudEvents();
                    b.AddMessagePackEvents(write);
                }
                else
                {
                    b.AddMessagePackEvents(write);
                    b.AddCloudEvents();
                }
            },
            beforeInMemoryRedis);
        var formatter = provider.GetRequiredService<IEventFormatterProxy<ICacheEvent>>();
        var @event = provider.GetRequiredService<ICacheEventFactory>().Create("cache", KnownEventTypes.CacheSet, new CacheEventData("the-key", new Dictionary<string, object?> { [KnownFieldNames.VersionKey] = 2L, [KnownFieldNames.PeerFieldsKey] = PeerBytes() }));
        var cloudEvents = CacheEventFormatterProxy.CreateDefault();

        var encoded = formatter.Encode(@event);

        formatter.Should().BeOfType<CompositeEventFormatter>();
        @event.Should().BeOfType<CacheCloudEventWrapper>();
        encoded.IsEmpty.Should().BeFalse("an event that cannot be encoded is never published");
        if (write)
        {
            encoded.Span[0].Should().Be(CacheValueFormats.MessagePack);
        }
        else
        {
            System.Text.Encoding.UTF8.GetString(encoded.Span).Should().Contain("\"source\":", "the JSON wire is still the CloudEvents one");
            cloudEvents.Decode(encoded)!.IsValid().Should().BeTrue("a peer that only reads CloudEvents JSON can decode it");
            encoded.ToArray().Should().Equal(cloudEvents.Encode(@event).ToArray());
        }

        var decoded = formatter.Decode(encoded)!;
        decoded.Key.Should().Be("the-key");
        decoded.IsValid().Should().BeTrue();
        decoded.SameSource(Source).Should().BeTrue();
        formatter.Decode(cloudEvents.Encode(@event))!.IsValid().Should().BeTrue("a CloudEvents JSON body from a peer that has not switched still reads");
    }

    [Fact]
    public void A_second_call_replaces_the_setting_and_wraps_the_formatter_once()
    {
        var original = Event(KnownEventTypes.CacheSet, null);

        var binary = Resolve(b =>
        {
            b.AddMessagePackEvents(write: false);
            b.AddMessagePackEvents(write: true);
        });
        var json = Resolve(b =>
        {
            b.AddMessagePackEvents(write: true);
            b.AddMessagePackEvents(write: false);
        });

        binary.Encode(original).Span[0].Should().Be(CacheValueFormats.MessagePack);
        json.Encode(original).Span[0].Should().Be((byte)'{');
        json.Decode(json.Encode(original))!.Key.Should().Be("the-key");
    }

    [Fact]
    public void Own_event_filtering_still_works()
    {
        var composite = new CompositeEventFormatter(_json, _binary, writeBinary: true);
        var own = composite.Decode(composite.Encode(Event(KnownEventTypes.CacheSet, null)))!;
        var other = Event(KnownEventTypes.CacheSet, null);
        other.Source = new Uri("urn:node-b");
        var foreign = composite.Decode(composite.Encode(other))!;

        own.SameSource(Source).Should().BeTrue();
        foreign.SameSource(Source).Should().BeFalse();
        var matcher = composite.CreateSourceMatcher(Source);
        matcher.Should().NotBeNull();
        matcher!.IsFromSource(_json.Encode(Event(KnownEventTypes.CacheSet, null))).Should().BeTrue();
        matcher.IsFromSource(composite.Encode(Event(KnownEventTypes.CacheSet, null))).Should().BeTrue("a binary body's source is read without a decode");
        matcher.IsFromSource(composite.Encode(other)).Should().BeFalse();
        matcher.IsFromSource(_json.Encode(other)).Should().BeFalse();
    }

    [Fact]
    public void Matching_the_source_of_a_binary_body_throws_nothing_even_first_chance()
    {
        var matcher = new CompositeEventFormatter(_json, _binary, writeBinary: true).CreateSourceMatcher(Source)!;
        var own = _binary.Encode(Event(KnownEventTypes.CacheSet, new Dictionary<string, object?> { [KnownFieldNames.VersionKey] = 1L }));
        var other = Event(KnownEventTypes.CacheSet, null);
        other.Source = new Uri("urn:node-b");
        var foreign = _binary.Encode(other);
        var thread = Environment.CurrentManagedThreadId;
        var thrown = 0;
        void Count(object? sender, FirstChanceExceptionEventArgs e)
        {
            if (Environment.CurrentManagedThreadId == thread)
            {
                thrown++;
            }
        }

        AppDomain.CurrentDomain.FirstChanceException += Count;
        try
        {
            matcher.IsFromSource(own).Should().BeTrue();
            matcher.IsFromSource(foreign).Should().BeFalse();
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= Count;
        }

        thrown.Should().Be(0);
    }

    [Fact]
    public void Matching_the_source_of_a_binary_body_allocates_nothing()
    {
        var matcher = _binary.CreateSourceMatcher(Source)!;
        var body = _binary.Encode(Event(KnownEventTypes.CacheSet, new Dictionary<string, object?> { [KnownFieldNames.VersionKey] = 1L }));
        matcher.IsFromSource(body).Should().BeTrue();

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            matcher.IsFromSource(body);
        }

        (GC.GetAllocatedBytesForCurrentThread() - before).Should().Be(0);
    }

    [Theory]
    [MemberData(nameof(SourceBodies))]
    public void The_binary_matcher_names_the_source_only_for_a_body_that_has_it_once_and_is_whole(string name, byte[] body, bool own)
    {
        _binary.CreateSourceMatcher(Source)!.IsFromSource(body).Should().Be(own, name);
    }

    [Fact]
    public void The_binary_matcher_does_not_claim_a_json_empty_or_unknown_body()
    {
        var matcher = _binary.CreateSourceMatcher(Source)!;

        matcher.IsFromSource(_json.Encode(Event(KnownEventTypes.CacheSet, null))).Should().BeFalse();
        matcher.IsFromSource(ReadOnlyMemory<byte>.Empty).Should().BeFalse();
        matcher.IsFromSource(new byte[] { 0x03, 0x80 }).Should().BeFalse();
    }

    [Fact]
    public void The_composite_picks_the_matcher_by_the_first_byte_and_has_none_when_neither_side_has_one()
    {
        var jsonBody = _json.Encode(Event(KnownEventTypes.CacheSet, null));
        var binaryBody = _binary.Encode(Event(KnownEventTypes.CacheSet, null));
        var neither = new CompositeEventFormatter(Substitute.For<IEventFormatterProxy<ICacheEvent>>(), Substitute.For<IEventFormatterProxy<ICacheEvent>>(), writeBinary: true);
        var binaryOnly = new CompositeEventFormatter(Substitute.For<IEventFormatterProxy<ICacheEvent>>(), _binary, writeBinary: true).CreateSourceMatcher(Source)!;

        neither.CreateSourceMatcher(Source).Should().BeNull();
        binaryOnly.IsFromSource(binaryBody).Should().BeTrue();
        binaryOnly.IsFromSource(jsonBody).Should().BeFalse("with no JSON matcher that body is left to a decode");
    }

    [Fact]
    public void A_truncated_body_does_not_decode()
    {
        var full = _binary.Encode(Event(KnownEventTypes.CacheSet, new Dictionary<string, object?> { [KnownFieldNames.VersionKey] = 1L })).ToArray();

        for (var length = 1; length < full.Length; length++)
        {
            var cut = full.AsMemory(0, length);
            var act = () => _binary.Decode(cut);
            act.Should().Throw<Exception>($"{length} of {full.Length} bytes");
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void A_map_count_the_body_can_hold_does_not_size_the_allocation(int shape)
    {
        const int Entries = 500_000;
        var body = Raw((ref MessagePackWriter w) =>
        {
            w.WriteMapHeader(1);
            w.Write(shape == 0 ? 5 : 4);
            if (shape >= 2)
            {
                w.WriteMapHeader(1);
                w.Write(shape == 2 ? 3 : 5);
            }

            w.WriteMapHeader(Entries);
            for (var i = 0; i < Entries; i++)
            {
                if (shape == 1)
                {
                    w.Write(99);
                    w.WriteNil();
                }
                else
                {
                    w.Write(string.Empty);
                    w.WriteNil();
                }
            }
        });

        var allocated = Allocated(() => _binary.Decode(body));

        allocated.Should().BeLessThan(body.Length);
    }

    [Fact]
    public void A_key_this_version_does_not_know_is_skipped()
    {
        var body = Raw((ref MessagePackWriter w) =>
        {
            w.WriteMapHeader(3);
            w.Write(2);
            w.Write("urn:node-a");
            w.Write(3);
            w.Write("k");
            w.Write(99);
            w.WriteArrayHeader(2);
            w.Write(1);
            w.Write("x");
        });

        var decoded = _binary.Decode(body)!;

        decoded.Key.Should().Be("k");
        decoded.SameSource(Source).Should().BeTrue();
    }

    [Fact]
    public void Nesting_beyond_the_untrusted_data_limit_is_refused()
    {
        var body = Raw((ref MessagePackWriter w) =>
        {
            w.WriteMapHeader(1);
            w.Write(99);
            for (var i = 0; i < 2000; i++)
            {
                w.WriteArrayHeader(1);
            }

            w.WriteNil();
        });

        var act = () => _binary.Decode(body);

        act.Should().Throw<InsufficientExecutionStackException>();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_count_the_body_cannot_hold_is_refused_and_nothing_is_allocated_for_it(bool inProperties)
    {
        var body = Raw((ref MessagePackWriter w) =>
        {
            w.WriteMapHeader(1);
            if (inProperties)
            {
                w.Write(4);
                w.WriteMapHeader(1);
                w.Write(5);
            }
            else
            {
                w.Write(5);
            }

            w.WriteMapHeader(100_000_000);
        });

        var act = () => _binary.Decode(body);
        act.Should().Throw<EndOfStreamException>();

        var before = GC.GetAllocatedBytesForCurrentThread();
        try
        {
            _binary.Decode(body);
        }
        catch (EndOfStreamException)
        {
            // Refused, as above; what is measured is what it allocated before refusing.
        }

        (GC.GetAllocatedBytesForCurrentThread() - before).Should().BeLessThan(64 * 1024, "a hundred million entries would be gigabytes");
    }

    [Fact]
    public void Maps_read_off_the_wire_use_collision_resistant_comparers()
    {
        var original = Event(KnownEventTypes.CacheSet, new Dictionary<string, object?>
        {
            [KnownFieldNames.MetadataKey] = new Dictionary<string, string?> { ["x"] = "y" },
            [KnownFieldNames.PeerFieldsKey] = new Dictionary<string, string?> { ["a"] = Convert.ToBase64String([1]) },
            [KnownFieldNames.VersionKey] = 1L,
        });

        var properties = ((CacheEvent)_binary.Decode(_binary.Encode(original))!).Data!.Properties!;

        ComparerOf(properties).Should().NotBeSameAs(EqualityComparer<string>.Default, "the properties");
        ComparerOf(properties[KnownFieldNames.MetadataKey]!).Should().NotBeSameAs(EqualityComparer<string>.Default, "the metadata");
        ComparerOf(properties[KnownFieldNames.PeerFieldsKey]!).Should().NotBeSameAs(EqualityComparer<string>.Default, "the peer fields");
    }

    [Fact]
    public async Task A_custom_json_formatter_registered_by_type_is_disposed_once_with_the_provider()
    {
        TypeFormatter.Created = null;
        var provider = ResolveProvider(b => { b.Services.AddSingleton<IEventFormatterProxy<ICacheEvent>, TypeFormatter>(); b.AddMessagePackEvents(write: false); }, false);
        provider.GetRequiredService<IEventFormatterProxy<ICacheEvent>>().Should().BeOfType<CompositeEventFormatter>();

        await provider.DisposeAsync();

        TypeFormatter.Created.Should().NotBeNull();
        TypeFormatter.Created!.Disposals.Should().Be(1);
    }

    [Fact]
    public async Task A_custom_json_formatter_registered_by_factory_is_disposed_once_with_the_provider()
    {
        var made = new DisposableFormatter();
        var provider = ResolveProvider(b => { b.Services.AddSingleton<IEventFormatterProxy<ICacheEvent>>(_ => made); b.AddMessagePackEvents(write: false); }, false);
        provider.GetRequiredService<IEventFormatterProxy<ICacheEvent>>().Should().BeOfType<CompositeEventFormatter>();

        await provider.DisposeAsync();

        made.Disposals.Should().Be(1);
    }

    [Fact]
    public async Task A_custom_json_formatter_registered_by_instance_is_left_to_its_owner()
    {
        var owned = new DisposableFormatter();
        var provider = ResolveProvider(b => { b.Services.AddSingleton<IEventFormatterProxy<ICacheEvent>>(owned); b.AddMessagePackEvents(write: false); }, false);
        provider.GetRequiredService<IEventFormatterProxy<ICacheEvent>>().Should().BeOfType<CompositeEventFormatter>();

        await provider.DisposeAsync();

        owned.Disposals.Should().Be(0);
    }

    private static long Allocated(Action action)
    {
        action();
        action();
        var before = GC.GetAllocatedBytesForCurrentThread();
        action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static PeerFieldBytes PeerBytes() => new() { ["a"] = [1, 2, 3], ["b"] = null, ["c"] = [], ["d"] = [255, 0, 254] };

    private static Dictionary<string, string?> PeerText() => PeerBytes().ToDictionary(f => f.Key, f => f.Value is null ? null : Convert.ToBase64String(f.Value));

    private static IEqualityComparer<string> ComparerOf(object map) =>
        (IEqualityComparer<string>)map.GetType().GetProperty("Comparer")!.GetValue(map)!;

    private static CacheEvent Event(string type, IDictionary<string, object?>? properties) => new()
    {
        Id = Guid.NewGuid().ToString(),
        Type = type,
        Source = Source,
        Data = new CacheEventData("the-key", properties),
    };

    private static ReadOnlyMemory<byte> Raw(WriteBody write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        buffer.GetSpan(1)[0] = CacheValueFormats.MessagePack;
        buffer.Advance(1);
        var writer = new MessagePackWriter(buffer);
        write(ref writer);
        writer.Flush();
        return buffer.WrittenMemory;
    }

    private static IEventFormatterProxy<ICacheEvent> Resolve(Action<ICachingBuilder> configure) =>
        ResolveProvider(configure, configureFirst: false).GetRequiredService<IEventFormatterProxy<ICacheEvent>>();

    private static ServiceProvider ResolveProvider(Action<ICachingBuilder> configure, bool configureFirst) =>
        new ServiceCollection()
            .AddCaching(
                b =>
                {
                    b.AddRedisConnection(o => o.ConnectionString = "127.0.0.1:1");
                    b.AddRedis(o => o.FormatNegotiation.Mode = FormatNegotiationMode.Force);
                    b.AddBroadcast();
                    if (configureFirst)
                    {
                        configure(b);
                    }

                    b.AddInMemoryRedis();
                    if (!configureFirst)
                    {
                        configure(b);
                    }
                },
                o =>
                {
                    o.AppShortName = "mp";
                    o.SourceUri = Source;
                })
            .BuildServiceProvider();

    private class DisposableFormatter : IEventFormatterProxy<ICacheEvent>, IDisposable, IAsyncDisposable
    {
        private int _disposals;

        public int Disposals => Volatile.Read(ref _disposals);

        public ICacheEvent? Decode(ReadOnlyMemory<byte> body) => null;

        public ReadOnlyMemory<byte> Encode(ICacheEvent @event) => default;

        public void Dispose() => Interlocked.Increment(ref _disposals);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TypeFormatter : DisposableFormatter
    {
        public TypeFormatter() => Created = this;

        public static TypeFormatter? Created { get; set; }
    }
}
