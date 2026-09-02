using System.Text.Json;
using StackExchange.Redis;

namespace UiPath.Caching.Tests;

/// <summary>
/// Entries written by 1.x's <c>SystemJsonSerializerProxy</c> must read back unchanged through the
/// default. This is what makes the seam change source-only rather than a data migration.
/// </summary>
public class LegacySerializerWireCompatTests
{
    private sealed record Sample(int Id, string Name, int[] Values);

    private readonly SystemJsonByteSerializerProxy _proxy = new();

    /// <summary>The 1.x proxy's own output: <c>JsonSerializer.SerializeToUtf8Bytes</c>.</summary>
    private static byte[] WrittenByLegacyProxy(object? value) =>
        JsonSerializer.SerializeToUtf8Bytes(value);

    /// <summary>A custom 1.x serializer returning a string-backed <c>RedisValue</c>, as the docs blessed.</summary>
    private static byte[] WrittenByLegacyStringPath(object? value) =>
        ((byte[]?)(RedisValue)JsonSerializer.Serialize(value))!;

    [Fact]
    public void Poco_written_by_the_legacy_proxy_still_reads()
    {
        var original = new Sample(42, "héllo 世界", [1, 2, 3]);

        _proxy.Deserialize<Sample>(WrittenByLegacyProxy(original)).Should().BeEquivalentTo(original);
    }

    [Fact]
    public void Poco_written_through_the_legacy_string_path_still_reads()
    {
        var original = new Sample(7, "legacy", [9]);

        _proxy.Deserialize<Sample>(WrittenByLegacyStringPath(original)).Should().BeEquivalentTo(original);
    }

    [Fact]
    public void Both_serializers_produce_identical_bytes_for_a_poco()
    {
        var value = new Sample(1, "x", []);

        _proxy.Serialize(value).Should().Equal(WrittenByLegacyProxy(value));
    }

    [Theory]
    [InlineData("plain string")]
    [InlineData(42)]
    [InlineData(true)]
    [InlineData(1.5)]
    public void Scalars_are_byte_identical_across_both_serializers(object value)
    {
        _proxy.Serialize(value).Should().Equal(WrittenByLegacyProxy(value));
    }

    [Fact]
    public void Null_is_byte_identical_to_the_legacy_proxy_and_reads_back_as_default()
    {
        _proxy.Serialize(null).Should().Equal(WrittenByLegacyProxy(null)).And.Equal("null"u8.ToArray());

        _proxy.Deserialize<Sample>(WrittenByLegacyProxy(null)).Should().BeNull();
    }

    [Fact]
    public void Null_and_empty_read_back_as_default()
    {
        _proxy.Deserialize<Sample>(null).Should().BeNull();
        _proxy.Deserialize<Sample>([]).Should().BeNull();
    }

    [Fact]
    public void Byte_payloads_written_by_the_legacy_proxy_still_round_trip()
    {
        byte[] original = [1, 2, 3];
        var legacy = WrittenByLegacyProxy(original);

        legacy.Should().Equal("\"AQID\""u8.ToArray(), "1.x base64-encoded byte[] inside JSON");

        _proxy.Deserialize<byte[]>(legacy).Should().Equal(original);
        _proxy.Serialize(original).Should().Equal(legacy);
    }

    /// <summary>Why raw passthrough is not the default: no throw, so nothing degrades it to a miss.</summary>
    [Fact]
    public void The_raw_proxy_would_misread_a_legacy_byte_payload_silently()
    {
        byte[] original = [1, 2, 3];
        var legacy = WrittenByLegacyProxy(original);

        new RawByteSerializerProxy().Deserialize<byte[]>(legacy)
            .Should().Equal(legacy).And.NotEqual(original);
    }

    /// <summary>The same mismatch on a POCO does throw, which the caches catch and report as a miss.</summary>
    [Fact]
    public void A_payload_that_is_not_json_surfaces_as_an_exception_for_typed_reads()
    {
        byte[] raw = [0xFF, 0xFE, 0xFD];

        var act = () => _proxy.Deserialize<Sample>(raw);

        act.Should().Throw<JsonException>();
    }
}
