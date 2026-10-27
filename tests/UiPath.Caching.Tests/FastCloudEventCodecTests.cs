using System.Text;
using System.Text.Json;
using CloudNative.CloudEvents;
using CloudNative.CloudEvents.SystemTextJson;
using Microsoft.Extensions.DependencyInjection;
using UiPath.Caching.CloudEvents;
using UiPath.Caching.Config;

namespace UiPath.Caching.Tests;

public class FastCloudEventCodecTests
{
    private const string Json = "application/json";

    private static readonly int[] NestedNumbers = [1, 2, 3];
    private static readonly string[] TypicalFields = ["name", "email", "address", "phone"];

    private static readonly CacheEventFormatterProxy Stock = new(new JsonEventFormatter<CacheEventData>());
    private static readonly CacheEventFormatterProxy Fast = CacheEventFormatterProxy.CreateDefault();

    public static TheoryData<string, CacheEventData> Payloads() => new()
    {
        { "key only", new CacheEventData("user:42") },
        { "empty key", new CacheEventData(string.Empty) },
        { "empty properties", new CacheEventData("k", new Dictionary<string, object?>()) },
        { "scalars", new CacheEventData("k", new Dictionary<string, object?> { ["s"] = "v", ["i"] = 7, ["l"] = long.MaxValue, ["d"] = 1.5, ["b"] = true, ["n"] = null }) },
        { "nested", new CacheEventData("k", new Dictionary<string, object?> { ["o"] = new Dictionary<string, object?> { ["x"] = NestedNumbers }, ["a"] = new object?[] { "a", null, 2 } }) },
        { "unicode", new CacheEventData("clé:日本語:😀", new Dictionary<string, object?> { ["é"] = "ünï\u2028cödé", ["emoji"] = "😀" }) },
        { "escapes", new CacheEventData("a\"b\\c\n\t<>&+'", new Dictionary<string, object?> { ["<k>"] = "\u0000\u001f</script>" }) },
        { "large", new CacheEventData(new string('k', 5_000), new Dictionary<string, object?> { ["blob"] = new string('x', 200_000), ["unicode"] = new string('é', 50_000) }) },
        { "many properties", new CacheEventData("k", Enumerable.Range(0, 500).ToDictionary(i => $"p{i}", i => (object?)$"v{i}")) },
    };

    public static TheoryData<string> Types() => [.. KnownTypes(), "custom", "with space", "ünï", "a/b+c", "  padded  "];

    public static TheoryData<string> Sources() =>
    [
        "urn:machine:1", "https://host.example/a/b?c=d#e", "https://host.example/a b", "https://hôst.example/ü", "http://[::1]:8080/x", "file:///c:/x", "urn:é:🙂", "relative/path", "/abs/path", "?q", "#frag", "  urn:padded  ", "HTTPS://Host.Example:443/a/../b", "https://host.example/a%20b", "https://host.example/%7Euser", "urn:UPPER:Case",
    ];

