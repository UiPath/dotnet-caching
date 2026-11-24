namespace UiPath.Caching.Migration.Tests.KeyMove;

// The one size rule of a hash: the same function answers the read, the promotion and the inventory.
public class KeyMoveHashSizeTests
{
    [Theory]
    [InlineData(0, nameof(DecodeClass.Ok))]
    [InlineData(4095, nameof(DecodeClass.Ok))]
    [InlineData(4096, nameof(DecodeClass.Ok))]
    [InlineData(4097, nameof(DecodeClass.TooLarge))]
    public void The_field_count_decides_and_a_hash_at_the_cap_is_not_too_large(long fields, string expected) =>
        KeyMoveHashSize.Of(fields, 4096).ToString().Should().Be(expected);

    [Theory]
    [InlineData(0, nameof(DecodeClass.Null))]
    [InlineData(1, nameof(DecodeClass.Ok))]
    [InlineData(5, nameof(DecodeClass.Ok))]
    [InlineData(6, nameof(DecodeClass.TooLarge))]
    public void The_inventory_judges_by_length_alone_with_the_same_cap(long fields, string expected) =>
        KeyMoveHashSize.OfLength(fields, 5).ToString().Should().Be(expected);

    [Theory]
    [InlineData(99, false)]
    [InlineData(100, false)]
    [InlineData(101, true)]
    public void A_read_hash_is_too_large_only_above_the_byte_cap(long bytes, bool exceeds) =>
        KeyMoveHashSize.ExceedsBytes(bytes, 100).Should().Be(exceeds);

    [Fact]
    public void The_default_cap_is_4096_fields_and_the_options_carry_it()
    {
        new KeyMoveOptions().MaxHashFields.Should().Be(4096);
    }
}
