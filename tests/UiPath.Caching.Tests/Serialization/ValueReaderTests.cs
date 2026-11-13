using System.Text;
using System.Text.Json;
using UiPath.Caching.Config;
using UiPath.Caching.Serialization;
using UiPath.Caching.Tests.Fakes;

namespace UiPath.Caching.Tests.Serialization;

public class ValueReaderTests
{
    private static readonly CachePolicy NamesASerializer = new() { Serializer = new SerializerPolicy { Write = "fast" } };

    public static TheoryData<string, string> JsonValues() => new()
    {
        { "\"text\"", "string" },
        { "42", "number" },
        { "null", "null literal" },
        { "  {\"a\":1}", "space-led object" },
        { "\n[1]", "newline-led array" },
        { "\r\n\"x\"", "return-led string" },
        { "\t7", "tab-led number" },
    };

    public static TheoryData<string, bool, bool> Sniffing() => new()
    {
        { "exact json proxy, no policy", false, true },
        { "exact json proxy, policy names a serializer", true, true },
        { "subclass, no policy", false, false },
        { "subclass, policy names a serializer", true, true },
        { "raw proxy, no policy", false, false },
        { "raw proxy, policy names a serializer", true, true },
        { "custom proxy, no policy", false, false },
        { "custom proxy, policy names a serializer", true, true },
    };

    [Fact]
    public void A_header_picks_the_serializer_and_is_sliced_off()
    {
        var fast = new PassthroughValueSerializer(0x10, 0x11);
        var sut = Reader(registry: Registry(("fast", fast)));

        var outcome = sut.Read<byte[]>([0x11, 1, 2, 3]);

        outcome.IsUnreadable.Should().BeFalse();
        outcome.Value.Should().Equal(1, 2, 3);
        fast.LastFormatId.Should().Be(0x11);
    }

    [Fact]
    public void A_headered_value_reads_through_the_serializer_that_owns_its_id()
    {
        var first = new PassthroughValueSerializer(0x10);
        var second = new PassthroughValueSerializer(0x12);
        var sut = Reader(registry: Registry(("first", first), ("second", second)));

        sut.Read<string>([0x12, (byte)'o', (byte)'k']).Value.Should().Be("ok");

        first.Deserialized.Should().Be(0);
        second.Deserialized.Should().Be(1);
    }

    [Fact]
    public void An_extended_id_is_the_marker_plus_one_byte()
    {
        var fast = new PassthroughValueSerializer(0x105);
        var sut = Reader(registry: Registry(("fast", fast)));

        var outcome = sut.Read<byte[]>([0x1F, 0x05, 9, 8]);

        outcome.Value.Should().Equal(9, 8);
        fast.LastFormatId.Should().Be(0x105);
    }

    [Fact]
    public void An_unregistered_id_is_unreadable_and_names_the_id()
    {
        var fast = new PassthroughValueSerializer(0x10);
        var sut = Reader(registry: Registry(("fast", fast)));

        var outcome = sut.Read<string>([0x11, 1]);

        outcome.IsUnreadable.Should().BeTrue();
        outcome.UnreadableFormat.Should().Be(0x11);
        outcome.Value.Should().BeNull();
        fast.Deserialized.Should().Be(0);
    }

    [Fact]
    public void A_serializer_that_throws_is_a_miss_with_its_failure_and_not_an_unreadable_format()
    {
        var sut = Reader(registry: Registry(("bad", new ThrowingValueSerializer())));

        var outcome = sut.Read<string>([0x10, 1, 2]);

        outcome.IsUnreadable.Should().BeFalse();
        outcome.IsMiss.Should().BeTrue();
        outcome.UnreadableFormat.Should().Be(0);
        outcome.Failure.Should().BeOfType<InvalidDataException>();
        outcome.Value.Should().BeNull();
    }