    public static TheoryData<string> WireVariants() =>
    [
        """{"specversion":"1.0","id":"i","type":"t","source":"urn:s","datacontenttype":"application/json","data":{"Key":"k"}}""",
        """{"data":{"Key":"k","Properties":{"a":[1,{"b":null}]}},"datacontenttype":"application/json","source":"urn:s","type":"t","id":"i","specversion":"1.0"}""",
        """  { "specversion" : "1.0" , "id" : "i" , "type" : "t" , "source" : "urn:s" , "datacontenttype" : "application/json" , "data" : { "Key" : "k" } }  """,
        """{"specversion":"1.0","id":"\u0069d","type":"t\u00e9","source":"urn:\u00e9","datacontenttype":"application/json","data":{"Key":"\ud83d\ude00"}}""",
        """{"specversion":"1.0","\u0069d":"i","type":"t","source":"urn:s","datacontenttype":"application/json","data":{"Key":"k"}}""",
        """{"specversion":"1.0","id":"i","type":"t","source":"urn:s","datacontenttype":"application/json","data":{"Key":"k"}}""" + "\n",
        """{"specversion":"1.0","id":"i","type":"t","source":"urn:s","datacontenttype":"application/json","data":{"key":"k"}}""",
        """{"specversion":"1.0","id":"i","type":"t","source":"urn:s","datacontenttype":"application/json","data":{}}""",
        """{"specversion":"1.0","id":"i","type":"t","source":"urn:s","datacontenttype":"application/json","data":{"Key":"k","Properties":null}}""",
        """{"specversion":"1.0","id":"i","type":"t","source":"urn:s","datacontenttype":"application/json","data":{"Key":null}}""",
        """{"specversion":"1.0","id":"i","type":"t","source":"urn:s","datacontenttype":"application/json","data":{"Key":"k","Extra":1}}""",
        """{"specversion":"1.0","id":"i","type":"t","source":"relative","datacontenttype":"application/json","data":{"Key":"k"}}""",
        """{"specversion":"1.0","id":"  ","type":"t","source":"urn:s","datacontenttype":"application/json","data":{"Key":"k"}}""",
        """{"specversion":"1.0","id":"i","type":"  ","source":"urn:s","datacontenttype":"application/json","data":{"Key":"k"}}""",
        """{"specversion":"1.0","id":"i","type":"t","source":"  ","datacontenttype":"application/json","data":{"Key":"k"}}""",
        """{"specversion":"1.0","id":"i","type":"t","source":"a b%zz::","datacontenttype":"application/json","data":{"Key":"k"}}""",
        // the shapes below leave the fast path
        """{"specversion":"1.0","id":"i","type":"t","source":"urn:s","datacontenttype":"application/json"}""",
        """{"specversion":"1.0","id":"i","type":"t","source":"urn:s","datacontenttype":"application/json","data":null}""",
        """{"specversion":"1.0","id":"i","type":"t","source":"urn:s","datacontenttype":"application/json","data":"text"}""",
        """{"specversion":"1.0","id":"i","type":"t","source":"urn:s","datacontenttype":"application/json","data":[1]}""",
        """{"specversion":"1.0","id":"i","type":"t","source":"urn:s","data":{"Key":"k"}}""",
        """{"specversion":"1.0","id":"i","type":"t","source":"urn:s","datacontenttype":"application/json; charset=utf-8","data":{"Key":"k"}}""",
        """{"specversion":"1.0","id":"i","type":"t","source":"urn:s","datacontenttype":"text/plain","data":{"Key":"k"}}""",
        """{"specversion":"1.0","id":"i","type":"t","source":"urn:s","datacontenttype":null,"data":{"Key":"k"}}""",
        """{"specversion":"1.0","id":"i","type":"t","source":"urn:s","datacontenttype":"application/json","subject":"s","data":{"Key":"k"}}""",
        """{"specversion":"1.0","id":"i","type":"t","source":"urn:s","datacontenttype":"application/json","time":"2026-01-02T03:04:05Z","data":{"Key":"k"}}""",
        """{"specversion":"1.0","id":"i","type":"t","source":"urn:s","datacontenttype":"application/json","dataschema":"urn:x","data":{"Key":"k"}}""",
        """{"specversion":"1.0","id":"i","type":"t","source":"urn:s","datacontenttype":"application/json","extra":"x","data":{"Key":"k"}}""",
        """{"specversion":"1.0","id":"i","type":"t","source":"urn:s","datacontenttype":"application/json","extra":null,"data":{"Key":"k"}}""",
        """{"specversion":"1.0","id":"i","type":"t","source":"urn:s","datacontenttype":"application/json","extra":5,"data":{"Key":"k"}}""",
        """{"specversion":"1.0","id":"i","type":"t","source":"urn:s","datacontenttype":"application/json","extra":true,"data":{"Key":"k"}}""",
        """{"specversion":"1.0","id":"i","type":"t","source":"urn:s","datacontenttype":"application/json","data_base64":"e30=","data":{"Key":"k"}}""",
        """{"specversion":"1.0","id":"i","type":"t","source":"urn:s","datacontenttype":"application/octet-stream","data_base64":"e30="}""",
        """{"specversion":"0.3","id":"i","type":"t","source":"urn:s","datacontenttype":"application/json","data":{"Key":"k"}}""",
        """{"specversion":"1.0","id":"i","type":"t","source":"urn:s","datacontenttype":"application/json","data":{"Key":"k"},"data":{"Key":"other"}}""",
        """{"specversion":"1.0","id":"i","id":"j","type":"t","source":"urn:s","datacontenttype":"application/json","data":{"Key":"k"}}""",
        """{"specversion":"1.0","id":"","type":"t","source":"urn:s","datacontenttype":"application/json","data":{"Key":"k"}}""",
        """{"specversion":"1.0","id":"i","type":"","source":"urn:s","datacontenttype":"application/json","data":{"Key":"k"}}""",
        """{"specversion":"1.0","id":"i","type":"t","source":"","datacontenttype":"application/json","data":{"Key":"k"}}""",
        """{"specversion":"1.0","id":"i","type":"t","datacontenttype":"application/json","data":{"Key":"k"}}""",
        """{"specversion":"1.0","type":"t","source":"urn:s","datacontenttype":"application/json","data":{"Key":"k"}}""",
        """{"specversion":"1.0","id":"i","source":"urn:s","datacontenttype":"application/json","data":{"Key":"k"}}""",
        """{"id":"i","type":"t","source":"urn:s","datacontenttype":"application/json","data":{"Key":"k"}}""",
        """{"specversion":1.0,"id":"i","type":"t","source":"urn:s","datacontenttype":"application/json","data":{"Key":"k"}}""",
        """{"specversion":"1.0","id":5,"type":"t","source":"urn:s","datacontenttype":"application/json","data":{"Key":"k"}}""",
        """{"specversion":"1.0","id":{"a":1},"type":"t","source":"urn:s","datacontenttype":"application/json","data":{"Key":"k"}}""",
        """{"specversion":"1.0","id":"i","type":"t","source":"urn:s","datacontenttype":"application/json","data":{"Key":"k"}} trailing""",
        """{"specversion":"1.0","id":"i","type":"t","source":"urn:s","datacontenttype":"application/json","data":{"Key":"k"}}{}""",
        """{"specversion":"1.0","id":"i","type":"t","source":"urn:s","datacontenttype":"application/json","data":{"Key":"k"},}""",
        """{"specversion":"1.0","id":"i","type":"t","source":"urn:s","datacontenttype":"application/json", /* c */ "data":{"Key":"k"}}""",
        """{"specversion":"1.0","id":"i","type":"t","source":"urn:s","datacontenttype":"application/json","data":{"Key":"k"}""",
        """{"specversion":"1.0","id":"i","type":"t","source":"urn:s","datacontenttype":"application/json","data":{"Key":"k""",
        """[{"specversion":"1.0"}]""",
        "\"just a string\"",
        "{}",
        "[]",
        "null",
        "42",
        "not json",
        "{",
        "\0",
    ];

