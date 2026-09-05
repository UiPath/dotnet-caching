using System.Text;
using System.Text.Json;

namespace UiPath.Caching.Tests;

public class SystemJsonByteSerializerProxyTests
{
    private readonly SystemJsonByteSerializerProxy _proxy = new();

    private sealed record Poco(string Name, int Count);

    [Fact]
    public void Byte_array_is_base64_encoded_inside_json()
    {
        var payload = new byte[] { 0x01, 0x02, 0x03 };

        var stored = _proxy.Serialize(payload)!;

        Encoding.UTF8.GetString(stored).Should().Be("\"AQID\"");
        stored.Should().NotBeSameAs(payload);
    }

    [Fact]
    public void Byte_array_round_trips_through_base64()
    {
        var payload = new byte[] { 0x00, 0x01, 0xFF };

        _proxy.Deserialize<byte[]>(_proxy.Serialize(payload)).Should().Equal(payload);
    }

    /// <summary>
    /// The memory seam does not change what this serializer writes: memory in is still base64 inside JSON out,
    /// so a tier asking for memory gets the same bytes it would have got as an array — never the caller's buffer.
    /// </summary>
    [Fact]
    public void SerializeToMemory_is_the_json_bytes_not_the_callers_memory()
    {
        ReadOnlyMemory<byte> payload = new byte[] { 0x01, 0x02, 0x03 };

        var memory = _proxy.SerializeToMemory(payload);

        Encoding.UTF8.GetString(memory.Span).Should().Be("\"AQID\"");
        memory.ToArray().Should().Equal(_proxy.Serialize(payload));
    }

    [Fact]
    public void ReadOnlyMemory_round_trips()
    {
        ReadOnlyMemory<byte> memory = new byte[] { 1, 2, 3 };

        var stored = _proxy.Serialize(memory);

        _proxy.Deserialize<ReadOnlyMemory<byte>>(stored).ToArray().Should().Equal(1, 2, 3);
    }

    [Fact]
    public void Empty_byte_array_round_trips()
    {
        var stored = _proxy.Serialize(Array.Empty<byte>())!;

        Encoding.UTF8.GetString(stored).Should().Be("\"\"");
        _proxy.Deserialize<byte[]>(stored).Should().BeEmpty();
    }

    /// <summary>
    /// Must stay non-null: a null payload reaches StackExchange.Redis as <c>RedisValue.Null</c>,
    /// which throws on SADD and on the multi-field HSET, and stores nothing on the single-value paths.
    /// </summary>
    [Fact]
    public void Null_serializes_to_the_json_null_literal_never_to_a_null_payload()
    {
        var stored = _proxy.Serialize(null);

        stored.Should().NotBeNull();
        Encoding.UTF8.GetString(stored!).Should().Be("null");
    }

    [Fact]
    public void Null_empty_and_the_json_null_literal_all_read_back_as_default()
    {
        _proxy.Deserialize<Poco>(null).Should().BeNull();
        _proxy.Deserialize<Poco>([]).Should().BeNull();
        _proxy.Deserialize<Poco>(Encoding.UTF8.GetBytes("null")).Should().BeNull();
    }

    [Fact]
    public void Poco_round_trips_as_utf8_json()
    {
        var value = new Poco("x", 42);

        var bytes = _proxy.Serialize(value)!;

        JsonSerializer.Deserialize<Poco>(bytes).Should().Be(value);
        _proxy.Deserialize<Poco>(bytes).Should().Be(value);
    }

    /// <summary>Unlike <see cref="RawByteSerializerProxy"/>, reading JSON as bytes parses it rather than passing it through.</summary>
    [Fact]
    public void Deserializing_json_as_bytes_decodes_rather_than_passing_through()
    {
        var stored = _proxy.Serialize(new byte[] { 7, 8 })!;

        _proxy.Deserialize<byte[]>(stored).Should().Equal(7, 8).And.NotEqual(stored);
    }

    [Fact]
    public void TryDeserialize_string_success_and_failure()
    {
        _proxy.TryDeserialize<Poco>("""{"Name":"x","Count":1}""", out var ok).Should().BeTrue();
        ok.Should().Be(new Poco("x", 1));
        _proxy.TryDeserialize<Poco>("not json", out var bad).Should().BeFalse();
        bad.Should().BeNull();
        _proxy.TryDeserialize<Poco>("   ", out _).Should().BeFalse();
    }

    [Fact]
    public void TryDeserialize_object_handles_bytes_json_element_and_text()
    {
        var raw = Encoding.UTF8.GetBytes("""{"Name":"x","Count":1}""");
        _proxy.TryDeserialize<Poco>(raw, out var fromBytes).Should().BeTrue();
        fromBytes.Should().Be(new Poco("x", 1));

        var element = JsonSerializer.SerializeToElement(new Poco("x", 1));
        _proxy.TryDeserialize<Poco>(element, out var fromElement).Should().BeTrue();
        fromElement.Should().Be(new Poco("x", 1));

        _proxy.TryDeserialize<Poco>((object)"""{"Name":"x","Count":1}""", out var fromText).Should().BeTrue();
        fromText.Should().Be(new Poco("x", 1));

        _proxy.TryDeserialize<Poco>(null, out _).Should().BeFalse();
    }

    [Fact]
    public void Honors_custom_serializer_options()
    {
        var proxy = new SystemJsonByteSerializerProxy(new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        Encoding.UTF8.GetString(proxy.Serialize(new Poco("x", 1))!).Should().Contain("\"name\"");
    }

    [Fact]
    public void Deserializing_as_object_parses_json_instead_of_returning_bytes()
    {
        var bytes = _proxy.Serialize(new Poco("x", 1))!;

        _proxy.Deserialize<object>(bytes).Should().NotBeOfType<byte[]>();
    }

    [Fact]
    public void TryDeserialize_object_round_trips_its_own_output()
    {
        var value = new Poco("x", 1);
        var bytes = _proxy.Serialize(value)!;

        _proxy.TryDeserialize<Poco>((object)bytes, out var result).Should().BeTrue();
        result.Should().Be(value);
    }

    /// <summary>A valid JSON null is a successful deserialization on every input shape; only an empty buffer is a failure.</summary>
    [Fact]
    public void TryDeserialize_object_treats_json_null_as_success_on_every_path()
    {
        var jsonNull = Encoding.UTF8.GetBytes("null");

        _proxy.TryDeserialize<Poco>((object)jsonNull, out var fromBytes).Should().BeTrue();
        fromBytes.Should().BeNull();

        _proxy.TryDeserialize<Poco>((object)"null", out var fromText).Should().BeTrue();
        fromText.Should().BeNull();

        _proxy.TryDeserialize<Poco>(JsonSerializer.SerializeToElement<Poco?>(null), out var fromElement).Should().BeTrue();
        fromElement.Should().BeNull();

        _proxy.TryDeserialize<Poco>((object)Array.Empty<byte>(), out _).Should().BeFalse();
    }

    [Fact]
    public void Memory_of_byte_round_trips()
    {
        Memory<byte> memory = new byte[] { 4, 5, 6 };

        var stored = _proxy.Serialize(memory)!;

        _proxy.Deserialize<Memory<byte>>(stored).ToArray().Should().Equal(4, 5, 6);
    }
}
