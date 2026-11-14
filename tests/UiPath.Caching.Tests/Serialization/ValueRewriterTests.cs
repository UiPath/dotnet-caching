using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using UiPath.Caching.Serialization;
using UiPath.Caching.Tests.Fakes;

namespace UiPath.Caching.Tests.Serialization;

[Collection(WriterPoolCollection.Name)]
public class ValueRewriterTests
{
    private readonly PassthroughValueSerializer _fast = new(0x10);
    private readonly PassthroughValueSerializer _other = new(0x11);

    [Theory]
    [InlineData("{\"a\":1}", "{\"a\":1}")]
    [InlineData("{\"b\":1,\"a\":2}", "{\"a\":2,\"b\":1}")]
    [InlineData("{\n  \"a\": [1, 2],\n  \"b\": {\"c\": null, \"d\": \"x\"}\n}", "{\"b\":{\"d\":\"x\",\"c\":null},\"a\":[1,2]}")]
    [InlineData("\"A\"", "\"\\u0041\"")]
    [InlineData("  [1,2,3]  ", "[1,2,3]")]
    public void Json_that_differs_only_in_layout_is_lossless(string original, string reencoded) =>
        LosslessCheck.Json(Utf8(original), Utf8(reencoded)).Should().BeTrue();

    [Theory]
    [InlineData("{\"a\":1,\"extra\":2}", "{\"a\":1}")]
    [InlineData("{\"a\":1}", "{\"a\":1,\"extra\":2}")]
    [InlineData("{\"a\":1}", "{\"a\":2}")]
    [InlineData("[1,2]", "[2,1]")]
    [InlineData("\"a\"", "\"b\"")]
    [InlineData("{\"a\":null}", "{}")]
    [InlineData("{\"a\":\"1\"}", "{\"a\":1}")]
    [InlineData("{\"a\":true}", "{\"a\":\"true\"}")]
    [InlineData("{\"a\":{\"b\":1}}", "{\"a\":{\"b\":1,\"c\":1}}")]
    public void Json_that_gained_or_lost_something_is_lossy(string original, string reencoded) =>
        LosslessCheck.Json(Utf8(original), Utf8(reencoded)).Should().BeFalse();

    [Theory]
    [InlineData("{\"a\":", "{\"a\":1}")]
    [InlineData("{\"a\":1}", "not json")]
    [InlineData("", "{}")]
    [InlineData("{}", "")]
    public void Json_that_does_not_parse_is_lossy(string original, string reencoded) =>
        LosslessCheck.Json(Utf8(original), Utf8(reencoded)).Should().BeFalse();

    [Fact]
    public void A_number_written_with_a_fraction_is_the_same_number_on_every_target()
    {
        // net8.0 compares with JsonNode.DeepEquals and net10.0 with JsonElement.DeepEquals. Both agree here because the package graph brings System.Text.Json 10 to net8.0 too;
        // the 8.x library compares numbers as text, so a graph that fell back to it would make such a value lossy (never rewritten) there, which is the safe side.
        LosslessCheck.Json(Utf8("{\"a\":1.0}"), Utf8("{\"a\":1}")).Should().BeTrue();
        LosslessCheck.Json(Utf8("{\"a\":1.50}"), Utf8("{\"a\":1.5}")).Should().BeTrue();
        LosslessCheck.Json(Utf8("{\"a\":1e2}"), Utf8("{\"a\":100}")).Should().BeTrue();
        LosslessCheck.Json(Utf8("{\"a\":1.1}"), Utf8("{\"a\":1}")).Should().BeFalse();
    }

    [Fact]
    public void Headered_bytes_are_lossless_only_when_every_byte_is_the_same()
    {
        LosslessCheck.Headered([0x10, 1, 2, 3], [0x10, 1, 2, 3]).Should().BeTrue();
        LosslessCheck.Headered([0x10, 1, 2, 3], [0x10, 1, 2, 4]).Should().BeFalse();
        LosslessCheck.Headered([0x10, 1, 2, 3], [0x11, 1, 2, 3]).Should().BeFalse();
        LosslessCheck.Headered([0x10, 1, 2, 3], [0x10, 1, 2]).Should().BeFalse();
        LosslessCheck.Headered([0x10, 1], [0x10, 1, 2]).Should().BeFalse();
    }

    [Fact]
    public void A_json_value_is_rewritten_to_the_target_with_its_header()
    {
        var sut = Rewriter();
        var original = Utf8("\"hello\"");

        sut.TryReencode<string>(original, _fast, out var payload).Should().BeTrue();

        using (payload)
        {
            payload.Memory.ToArray().Should().Equal([0x10, .. Utf8("hello")]);
        }
    }

