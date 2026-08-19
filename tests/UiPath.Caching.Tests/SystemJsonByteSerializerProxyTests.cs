using System.Text;
using System.Text.Json;

namespace UiPath.Caching.Tests;

public class SystemJsonByteSerializerProxyTests
{
    private readonly SystemJsonByteSerializerProxy _proxy = new();

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
    public void Null_maps_to_null_and_empty_to_default()
    {
        _proxy.Serialize(null).Should().BeNull();
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

    [Fact]
    public void Honors_custom_serializer_options()
    {
        var proxy = new SystemJsonByteSerializerProxy(new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Encoding.UTF8.GetString(proxy.Serialize(new Poco("x", 1))!).Should().Contain("\"name\"");
    }
}
