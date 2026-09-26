using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UiPath.Caching.Tests;

public class SerializedPayloadTests
{
    public static IEnumerable<object?[]> Values() =>
    [
        [null],
        [42],
        ["text with \"quotes\" and <html> & ünïcödé"],
        [new byte[] { 1, 2, 3 }],
        [new Poco { Name = "p", Count = 3, Tags = ["a", "b"] }],
        [new List<Poco> { new() { Name = "x" }, new() { Name = "y" } }],
        [new string('x', 100_000)],
    ];

    [Theory]
    [MemberData(nameof(Values))]
    public void The_payload_holds_the_bytes_Serialize_writes(object? value)
    {
        var sut = new SystemJsonByteSerializerProxy();

        using var payload = SerializedPayload.Serialize(sut, value);

        payload.Memory.ToArray().Should().Equal(sut.Serialize(value));
    }

    [Theory]
    [MemberData(nameof(Values))]
    public void Custom_options_shape_the_payload_as_they_shape_Serialize(object? value)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        var sut = new SystemJsonByteSerializerProxy(options);

        using var payload = SerializedPayload.Serialize(sut, value);

        payload.Memory.ToArray().Should().Equal(sut.Serialize(value));
    }

    [Fact]
    public void A_value_declared_as_its_base_type_is_written_with_its_runtime_contract()
    {
        var sut = new SystemJsonByteSerializerProxy();
        Poco value = new DerivedPoco { Name = "d", Extra = "kept" };

        using var payload = SerializedPayload.Serialize(sut, value);

        payload.Memory.ToArray().Should().Equal(sut.Serialize(value));
        System.Text.Encoding.UTF8.GetString(payload.Memory.Span).Should().Contain("kept");
    }

    [Fact]
    public void A_value_type_is_written_as_Serialize_writes_it()
    {
        var sut = new SystemJsonByteSerializerProxy();
        int? value = 7;

        using var payload = SerializedPayload.Serialize(sut, value);

        payload.Memory.ToArray().Should().Equal(sut.Serialize(value));
    }

    [Fact]
    public void Null_is_written_as_Serialize_writes_it_even_when_a_converter_handles_null()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new NullHandlingConverter());
        var sut = new SystemJsonByteSerializerProxy(options);
        Nested? value = null;

        using var payload = SerializedPayload.Serialize(sut, value);

        payload.Memory.ToArray().Should().Equal(sut.Serialize(value));
    }

    [Fact]
    public void A_nullable_value_is_written_with_its_underlying_contract()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new NullableIntConverter());
        var sut = new SystemJsonByteSerializerProxy(options);
        int? value = 5;

        using var payload = SerializedPayload.Serialize(sut, value);

        payload.Memory.ToArray().Should().Equal(sut.Serialize(value));
    }

    [Fact]
    public void A_subclass_that_overrides_Serialize_keeps_its_own_bytes()
    {
        var sut = new PrefixingSerializer();

        using var payload = SerializedPayload.Serialize(sut, "v");

        payload.Memory.ToArray().Should().Equal(sut.Serialize("v"));
    }

    [Fact]
    public void A_subclass_that_adds_its_own_overloads_still_serializes()
    {
        var sut = new OverloadingSerializer();

        using var payload = SerializedPayload.Serialize(sut, "v");

        payload.Memory.ToArray().Should().Equal(sut.Serialize("v"));
    }

    [Fact]
    public void A_converter_writing_invalid_json_gets_the_same_result_as_through_Serialize()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new TwoValuesConverter());
        var sut = new SystemJsonByteSerializerProxy(options);
        var value = new Nested { Value = "x" };

        var viaSerialize = Outcome(() => sut.Serialize(value)!);
        var viaPayload = Outcome(() =>
        {
            using var payload = SerializedPayload.Serialize(sut, value);
            return payload.Memory.ToArray();
        });

        viaPayload.Should().Be(viaSerialize);
    }

    [Fact]
    public void A_nested_serialization_does_not_share_the_outer_buffer()
    {
        var inner = new SystemJsonByteSerializerProxy();
        var options = new JsonSerializerOptions();
        options.Converters.Add(new NestingConverter(inner));
        var sut = new SystemJsonByteSerializerProxy(options);
        var value = new Nested { Value = "outer" };

        using var payload = SerializedPayload.Serialize(sut, value);

        payload.Memory.ToArray().Should().Equal(sut.Serialize(value));
    }

    [Fact]
    public void The_raw_proxy_lends_the_callers_memory()
    {
        var bytes = new byte[] { 9, 8, 7 };
        var sut = new RawByteSerializerProxy();

        using var payload = SerializedPayload.Serialize(sut, new ReadOnlyMemory<byte>(bytes));

        MemoryMarshal.TryGetArray(payload.Memory, out var segment).Should().BeTrue();
        segment.Array.Should().BeSameAs(bytes);
    }

    [Fact]
    public void A_payload_made_from_borrowed_memory_leaves_it_alone_when_disposed()
    {
        var bytes = new byte[] { 1, 2 };
        var payload = new SerializedPayload(bytes);

        payload.Dispose();

        payload.Memory.ToArray().Should().Equal(1, 2);
    }

    private static string Outcome(Func<byte[]> serialize)
    {
        try
        {
            return System.Text.Encoding.UTF8.GetString(serialize());
        }
        catch (Exception ex)
        {
            return ex.GetType().Name;
        }
    }

    public class Poco
    {
        public string? Name { get; set; }

        public int Count { get; set; }

        public string? Missing { get; set; }

        public List<string>? Tags { get; set; }
    }

    public sealed class DerivedPoco : Poco
    {
        public string? Extra { get; set; }
    }

    public sealed class Nested
    {
        public string? Value { get; set; }
    }

    /// <summary>Adds overloads beside the virtual members without overriding them.</summary>
    private sealed class OverloadingSerializer : SystemJsonByteSerializerProxy
    {
        public byte[] Serialize(string value, bool indented) => System.Text.Encoding.UTF8.GetBytes(indented ? value : value.Trim());

        public ReadOnlyMemory<byte> SerializeToMemory<T>(T? value, int unused) => SerializeToMemory(value);
    }

    private sealed class PrefixingSerializer : SystemJsonByteSerializerProxy
    {
        public override byte[]? Serialize(object? value) => [0xFF, .. base.Serialize(value)!];
    }

    /// <summary>Two root values: only the writer's own validation would reject it.</summary>
    private sealed class TwoValuesConverter : JsonConverter<Nested>
    {
        public override Nested Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, Nested value, JsonSerializerOptions options)
        {
            writer.WriteStringValue("a");
            writer.WriteStringValue("b");
        }
    }

    private sealed class NullHandlingConverter : JsonConverter<Nested>
    {
        public override bool HandleNull => true;

        public override Nested Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, Nested? value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value is null ? "handled-null" : value.Value);
    }

    private sealed class NullableIntConverter : JsonConverter<int?>
    {
        public override int? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options) =>
            writer.WriteStringValue("nullable-contract");
    }

    /// <summary>Serializes its value through another proxy mid-write, as a converter that embeds JSON would.</summary>
    private sealed class NestingConverter(SystemJsonByteSerializerProxy inner) : JsonConverter<Nested>
    {
        public override Nested Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, Nested value, JsonSerializerOptions options)
        {
            using var embedded = SerializedPayload.Serialize(inner, value.Value);
            writer.WriteStringValue(System.Text.Encoding.UTF8.GetString(embedded.Memory.Span));
        }
    }
}
