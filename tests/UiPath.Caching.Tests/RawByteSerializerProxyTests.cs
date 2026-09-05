using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace UiPath.Caching.Tests;

public class RawByteSerializerProxyTests
{
    private readonly RawByteSerializerProxy _proxy = new();

    private sealed record Poco(string Name, int Count);

    [Fact]
    public void Byte_array_passes_through_by_reference()
    {
        var payload = new byte[] { 0x00, 0x01, 0xFF };
        _proxy.Serialize(payload).Should().BeSameAs(payload);
        _proxy.Deserialize<byte[]>(payload).Should().BeSameAs(payload);
    }

    [Fact]
    public void ReadOnlyMemory_is_materialized_not_json_encoded()
    {
        ReadOnlyMemory<byte> memory = new byte[] { 1, 2, 3 };
        _proxy.Serialize(memory).Should().Equal(1, 2, 3);
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
        var empty = Array.Empty<byte>();
        _proxy.Serialize(empty).Should().BeSameAs(empty);
        _proxy.Deserialize<byte[]>(empty).Should().BeSameAs(empty);
    }

    [Fact]
    public void Null_falls_through_to_json_and_empty_reads_as_default()
    {
        _proxy.Serialize(null).Should().Equal("null"u8.ToArray());
        _proxy.Deserialize<Poco>(null).Should().BeNull();
        _proxy.Deserialize<Poco>([]).Should().BeNull();
    }

    [Fact]
    public void Poco_round_trips_as_utf8_json()
    {
        var value = new Poco("x", 42);
        var bytes = _proxy.Serialize(value)!;
        JsonSerializer.Deserialize<Poco>(bytes).Should().Be(value);
        _proxy.Deserialize<Poco>(bytes).Should().Be(value);
    }

    [Fact]
    public void Deserializing_json_as_bytes_returns_raw_utf8()
    {
        var bytes = _proxy.Serialize(new Poco("x", 1))!;
        _proxy.Deserialize<byte[]>(bytes).Should().BeSameAs(bytes);
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
        _proxy.TryDeserialize<byte[]>(raw, out var bytes).Should().BeTrue();
        bytes.Should().BeSameAs(raw);

        var element = JsonSerializer.SerializeToElement(new Poco("x", 1));
        _proxy.TryDeserialize<Poco>(element, out var fromElement).Should().BeTrue();
        fromElement.Should().Be(new Poco("x", 1));

        _proxy.TryDeserialize<Poco>((object)"""{"Name":"x","Count":1}""", out var fromText).Should().BeTrue();
        fromText.Should().Be(new Poco("x", 1));

        _proxy.TryDeserialize<Poco>(null, out _).Should().BeFalse();
    }

    /// <summary>The point of the memory seam: the caller's memory is returned, not a copy of it — offset and length included.</summary>
    [Fact]
    public void SerializeToMemory_returns_the_callers_memory_itself()
    {
        var backing = new byte[] { 0, 1, 2, 3, 4, 5 };
        ReadOnlyMemory<byte> window = backing.AsMemory(2, 3);

        var memory = _proxy.SerializeToMemory(window);

        MemoryMarshal.TryGetArray(memory, out var segment).Should().BeTrue();
        segment.Array.Should().BeSameAs(backing);
        segment.Offset.Should().Be(2);
        segment.Count.Should().Be(3);
    }

    [Fact]
    public void SerializeToMemory_passes_mutable_memory_and_arrays_through_as_well()
    {
        var backing = new byte[] { 9, 8, 7 };

        MemoryMarshal.TryGetArray(_proxy.SerializeToMemory<Memory<byte>>(backing), out var fromMemory).Should().BeTrue();
        fromMemory.Array.Should().BeSameAs(backing);

        MemoryMarshal.TryGetArray(_proxy.SerializeToMemory(backing), out var fromArray).Should().BeTrue();
        fromArray.Array.Should().BeSameAs(backing);
    }

    [Fact]
    public void SerializeToMemory_falls_back_to_json_for_everything_else()
    {
        var value = new Poco("x", 42);

        _proxy.SerializeToMemory(value).ToArray().Should().Equal(_proxy.Serialize(value));
        _proxy.SerializeToMemory<Poco>(null).ToArray().Should().Equal("null"u8.ToArray());
    }

    [Fact]
    public void Honors_custom_serializer_options()
    {
        var proxy = new RawByteSerializerProxy(new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Encoding.UTF8.GetString(proxy.Serialize(new Poco("x", 1))!).Should().Contain("\"name\"");
    }

    /// <summary><c>byte[]</c> is an object, so a naive "value is T" passthrough would hand back the raw bytes.</summary>
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

        stored.Should().Equal(4, 5, 6);
        _proxy.Deserialize<Memory<byte>>(stored).ToArray().Should().Equal(4, 5, 6);
    }
}