    [Theory]
    [MemberData(nameof(Payloads), DisableDiscoveryEnumeration = true)]
    public void Encode_is_byte_identical_for_every_payload_and_known_type(string name, CacheEventData data)
    {
        name.Should().NotBeEmpty();
        foreach (var type in KnownTypes())
        {
            AssertEncodeParity(Standard(type, "urn:machine:1", data));
            AssertEncodeParity(Standard(type, "urn:machine:1", data, id: "fixed-id"));
        }
    }

    [Theory]
    [MemberData(nameof(Types))]
    public void Encode_is_byte_identical_for_every_type(string type) =>
        AssertEncodeParity(Standard(type, "urn:machine:1", new CacheEventData("k")));

    [Theory]
    [MemberData(nameof(Sources))]
    public void Encode_is_byte_identical_for_every_source(string source) =>
        AssertEncodeParity(Standard("CacheSet", source, new CacheEventData("k")));

    [Fact]
    public void Encode_is_byte_identical_for_every_attribute_order()
    {
        var data = new CacheEventData("k", new Dictionary<string, object?> { ["a"] = 1 });
        var setters = new Dictionary<string, Action<CloudEvent>>
        {
            ["id"] = e => e.Id = "id-1",
            ["type"] = e => e.Type = "CacheSet",
            ["source"] = e => e.Source = new Uri("urn:s"),
            ["contenttype"] = e => e.DataContentType = Json,
        };
        foreach (var order in Permutations([.. setters.Keys]))
        {
            var cloudEvent = new CloudEvent { Data = data };
            foreach (var step in order)
            {
                setters[step](cloudEvent);
            }

            AssertEncodeParity(new CacheCloudEventWrapper(cloudEvent));
        }
    }