    [Fact]
    public void A_subset_dto_read_is_not_rewritten()
    {
        var sut = Rewriter();
        var original = Utf8("{\"id\":7,\"name\":\"n\",\"added_later\":true}");

        sut.TryReencode<Dto>(original, _fast, out var payload).Should().BeFalse();

        payload.Memory.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Property_order_and_number_form_are_not_lossy()
    {
        var sut = Rewriter();
        var original = Utf8("{\"name\":\"n\",\"value\":1.0}");

        sut.TryReencode<Measure>(original, _fast, out var payload).Should().BeTrue();

        using (payload)
        {
            payload.Memory.Span[0].Should().Be(0x10);
        }
    }

    [Fact]
    public void A_headered_value_reserialized_to_the_same_bytes_is_rewritten()
    {
        var sut = Rewriter();
        byte[] original = [0x10, .. Utf8("hi")];

        sut.TryReencode<string>(original, _other, out var payload).Should().BeTrue();

        using (payload)
        {
            payload.Memory.ToArray().Should().Equal([0x11, .. Utf8("hi")]);
        }
    }

    [Fact]
    public void A_headered_value_that_reserializes_differently_is_not_rewritten()
    {
        var sut = Rewriter();
        // The serializer that wrote it reads a member the type lacks and cannot write it back.
        byte[] original = [0x10, .. Utf8("{\"id\":7,\"name\":\"n\",\"extra\":true}")];

        sut.TryReencode<Dto>(original, _other, out _).Should().BeFalse();
    }

    [Fact]
    public void A_value_already_in_a_format_the_target_owns_is_not_rewritten()
    {
        var both = new PassthroughValueSerializer(0x10, 0x12);
        var sut = new ValueRewriter(new SystemJsonByteSerializerProxy(), new ValueSerializerRegistry([("both", both, false)]));
        byte[] original = [0x10, .. Utf8("hi")];

        sut.TryReencode<string>(original, both, out _).Should().BeFalse();
    }

    [Fact]
    public void A_header_no_serializer_owns_is_not_rewritten()
    {
        var sut = Rewriter();
        byte[] original = [0x14, .. Utf8("hi")];

        sut.TryReencode<string>(original, _fast, out _).Should().BeFalse();
    }

    [Fact]
    public void An_empty_payload_is_never_rewritten()
    {
        var sut = Rewriter();

        sut.TryReencode<string>([], _fast, out var payload).Should().BeFalse();

        payload.Memory.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void A_default_value_is_never_rewritten()
    {
        var sut = Rewriter();

        sut.TryReencode<string>(Utf8("null"), _fast, out _).Should().BeFalse();
        sut.TryReencode<int>(Utf8("0"), _fast, out _).Should().BeFalse();
        sut.TryReencode<int>(Utf8("1"), _fast, out var payload).Should().BeTrue();
        payload.Dispose();
    }

    [Fact]
    public void A_custom_json_proxy_is_not_known_to_write_json_so_its_values_are_not_rewritten()
    {
        var sut = new ValueRewriter(new SubclassedProxy(), new ValueSerializerRegistry([("fast", _fast, false)]));

        sut.TryReencode<string>(Utf8("\"x\""), _fast, out _).Should().BeFalse();
    }

    [Fact]
    public void A_headered_value_is_rewritten_whatever_the_json_proxy_is()
    {
        var sut = new ValueRewriter(new SubclassedProxy(), new ValueSerializerRegistry([("fast", _fast, false), ("other", _other, false)]));
        byte[] original = [0x10, .. Utf8("hi")];

        sut.TryReencode<string>(original, _other, out var payload).Should().BeTrue();
        payload.Dispose();
    }

    [Fact]
    public void A_target_that_throws_is_not_swallowed()
    {
        var bad = new ThrowingSerializer();
        var sut = new ValueRewriter(new SystemJsonByteSerializerProxy(), new ValueSerializerRegistry([("bad", bad, false)]));

        var act = () => sut.TryReencode<string>(Utf8("\"x\""), bad, out _);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void A_json_document_that_repeats_a_property_name_is_lossy_never_an_exception()
    {
        var sut = Rewriter();
        var original = Utf8("{\"id\":1,\"id\":2,\"name\":\"n\"}");

        var act = () => sut.TryReencode<Dto>(original, _fast, out _);

        act.Should().NotThrow();
        sut.TryReencode<Dto>(original, _fast, out _).Should().BeFalse("the type reads the last of them, and writes one");
        LosslessCheck.Json(original, Utf8("{\"id\":2,\"name\":\"n\"}")).Should().BeFalse();
    }

    [Fact]
    public void Bytes_that_do_not_read_as_the_type_are_lossy_not_an_error()
    {
        var sut = Rewriter();

        sut.TryReencode<Dto>(Utf8("[1,2]"), _fast, out _).Should().BeFalse();
        sut.TryReencode<Dto>(Utf8("{\"id\":\"not a number\"}"), _fast, out _).Should().BeFalse();
    }

    [Fact]
    public void A_target_that_cannot_carry_the_value_back_is_found_out_before_anything_is_written()
    {
        var forgetful = new ForgetfulSerializer();
        var sut = new ValueRewriter(new SystemJsonByteSerializerProxy(), new ValueSerializerRegistry([("forgetful", forgetful, false)]));

        sut.TryReencode<Dto>(Utf8("{\"id\":7,\"name\":\"n\"}"), forgetful, out var payload).Should().BeFalse();

        payload.Memory.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void A_payload_over_the_copy_limit_goes_back_to_the_pool_when_checking_it_throws()
    {
        using var pool = new RecordingPool();
        // The first check of the value passes, the one of what the target wrote back does not: the serializer that wrote the original rejects it.
        var source = new FailsOnTheSecondSerialize(0x10);
        var sut = new ValueRewriter(new SystemJsonByteSerializerProxy(), new ValueSerializerRegistry([("source", source, false), ("target", _other, false)]));
        byte[] original = [0x10, .. new string('a', 40_000).Select(c => (byte)c)];

        var act = () => sut.TryReencode<string>(original, _other, out _);

        act.Should().Throw<InvalidOperationException>();
        pool.RentedByThisThread.Should().NotBeEmpty("a payload that large keeps its rented array until it is disposed");
        // Counted, and only the large ones: the shared pool hands a returned array out again, so one never returned looks like one that was,
        // and the writer of this thread may return a small one it rented before the pool was swapped.
        pool.Returned.Count(array => array.Length > 16 * 1024).Should().Be(pool.RentedByThisThread.Count(array => array.Length > 16 * 1024), "every large array this call rented was given back");
    }

    [Fact]
    public void Only_the_default_json_proxy_can_have_its_json_compared()
    {
        new ValueRewriter(new SystemJsonByteSerializerProxy(), new ValueSerializerRegistry([("fast", _fast, false)])).ComparesJson.Should().BeTrue();
        new ValueRewriter(new SubclassedProxy(), new ValueSerializerRegistry([("fast", _fast, false)])).ComparesJson.Should().BeFalse();
    }

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private ValueRewriter Rewriter() =>
        new(new SystemJsonByteSerializerProxy(), new ValueSerializerRegistry([("fast", _fast, false), ("other", _other, false)]));

    private sealed class Dto
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }

    private sealed class FailsOnTheSecondSerialize(int id) : ICacheValueSerializer
    {
        private int _calls;

        public IReadOnlyList<int> FormatIds { get; } = [id];

        public int Serialize<T>(T value, IBufferWriter<byte> target)
        {
            if (Interlocked.Increment(ref _calls) > 1)
            {
                throw new InvalidOperationException("rejected");
            }

            target.Write(Encoding.UTF8.GetBytes(value?.ToString() ?? string.Empty));
            return id;
        }

        public T? Deserialize<T>(int formatId, ReadOnlySequence<byte> source) => (T)(object)Encoding.UTF8.GetString(source.ToArray());
    }

    private sealed class Measure
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("value")]
        public double Value { get; set; }
    }

    // Writes only the id, so what it reads back is not what it was given.
    private sealed class ForgetfulSerializer : ICacheValueSerializer
    {
        public IReadOnlyList<int> FormatIds { get; } = [0x15];

        public int Serialize<T>(T value, IBufferWriter<byte> target)
        {
            target.Write(Encoding.UTF8.GetBytes("{\"id\":7}"));
            return 0x15;
        }

        public T? Deserialize<T>(int formatId, ReadOnlySequence<byte> source) => JsonSerializer.Deserialize<T>(source.ToArray());
    }

    private sealed class SubclassedProxy : SystemJsonByteSerializerProxy
    {
    }

    private sealed class ThrowingSerializer : ICacheValueSerializer
    {
        public IReadOnlyList<int> FormatIds { get; } = [0x13];

        public int Serialize<T>(T value, IBufferWriter<byte> target) => throw new InvalidOperationException("boom");

        public T? Deserialize<T>(int formatId, ReadOnlySequence<byte> source) => throw new NotSupportedException();
    }
}
