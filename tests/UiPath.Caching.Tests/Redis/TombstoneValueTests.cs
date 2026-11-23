using StackExchange.Redis;
using UiPath.Caching.Redis;
using UiPath.Caching.Serialization;

namespace UiPath.Caching.Tests.Redis;

public class TombstoneValueTests
{
    [Theory]
    [InlineData(1L)]
    [InlineData(30_000L)]
    [InlineData(long.MaxValue)]
    public void A_tombstone_reads_back_its_grace(long grace)
    {
        var bytes = TombstoneValue.Create(grace);

        bytes[0].Should().Be(0x0F);
        TombstoneValue.TryRead(bytes, out var read).Should().BeTrue();
        read.Should().Be(grace);
        TombstoneValue.Is(bytes).Should().BeTrue();
    }

    [Fact]
    public void Two_tombstones_of_one_grace_are_never_equal()
    {
        var bytes = Enumerable.Range(0, 50).Select(_ => Convert.ToHexString(TombstoneValue.Create(30_000))).ToArray();

        bytes.Distinct().Should().HaveCount(bytes.Length, "each carries its own nonce");
        TombstoneValue.Create(30_000, 7).Should().Equal(TombstoneValue.Create(30_000, 7));
        TombstoneValue.Create(30_000, 7).Should().NotEqual(TombstoneValue.Create(30_000, 8));
    }

    [Theory]
    [InlineData("\"text\"")]
    [InlineData("{\"a\":1}")]
    [InlineData("")]
    [InlineData("30000")]
    public void A_value_that_does_not_start_with_the_reserved_byte_is_no_tombstone(string value)
    {
        TombstoneValue.Is(value).Should().BeFalse();
        TombstoneValue.Is(RedisValue.Null).Should().BeFalse();
    }

    [Theory]
    [InlineData(new byte[] { 0x0F })]
    [InlineData(new byte[] { 0x0F, (byte)'x' })]
    [InlineData(new byte[] { 0x0F, (byte)'1', (byte)'x' })]
    [InlineData(new byte[] { 0x0F, (byte)'1' })]
    [InlineData(new byte[] { 0x0F, (byte)'1', (byte)':' })]
    [InlineData(new byte[] { 0x0E, (byte)'1' })]
    [InlineData(new byte[] { 0x10, (byte)'1' })]
    public void Bytes_that_are_not_the_reserved_byte_a_grace_and_a_nonce_are_no_tombstone(byte[] value)
    {
        TombstoneValue.Is(value).Should().BeFalse();
    }

    [Theory]
    [InlineData("1000:0123456789abcdef", true)]
    [InlineData("1000:0123456789abcdeg", false)]
    [InlineData("1000:0123456789abcde", false)]
    [InlineData("1000:0123456789ABCDEF", false)]
    [InlineData("0:0123456789abcdef", false)]
    [InlineData("-5:0123456789abcdef", false)]
    [InlineData("1000,0123456789abcdef", false)]
    public void A_tombstone_is_a_grace_and_sixteen_hex_digits_of_nonce(string text, bool tombstone)
    {
        byte[] bytes = [0x0F, .. System.Text.Encoding.ASCII.GetBytes(text)];

        TombstoneValue.Is(bytes).Should().Be(tombstone);
    }

    [Theory]
    [InlineData(new byte[] { 0x0F, (byte)'1' }, true)]
    [InlineData(new byte[] { 0x0F, (byte)'9', (byte)':' }, true)]
    [InlineData(new byte[] { 0x0F }, false)]
    [InlineData(new byte[] { 0x0F, (byte)':' }, false)]
    [InlineData(new byte[] { 0x10, (byte)'1' }, false)]
    [InlineData(new byte[] { }, false)]
    public void The_start_of_a_tombstone_is_the_header_and_a_digit(byte[] head, bool tombstone)
    {
        TombstoneValue.IsHead(head).Should().Be(tombstone);
    }

    [Fact]
    public void Whatever_starts_like_a_tombstone_in_a_read_of_the_start_is_one_in_the_whole()
    {
        var bytes = TombstoneValue.Create(30_000);

        TombstoneValue.IsHead(bytes.AsSpan(0, 2)).Should().BeTrue();
        TombstoneValue.Is(bytes).Should().BeTrue();
    }

    [Fact]
    public void The_reserved_byte_is_no_id_a_value_serializer_may_claim_and_no_inventory_format()
    {
        FormatIdRules.IsAllowed(FormatIdRules.TombstoneId).Should().BeFalse();
        FormatIdRules.IsEventId(FormatIdRules.TombstoneId).Should().BeTrue("event ids are a separate namespace, and a tombstone is never an event");
        FormatInventory.Classify(TombstoneValue.Create(30_000), ValueSerializerRegistry.Empty).Should().BeNull("a tombstone holds no value, so it is no format of one, and not an unknown one");
    }

    [Theory]
    [InlineData(1000L, 990L, 5L, "Replace")]
    [InlineData(1000L, 990L, 9L, "Replace")]
    [InlineData(1000L, 990L, 10L, "Young")]
    [InlineData(1000L, 990L, 11L, "Young")]
    [InlineData(1000L, 1000L, 0L, "Young")]
    [InlineData(1000L, 1000L, 1L, "Young")]
    [InlineData(1000L, 1L, 998L, "Replace")]
    [InlineData(1000L, 1L, 999L, "Young")]
    public void A_generator_that_started_before_the_removal_leaves_it_and_one_that_started_after_replaces_it(long grace, long leftMs, long elapsedMs, string expected)
    {
        TombstoneValue.Judge(grace, TimeSpan.FromMilliseconds(leftMs), elapsedMs).ToString().Should().Be(expected);
    }

    [Theory]
    [InlineData(1000L, 1001L)]
    [InlineData(1000L, 600_000L)]
    public void A_deadline_past_the_grace_gives_an_age_below_zero_and_is_unknown(long grace, long leftMs)
    {
        TombstoneValue.Judge(grace, TimeSpan.FromMilliseconds(leftMs), 0).Should().Be(TombstoneVerdict.Unknown);
        TombstoneValue.Judge(grace, TimeSpan.FromMilliseconds(leftMs), 5_000).Should().Be(TombstoneVerdict.Unknown);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(5_000L)]
    public void A_tombstone_with_no_deadline_is_unknown_for_any_generator(long elapsedMs)
    {
        TombstoneValue.Judge(1000, null, elapsedMs).Should().Be(TombstoneVerdict.Unknown);
    }

    [Theory]
    [InlineData(1000L, 0L)]
    [InlineData(1000L, 1L)]
    [InlineData(1000L, 1001L)]
    public void With_no_generator_to_compare_with_any_tombstone_is_replaced(long grace, long leftMs)
    {
        TombstoneValue.Judge(grace, TimeSpan.FromMilliseconds(leftMs), null).Should().Be(TombstoneVerdict.Replace);
        TombstoneValue.Judge(grace, null, null).Should().Be(TombstoneVerdict.Replace);
    }

    [Fact]
    public void A_fraction_of_a_millisecond_left_counts_as_the_whole_one_so_the_age_is_never_overstated()
    {
        TombstoneValue.Judge(1000, TimeSpan.FromTicks(9_995_000), 10).Should().Be(TombstoneVerdict.Young, "999.5 ms left is 1000 ms of it left: age 0");
        TombstoneValue.Judge(1000, TimeSpan.FromTicks(9_995_000), 0).Should().Be(TombstoneVerdict.Young);
    }
}