    [Theory]
    [InlineData(typeof(ObjectDisposedException))]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(NotSupportedException))]
    public void Whatever_a_known_serializer_throws_is_a_miss_that_keeps_the_key(Type failure)
    {
        var sut = Reader(registry: Registry(("bad", new ThrowingValueSerializer { Failure = (Exception)Activator.CreateInstance(failure, "x")! })));

        var outcome = sut.Read<string>([0x10, 1, 2]);

        outcome.IsUnreadable.Should().BeFalse();
        outcome.Failure.Should().BeOfType(failure);
    }

    [Fact]
    public void An_out_of_memory_failure_is_never_swallowed()
    {
        var sut = Reader(registry: Registry(("bad", new ThrowingValueSerializer { Failure = new OutOfMemoryException() })));

        var act = () => sut.Read<string>([0x10, 1, 2]);

        act.Should().Throw<OutOfMemoryException>();
    }

    [Fact]
    public void An_out_of_memory_from_headerless_json_is_a_miss_as_with_no_registry()
    {
        var sut = Reader(new OutOfMemoryProxy());

        var outcome = sut.Read<string>([0x22, 0x61, 0x22]);

        outcome.Failure.Should().BeOfType<OutOfMemoryException>();
        outcome.IsMiss.Should().BeTrue();
        outcome.IsUnreadable.Should().BeFalse();
    }

    [Fact]
    public void A_header_with_no_body_goes_to_the_serializer_and_fails_with_it()
    {
        var sut = Reader(registry: Registry(("bad", new ThrowingValueSerializer())));

        var outcome = sut.Read<string>([0x10]);

        outcome.IsUnreadable.Should().BeFalse();
        outcome.Failure.Should().NotBeNull();
    }

    [Fact]
    public void Json_that_does_not_parse_still_throws()
    {
        var sut = Reader();

        var act = () => sut.Read<string>(Encoding.UTF8.GetBytes("{not json"));

        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void An_unknown_id_has_no_failure()
    {
        Reader().Read<string>([0x1E, 1]).Failure.Should().BeNull();
    }

    [Fact]
    public void An_unregistered_extended_id_is_unreadable()
    {
        var sut = Reader(registry: Registry(("fast", new PassthroughValueSerializer(0x105))));

        sut.Read<string>([0x1F, 0x06, 1]).UnreadableFormat.Should().Be(0x106);
    }

    [Fact]
    public void A_lone_extended_marker_is_unreadable()
    {
        var sut = Reader();

        var outcome = sut.Read<string>([0x1F]);

        outcome.IsUnreadable.Should().BeTrue();
        outcome.UnreadableFormat.Should().Be(-1);
    }

    [Theory]
    [MemberData(nameof(JsonValues))]
    public void Headerless_bytes_read_as_json_even_when_serializers_are_registered(string json, string because)
    {
        var fast = new PassthroughValueSerializer();
        var sut = Reader(registry: Registry(("fast", fast)));

        var outcome = sut.Read<JsonElement>(Encoding.UTF8.GetBytes(json));

        outcome.IsUnreadable.Should().BeFalse(because);
        outcome.Value.GetRawText().Should().Be(JsonDocument.Parse(json).RootElement.GetRawText(), because);
        fast.Deserialized.Should().Be(0, because);
    }

    [Fact]
    public void A_base64_byte_array_written_by_the_default_proxy_reads_back()
    {
        var proxy = new SystemJsonByteSerializerProxy();
        var original = new byte[] { 2, 3, 0xFF, 0x10 };
        var sut = Reader(proxy);

        sut.Read<byte[]>(proxy.Serialize(original)!).Value.Should().Equal(original);
    }

    [Fact]
    public void A_cached_null_literal_reads_as_null()
    {
        var proxy = new SystemJsonByteSerializerProxy();

        Reader(proxy).Read<string>(proxy.Serialize(null)!).Value.Should().BeNull();
    }

    [Theory]
    [MemberData(nameof(Sniffing))]
    public void Sniffing_is_gated_by_the_proxy_and_the_policy(string scenario, bool withPolicy, bool expected)
    {
        ISerializerProxy<byte[]> proxy = scenario.Split(',')[0] switch
        {
            "exact json proxy" => new SystemJsonByteSerializerProxy(),
            "subclass" => new JsonSubclass(),
            "raw proxy" => new RawByteSerializerProxy(),
            _ => new CustomProxy(),
        };

        Reader(proxy).SniffsFor(withPolicy ? NamesASerializer : null).Should().Be(expected, scenario);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("JSON")]
    [InlineData("Json")]
    public void A_json_policy_does_not_open_the_gate_for_a_raw_proxy(string write)
    {
        var jsonPolicy = new CachePolicy { Serializer = new SerializerPolicy { Write = write } };

        Reader(new RawByteSerializerProxy()).SniffsFor(jsonPolicy).Should().BeFalse();
        Reader(new CustomProxy()).SniffsFor(jsonPolicy).Should().BeFalse();
        Reader(new JsonSubclass()).SniffsFor(jsonPolicy).Should().BeFalse();
    }

    [Fact]
    public void A_json_policy_that_lists_other_formats_to_read_does_not_open_the_gate_for_a_raw_proxy()
    {
        var migratingBack = new CachePolicy { Serializer = new SerializerPolicy { Write = "json", Read = ["json", "fast"] } };

        Reader(new RawByteSerializerProxy()).SniffsFor(migratingBack).Should().BeFalse();
    }

    [Theory]
    [InlineData("json")]
    [InlineData("fast")]
    public void The_default_json_proxy_sniffs_whatever_the_policy_writes(string write)
    {
        var policy = new CachePolicy { Serializer = new SerializerPolicy { Write = write, Read = ["json", "fast"] } };

        Reader(new SystemJsonByteSerializerProxy()).SniffsFor(policy).Should().BeTrue();
    }

    [Fact]
    public void A_resolved_write_serializer_opens_the_gate_for_a_raw_proxy()
    {
        var resolved = CachePolicyMerger.WithWriteSerializer(new CachePolicy(), new PassthroughValueSerializer());

        Reader(new RawByteSerializerProxy()).SniffsFor(resolved).Should().BeTrue();
    }

    [Fact]
    public void A_policy_without_a_write_serializer_does_not_open_the_gate()
    {
        var readsOnly = new CachePolicy { Serializer = new SerializerPolicy { Read = ["fast"] } };

        Reader(new RawByteSerializerProxy()).SniffsFor(readsOnly).Should().BeFalse();
    }

    [Fact]
    public void An_empty_registry_never_sniffs()
    {
        var sut = Reader(registry: ValueSerializerRegistry.Empty);

        sut.SniffsFor(null).Should().BeFalse();
        sut.SniffsFor(NamesASerializer).Should().BeFalse();
    }

    private static ValueSerializerRegistry Registry(params (string Name, ICacheValueSerializer Serializer)[] entries) =>
        new(entries.Select(e => (e.Name, e.Serializer, false)));

    private static ValueReader Reader(ISerializerProxy<byte[]>? json = null, ValueSerializerRegistry? registry = null) =>
        new(json ?? new SystemJsonByteSerializerProxy(), registry ?? Registry(("fast", new PassthroughValueSerializer())));

    private sealed class CustomProxy : ISerializerProxy<byte[]>
    {
        public byte[]? Serialize(object? value) => throw new NotSupportedException();

        public T? Deserialize<T>(byte[]? value) => throw new NotSupportedException();

        public bool TryDeserialize<T>(string? value, out T? result) => throw new NotSupportedException();

        public bool TryDeserialize<T>(object? value, out T? result) => throw new NotSupportedException();
    }

    private sealed class OutOfMemoryProxy : ISerializerProxy<byte[]>
    {
        public byte[]? Serialize(object? value) => throw new NotSupportedException();

        public T? Deserialize<T>(byte[]? value) => throw new OutOfMemoryException();

        public bool TryDeserialize<T>(string? value, out T? result) => throw new NotSupportedException();

        public bool TryDeserialize<T>(object? value, out T? result) => throw new NotSupportedException();
    }

    private sealed class JsonSubclass : SystemJsonByteSerializerProxy;
}