    [Fact]
    public void Encode_is_byte_identical_after_an_attribute_is_removed_and_set_again()
    {
        var cloudEvent = Standard("CacheSet", "urn:s", new CacheEventData("k")).CloudEvent;
        cloudEvent.Type = null;
        cloudEvent.Type = "CacheRemoved";
        AssertEncodeParity(new CacheCloudEventWrapper(cloudEvent));
    }

    [Fact]
    public void Encode_of_a_decoded_event_is_byte_identical()
    {
        var wire = Stock.Encode(Standard("CacheSet", "urn:s", new CacheEventData("k", new Dictionary<string, object?> { ["a"] = "é" })));
        AssertEncodeParity(Stock.Decode(wire)!);
        AssertEncodeParity(Fast.Decode(wire)!);
    }

    [Fact]
    public void Encode_of_an_event_the_fast_path_does_not_support_matches_the_stock_formatter()
    {
        var data = new CacheEventData("k");
        List<CloudEvent> events =
        [
            Standard("CacheSet", "urn:s", data).CloudEvent.With(e => e.Subject = "subject"),
            Standard("CacheSet", "urn:s", data).CloudEvent.With(e => e.Time = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero)),
            Standard("CacheSet", "urn:s", data).CloudEvent.With(e => e.DataSchema = new Uri("urn:schema")),
            Standard("CacheSet", "urn:s", data).CloudEvent.With(e => e.DataContentType = "application/json; charset=utf-8"),
            Standard("CacheSet", "urn:s", data).CloudEvent.With(e => e.DataContentType = "text/plain"),
            Standard("CacheSet", "urn:s", data).CloudEvent.With(e => e.DataContentType = null),
            Standard("CacheSet", "urn:s", data).CloudEvent.With(e => e["extra"] = "x"),
            new CloudEvent(CloudEventsSpecVersion.V1_0, [CloudEventAttribute.CreateExtension("ext", CloudEventAttributeType.String)])
            {
                Id = "i",
                Type = "t",
                Source = new Uri("urn:s"),
                DataContentType = Json,
                Data = data,
                ["ext"] = "v",
            },
            new CloudEvent { Id = "i", Type = "t", Source = new Uri("urn:s"), DataContentType = Json },
            new CloudEvent { Id = "i", Type = "t", Source = new Uri("urn:s"), DataContentType = Json, Data = "not cache data" },
        ];

