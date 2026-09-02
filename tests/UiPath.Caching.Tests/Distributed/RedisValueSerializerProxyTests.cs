using StackExchange.Redis;
using UiPath.Caching.Distributed;

namespace UiPath.Caching.Tests.Distributed;

public class RedisValueSerializerProxyTests
{
    private sealed record Poco(string Name);

    private readonly RedisValueSerializerProxy _proxy = new(new SystemJsonByteSerializerProxy());

    [Fact]
    public void Bytes_round_trip_unencoded()
    {
        var payload = new byte[] { 0x00, 0x01, 0xFF };
        RedisValue stored = _proxy.Serialize(payload);
        ((byte[])stored!).Should().Equal(payload);
        _proxy.Deserialize<byte[]>(stored).Should().Equal(payload);
    }

    [Fact]
    public void Null_and_empty_map_to_defaults()
    {
        _proxy.Serialize(null).IsNull.Should().BeTrue();
        _proxy.Deserialize<Poco>(RedisValue.Null).Should().BeNull();
        _proxy.Deserialize<Poco>(RedisValue.EmptyString).Should().BeNull();
    }

    [Fact]
    public void Poco_round_trips_via_inner_json()
    {
        var value = new Poco("x");
        var stored = _proxy.Serialize(value);
        _proxy.Deserialize<Poco>(stored).Should().Be(value);
    }

    [Fact]
    public void TryDeserialize_delegates_to_inner()
    {
        _proxy.TryDeserialize<Poco>("""{"Name":"x"}""", out var ok).Should().BeTrue();
        ok.Should().Be(new Poco("x"));
        _proxy.TryDeserialize<Poco>("not json", out _).Should().BeFalse();
    }
}
