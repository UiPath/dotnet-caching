using UiPath.Caching.Serialization;

namespace UiPath.Caching.Tests.Serialization;

public class ValueHeaderTests
{
    public static TheoryData<byte> JsonFirstBytes()
    {
        var bytes = new List<byte> { (byte)'{', (byte)'[', (byte)'"', (byte)'-', (byte)'t', (byte)'f', (byte)'n', 0x09, 0x0A, 0x0D, 0x20, 0x00, 0xEF };
        bytes.AddRange(Enumerable.Range('0', 10).Select(c => (byte)c));
        // Everything above the header range is headerless too: a UTF-8 lead byte, or text the default path never writes.
        bytes.AddRange([0x21, 0x7F, 0x80, 0xFF]);
        var data = new TheoryData<byte>();
        foreach (var b in bytes)
        {
            data.Add(b);
        }

        return data;
    }

    public static TheoryData<byte[]> DefaultProxyOutputs()
    {
        var proxy = new SystemJsonByteSerializerProxy();
        var data = new TheoryData<byte[]>();
        foreach (var value in new object?[] { "s", 42, -1, null, new byte[] { 2, 3 }, true, 1.5m })
        {
            data.Add(proxy.Serialize(value)!);
        }

        return data;
    }

    [Theory, MemberData(nameof(JsonFirstBytes))]
    public void Json_first_bytes_are_headerless(byte first)
    {
        ValueHeader.TryRead([first, 0x41, 0x42], out _, out _).Should().BeFalse();
    }

    [Theory, MemberData(nameof(DefaultProxyOutputs))]
    public void Default_proxy_output_is_headerless(byte[] payload)
    {
        payload.Should().NotBeEmpty();
        ValueHeader.TryRead(payload, out _, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(0x01), InlineData(0x08), InlineData(0x0B), InlineData(0x0C), InlineData(0x0E)]
    public void Every_header_byte_outside_the_json_range_is_a_header(int id)
    {
        ValueHeader.TryRead([(byte)id, 0x41], out var formatId, out var headerLength).Should().BeTrue();
        formatId.Should().Be(id);
        headerLength.Should().Be(1);
    }

    [Fact]
    public void A_payload_of_only_the_header_byte_reads_as_that_header()
    {
        ValueHeader.TryRead([0x10], out var formatId, out var headerLength).Should().BeTrue();

        formatId.Should().Be(0x10);
        headerLength.Should().Be(1);
    }

    [Theory, InlineData(0x02), InlineData(0x10), InlineData(0x1E)]
    public void One_byte_ids_round_trip(int id)
    {
        ValueHeader.Length(id).Should().Be(1);
        var buffer = new byte[3];

        ValueHeader.Write(buffer, id).Should().Be(1);
        buffer[0].Should().Be((byte)id);
        ValueHeader.TryRead(buffer, out var formatId, out var headerLength).Should().BeTrue();
        formatId.Should().Be(id);
        headerLength.Should().Be(1);
    }

    [Theory, InlineData(0x100), InlineData(0x1A5), InlineData(0x1FF)]
    public void Extended_ids_round_trip(int id)
    {
        ValueHeader.Length(id).Should().Be(2);
        var buffer = new byte[3];

        ValueHeader.Write(buffer, id).Should().Be(2);
        buffer[0].Should().Be(0x1F);
        buffer[1].Should().Be((byte)(id & 0xFF));
        ValueHeader.TryRead(buffer, out var formatId, out var headerLength).Should().BeTrue();
        formatId.Should().Be(id);
        headerLength.Should().Be(2);
    }

    [Fact]
    public void Extended_marker_without_id_byte_is_unreadable()
    {
        ValueHeader.TryRead([0x1F], out var formatId, out _).Should().BeTrue();
        formatId.Should().Be(-1);
    }

    [Fact]
    public void Empty_value_is_not_a_header()
    {
        ValueHeader.TryRead([], out _, out var headerLength).Should().BeFalse();
        headerLength.Should().Be(0);
    }

    [Theory, InlineData(0), InlineData(0x09), InlineData(0x0A), InlineData(0x0D), InlineData(0x1F), InlineData(0x20), InlineData(0x0FF), InlineData(0x200), InlineData(-1)]
    public void An_id_that_would_read_back_as_json_or_not_at_all_is_not_written(int id)
    {
        var act = () => ValueHeader.Length(id);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