        foreach (var cloudEvent in events)
        {
            AssertSameOutcome(() => Stock.Encode(new CacheCloudEventWrapper(cloudEvent)), () => Fast.Encode(new CacheCloudEventWrapper(cloudEvent)), (a, b) => b.ToArray().Should().Equal(a.ToArray()));
        }
    }

    [Fact]
    public void Encode_of_an_invalid_event_fails_like_the_stock_formatter()
    {
        CloudEvent[] invalid =
        [
            new() { Type = "t", Source = new Uri("urn:s"), Data = new CacheEventData("k"), DataContentType = Json },
            new() { Id = "i", Source = new Uri("urn:s"), Data = new CacheEventData("k"), DataContentType = Json },
            new() { Id = "i", Type = "t", Data = new CacheEventData("k"), DataContentType = Json },
        ];
        foreach (var cloudEvent in invalid)
        {
            AssertSameOutcome(() => Stock.Encode(new CacheCloudEventWrapper(cloudEvent)), () => Fast.Encode(new CacheCloudEventWrapper(cloudEvent)), (a, b) => b.ToArray().Should().Equal(a.ToArray()));
        }
    }

    [Fact]
    public void Encode_of_a_foreign_event_is_empty_on_both_paths()
    {
        var foreign = Substitute.For<ICacheEvent>();
        Fast.Encode(foreign).Should().Be(ReadOnlyMemory<byte>.Empty);
        Stock.Encode(foreign).Should().Be(ReadOnlyMemory<byte>.Empty);
    }

    [Theory]
    [MemberData(nameof(Payloads), DisableDiscoveryEnumeration = true)]
    public void Decode_equals_the_stock_decode_for_every_payload(string name, CacheEventData data)
    {
        name.Should().NotBeEmpty();
        foreach (var type in KnownTypes())
        {
            AssertDecodeParity(Stock.Encode(Standard(type, "urn:machine:1", data)));
        }
    }

    [Theory]
    [MemberData(nameof(Sources))]
    public void Decode_equals_the_stock_decode_for_every_source(string source) =>
        AssertDecodeParity(Stock.Encode(Standard("CacheSet", source, new CacheEventData("k"))));

    [Theory]
    [MemberData(nameof(Types))]
    public void Decode_equals_the_stock_decode_for_every_type(string type) =>
        AssertDecodeParity(Stock.Encode(Standard(type, "urn:machine:1", new CacheEventData("k"))));

    [Fact]
    public void Decode_equals_the_stock_decode_for_a_repeated_type_and_source()
    {
        for (var i = 0; i < 100; i++)
        {
            AssertDecodeParity(Stock.Encode(Standard("T" + (i % 40), "urn:s:" + (i % 40), new CacheEventData("k" + i))));
        }
    }

    [Fact]
    public void Decode_keeps_the_first_sixteen_types_cached_and_does_not_cache_the_ones_past_them()
    {
        var fast = CacheEventFormatterProxy.CreateDefault();
        ICacheEvent Decode(string type) => fast.Decode(Stock.Encode(Standard(type, "urn:s", new CacheEventData("k"))))!;

        var first = Enumerable.Range(0, 16).Select(i => Decode("T" + i).Type).ToList();
        var overflow = Decode("overflow").Type;

        for (var i = 0; i < 16; i++)
        {
            Decode("T" + i).Type.Should().BeSameAs(first[i], "a full cache keeps what it has");
        }

        Decode("overflow").Type.Should().NotBeSameAs(overflow, "a miss past the limit is not stored");
    }

    [Theory]
    [MemberData(nameof(WireVariants))]
    public void Decode_matches_the_stock_decode_or_falls_back_for_a_wire_shape(string wire)
    {
        var bytes = Encoding.UTF8.GetBytes(wire);
        AssertDecodeParity(bytes);
        byte[] withBom = [0xEF, 0xBB, 0xBF, .. bytes];
        AssertDecodeParity(withBom);
    }

    [Fact]
    public void Decode_matches_the_stock_decode_for_invalid_utf8_and_truncation()
    {
        var good = Stock.Encode(Standard("CacheSet", "urn:s", new CacheEventData("key", new Dictionary<string, object?> { ["a"] = "value" }))).ToArray();
        AssertDecodeParity(good);
        for (var cut = 1; cut < good.Length; cut += 3)
        {
            AssertDecodeParity(good.AsMemory(0, cut));
        }

        var broken = (byte[])good.Clone();
        broken[Array.IndexOf(broken, (byte)'k') + 1] = 0xFF;
        AssertDecodeParity(broken);
        byte[] brokenSource = [.. "{\"specversion\":\"1.0\",\"id\":\"i\",\"type\":\"t\",\"source\":\"urn:"u8, 0xC3, .. "\",\"datacontenttype\":\"application/json\",\"data\":{\"Key\":\"k\"}}"u8];
        AssertDecodeParity(brokenSource);
    }

    [Fact]
    public void Decode_matches_the_stock_decode_for_deeply_nested_data()
    {
        foreach (var depth in new[] { 10, 30, 60, 61, 62, 63, 64, 65, 100 })
        {
            var nested = string.Concat(Enumerable.Repeat("{\"n\":", depth)) + "1" + new string('}', depth);
            AssertDecodeParity(Encoding.UTF8.GetBytes($$$"""{"specversion":"1.0","id":"i","type":"t","source":"urn:s","datacontenttype":"application/json","data":{"Key":"k","Properties":{{{nested}}}}}"""));
        }
    }

    [Fact]
    public void Decode_of_empty_input_is_null_on_both_paths()
    {
        Fast.Decode(ReadOnlyMemory<byte>.Empty).Should().BeNull();
        Stock.Decode(ReadOnlyMemory<byte>.Empty).Should().BeNull();
    }

    [Fact]
    public void Decode_of_a_supported_event_returns_a_wrapper_whose_cloud_event_matches_the_stock_one()
    {
        var wire = Stock.Encode(Standard("CacheSet", "urn:s", new CacheEventData("k", new Dictionary<string, object?> { ["a"] = 1 })));
        var fast = (CacheCloudEventWrapper)Fast.Decode(wire)!;
        var stock = (CacheCloudEventWrapper)Stock.Decode(wire)!;

        fast.IsValid().Should().BeTrue();
        fast.CloudEvent.Id.Should().Be(stock.CloudEvent.Id);
        fast.CloudEvent.Type.Should().Be(stock.CloudEvent.Type);
        fast.CloudEvent.Source.Should().Be(stock.CloudEvent.Source);
        fast.CloudEvent.DataContentType.Should().Be(stock.CloudEvent.DataContentType);
        fast.CloudEvent.SpecVersion.Should().Be(stock.CloudEvent.SpecVersion);
        fast.CloudEvent.GetPopulatedAttributes().Select(a => a.Key.Name).Should().Equal(stock.CloudEvent.GetPopulatedAttributes().Select(a => a.Key.Name));
        fast.AttachTransportId("1-0");
        fast.TransportId.Should().Be("1-0");
        fast.Invoking(e => e.AttachTransportId("2-0")).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void A_proxy_built_directly_keeps_the_stock_behaviour_for_a_configured_formatter()
    {
        var camel = new CacheEventFormatterProxy(new JsonEventFormatter<CacheEventData>(new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }, default));
        var ev = Standard("CacheSet", "urn:s", new CacheEventData("k"));

        var bytes = camel.Encode(ev);

        Encoding.UTF8.GetString(bytes.Span).Should().Contain("\"data\":{\"key\":\"k\"");
        camel.Decode(bytes)!.Key.Should().Be("k");
        bytes.ToArray().Should().NotEqual(Stock.Encode(ev).ToArray());
    }

    [Fact]
    public void AddCloudEvents_wires_the_default_formatter_with_the_fast_path()
    {
        var services = new ServiceCollection();
        new CachingBuilder(services).AddCloudEvents();
        using var provider = services.BuildServiceProvider();
        var wired = provider.GetRequiredService<IEventFormatterProxy<ICacheEvent>>();
        var ev = Typical();

        wired.Should().BeOfType<CacheEventFormatterProxy>();
        wired.Encode(ev).ToArray().Should().Equal(Stock.Encode(ev).ToArray());
        AllocatedBy(() => wired.Encode(ev)).Should().BeLessThan(2_000);
    }

    [Fact]
    public void The_codec_declines_what_it_does_not_fully_support()
    {
        var codec = new FastCloudEventCodec();
        var data = new CacheEventData("k");
        var supported = Standard("CacheSet", "urn:s", data).CloudEvent;
        codec.TryEncode(supported, out _).Should().BeTrue();

        codec.TryEncode(supported.With(e => e.Subject = "s"), out _).Should().BeFalse();
        codec.TryEncode(Standard("CacheSet", "urn:s", data).CloudEvent.With(e => e["x"] = "1"), out _).Should().BeFalse();
        codec.TryEncode(Standard("CacheSet", "urn:s", data).CloudEvent.With(e => e.DataContentType = "text/plain"), out _).Should().BeFalse();
        codec.TryEncode(Standard("CacheSet", "urn:s", data).CloudEvent.With(e => e.DataContentType = null), out _).Should().BeFalse();
        codec.TryEncode(Standard("CacheSet", "urn:s", data).CloudEvent.With(e => e.Data = null), out _).Should().BeFalse();

        codec.TryDecode("""{"specversion":"1.0","id":"i","type":"t","source":"urn:s","datacontenttype":"application/json","extra":"x","data":{"Key":"k"}}"""u8, out _).Should().BeFalse();
        codec.TryDecode("""{"specversion":"1.0","id":"i","type":"t","source":"urn:s","datacontenttype":"application/json","data":{"Key":"k"}}"""u8, out var decoded).Should().BeTrue();
        decoded!.Key.Should().Be("k");
        codec.TryDecode("{"u8, out _).Should().BeFalse();
    }

    [Fact]
    public void Encode_then_decode_of_a_typical_event_allocates_well_under_the_stock_cost()
    {
        var ev = Typical();
        var wire = Stock.Encode(ev);
        wire.Length.Should().BeInRange(300, 450);

        var stock = AllocatedBy(() => Stock.Decode(Stock.Encode(ev)));
        var fast = AllocatedBy(() => Fast.Decode(Fast.Encode(ev)));
        var fastEncode = AllocatedBy(() => Fast.Encode(ev));
        var fastDecode = AllocatedBy(() => Fast.Decode(wire));

        stock.Should().BeGreaterThan(10_000, "the stock formatter is the baseline this test guards against");
        fast.Should().BeLessThan(5_500).And.BeLessThan(stock / 2);
        fastEncode.Should().BeLessThan(2_000);
        fastDecode.Should().BeLessThan(3_500);
    }

    [Fact]
    public void Parallel_encodes_and_decodes_each_produce_their_own_correct_bytes()
    {
        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.For(0, 32, new ParallelOptions { MaxDegreeOfParallelism = 32 }, worker =>
        {
            for (var i = 0; i < 2_000; i++)
            {
                var size = (worker * 7 + i) % 5 == 0 ? 90_000 : 10 + (i % 50);
                var data = new CacheEventData($"key-{worker}-{i}", new Dictionary<string, object?> { ["w"] = worker, ["blob"] = new string((char)('a' + (worker % 26)), size) });
                var ev = Standard(KnownTypes()[i % 4], $"urn:w:{worker}", data, id: $"{worker}-{i}");

                var bytes = Fast.Encode(ev);
                var expected = Stock.Encode(ev);
                if (!bytes.Span.SequenceEqual(expected.Span))
                {
                    failures.Add($"encode {worker}/{i}");
                }

                var decoded = Fast.Decode(bytes);
                if (decoded?.Id != $"{worker}-{i}" || decoded.Key != $"key-{worker}-{i}" || decoded.Source?.OriginalString != $"urn:w:{worker}")
                {
                    failures.Add($"decode {worker}/{i}");
                }
            }
        });

        failures.Should().BeEmpty();
    }

    [Fact]
    public void Encoded_bytes_stay_intact_after_the_thread_encodes_again()
    {
        var first = Standard("CacheSet", "urn:s", new CacheEventData("first", new Dictionary<string, object?> { ["a"] = new string('1', 300) }), id: "1");
        var second = Standard("CacheRemoved", "urn:s", new CacheEventData("second", new Dictionary<string, object?> { ["a"] = new string('2', 100_000) }), id: "2");

        var held = Fast.Encode(first);
        var snapshot = held.ToArray();
        Fast.Encode(second);
        Fast.Encode(Typical());

        held.ToArray().Should().Equal(snapshot).And.Equal(Stock.Encode(first).ToArray());
    }

    [Fact]
    public void A_failed_fast_encode_does_not_affect_the_next_one()
    {
        var bad = Standard("CacheSet", "urn:s", new CacheEventData("k", new Dictionary<string, object?> { ["x"] = new Func<int>(() => 1) }));
        var good = Typical();

        for (var i = 0; i < 3; i++)
        {
            AssertSameOutcome(() => Stock.Encode(bad), () => Fast.Encode(bad), (a, b) => b.ToArray().Should().Equal(a.ToArray()));
            Fast.Encode(good).ToArray().Should().Equal(Stock.Encode(good).ToArray());
        }
    }

    private static string[] KnownTypes() => [KnownEventTypes.CacheSet, KnownEventTypes.CacheRefreshed, KnownEventTypes.CacheRemoved, KnownEventTypes.TopicInvalidated];

    private static CacheCloudEventWrapper Typical() =>
        Standard(
            "CacheSet",
            "urn:machine:host-1",
            new CacheEventData(
                "orders:tenant-7f3a:customer:1842",
                new Dictionary<string, object?>
                {
                    ["version"] = 1842L,
                    ["hash"] = "c7f1b2a49de0",
                    ["fields"] = TypicalFields,
                    ["node"] = "host-1",
                    ["tenant"] = "7f3a2c1e-9b4d-4e8a-a3f1-6c5d2b8e0f47",
                    ["note"] = "typical broadcast payload for one cache write",
                }),
            id: "5b1c9d2e-3f4a-4b6c-8d7e-9f0a1b2c3d4e");

    private static CacheCloudEventWrapper Standard(string type, string source, CacheEventData data, string? id = null) =>
        new(new CloudEvent
        {
            Id = id ?? Guid.NewGuid().ToString(),
            Type = type,
            Source = new Uri(source, UriKind.RelativeOrAbsolute),
            DataContentType = Json,
            Data = data,
        });

    private static void AssertEncodeParity(ICacheEvent ev)
    {
        var expected = Stock.Encode(ev);
        var actual = Fast.Encode(ev);
        actual.ToArray().Should().Equal(expected.ToArray());
    }

    private static void AssertDecodeParity(ReadOnlyMemory<byte> wire) =>
        AssertSameOutcome(() => Stock.Decode(wire), () => Fast.Decode(wire), (expected, actual) =>
        {
            actual.Should().NotBeNull();
            actual.Id.Should().Be(expected!.Id);
            actual.Type.Should().Be(expected.Type);
            actual.Source.Should().Be(expected.Source);
            actual.Source?.OriginalString.Should().Be(expected.Source?.OriginalString);
            actual.Key.Should().Be(expected.Key);
            actual.IsValid().Should().Be(expected.IsValid());
            Serialize(actual.Data).Should().Be(Serialize(expected.Data));
            actual.GetType().Should().Be(expected.GetType());
        });

    private static string Serialize(CacheEventData? data) => JsonSerializer.Serialize(data);

    private static void AssertSameOutcome<T>(Func<T> stock, Func<T> fast, Action<T, T> compare)
    {
        T? expected = default;
        Exception? expectedError = null;
        try
        {
            expected = stock();
        }
        catch (Exception ex)
        {
            expectedError = ex;
        }

        if (expectedError is not null)
        {
            var thrown = Record.Exception(() => fast());
            thrown.Should().NotBeNull();
            thrown.GetType().Should().Be(expectedError.GetType());
            thrown.Message.Should().Be(expectedError.Message);
            return;
        }

        if (expected is null)
        {
            fast().Should().BeNull();
            return;
        }

        compare(expected, fast());
    }

    private static IEnumerable<string[]> Permutations(string[] items)
    {
        if (items.Length <= 1)
        {
            yield return items;
            yield break;
        }

        for (var i = 0; i < items.Length; i++)
        {
            var rest = items.Where((_, j) => j != i).ToArray();
            foreach (var tail in Permutations(rest))
            {
                yield return [items[i], .. tail];
            }
        }
    }

    private static long AllocatedBy(Action action)
    {
        for (var i = 0; i < 1_000; i++)
        {
            action();
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1_000; i++)
        {
            action();
        }

        return (GC.GetAllocatedBytesForCurrentThread() - before) / 1_000;
    }
}

internal static class CloudEventTestExtensions
{
    public static CloudEvent With(this CloudEvent cloudEvent, Action<CloudEvent> change)
    {
        change(cloudEvent);
        return cloudEvent;
    }
}
