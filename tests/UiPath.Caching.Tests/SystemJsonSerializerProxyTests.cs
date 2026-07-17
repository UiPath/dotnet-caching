using System.Text.Json;
using StackExchange.Redis;

namespace UiPath.Caching.Tests;

public class SystemJsonSerializerProxyTests
{
    private sealed record Sample(int Id, string Name, int[] Values);

    private readonly SystemJsonSerializerProxy _proxy = new();

    [Fact]
    public void RoundTrips_complex_object()
    {
        var original = new Sample(42, "héllo 世界", [1, 2, 3]);

        var result = _proxy.Deserialize<Sample>(_proxy.Serialize(original));

        result.Should().BeEquivalentTo(original);
    }

    [Fact]
    public void Serialize_produces_utf8_json_on_the_wire()
    {
        var value = new Sample(1, "x", []);

        ((byte[])_proxy.Serialize(value)!).Should().Equal(JsonSerializer.SerializeToUtf8Bytes(value));
    }

    [Fact]
    public void Deserializes_value_written_by_the_legacy_string_path()
    {
        RedisValue legacy = JsonSerializer.Serialize(new Sample(7, "legacy", [9]));

        _proxy.Deserialize<Sample>(legacy).Should().BeEquivalentTo(new Sample(7, "legacy", [9]));
    }

    [Fact]
    public void Null_and_empty_return_default()
    {
        _proxy.Deserialize<Sample>(RedisValue.Null).Should().BeNull();
        _proxy.Deserialize<Sample>(RedisValue.EmptyString).Should().BeNull();
    }
}
