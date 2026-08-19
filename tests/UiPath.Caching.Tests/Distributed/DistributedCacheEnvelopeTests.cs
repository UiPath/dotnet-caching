using UiPath.Caching.Distributed;

namespace UiPath.Caching.Tests.Distributed;

public class DistributedCacheEnvelopeTests
{
    private static readonly byte[] Payload = [0x00, 0x10, 0xFF, 0x7A];

    public static TheoryData<long?, DateTimeOffset?> ExpirationCombinations => new()
    {
        { null, null },
        { TimeSpan.FromMinutes(20).Ticks, null },
        { null, new DateTimeOffset(2026, 8, 13, 12, 0, 0, TimeSpan.Zero) },
        { TimeSpan.FromMinutes(20).Ticks, new DateTimeOffset(2026, 8, 13, 12, 0, 0, TimeSpan.Zero) },
    };

    [Theory]
    [MemberData(nameof(ExpirationCombinations))]
    public void Round_trips_all_expiration_combinations(long? sliding, DateTimeOffset? absolute)
    {
        var encoded = new DistributedCacheEnvelope(Payload, sliding, absolute).Encode();
        var decoded = DistributedCacheEnvelope.TryDecode(encoded)!;

        decoded.Should().NotBeNull();
        decoded.Data.Should().Equal(Payload);
        decoded.SlidingTicks.Should().Be(sliding);
        decoded.AbsoluteExpiration.Should().Be(absolute);
    }

    [Fact]
    public void Empty_payload_round_trips()
    {
        var decoded = DistributedCacheEnvelope.TryDecode(new DistributedCacheEnvelope([], null, null).Encode())!;
        decoded.Data.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0x01, 0x02 })]
    [InlineData(new byte[] { (byte)'X', (byte)'P', (byte)'D', (byte)'C', 1, 0 })]
    [InlineData(new byte[] { (byte)'U', (byte)'P', (byte)'D', (byte)'C', 99, 0 })]
    [InlineData(new byte[] { (byte)'U', (byte)'P', (byte)'D', (byte)'C', 1, 1 })]
    public void Foreign_or_corrupt_values_decode_to_null(byte[]? value)
    {
        DistributedCacheEnvelope.TryDecode(value).Should().BeNull();
    }

    [Fact]
    public void Json_payload_is_not_mistaken_for_an_envelope()
    {
        DistributedCacheEnvelope.TryDecode("""{"Name":"x"}"""u8.ToArray()).Should().BeNull();
    }
}
