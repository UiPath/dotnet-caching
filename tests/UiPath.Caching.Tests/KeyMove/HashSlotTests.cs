using System.Text;
using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Tests.KeyMove;

public class HashSlotTests
{
    [Fact]
    public void Crc16_matches_the_xmodem_check_value()
    {
        HashSlot.Crc16("123456789"u8).Should().Be(0x31C3);
    }

    [Theory]
    [InlineData("foo", 12182)]
    [InlineData("bar", 5061)]
    public void Slot_matches_the_values_the_server_reports(string key, int slot)
    {
        HashSlot.Of(key).Should().Be(slot);
    }

    [Fact]
    public void Keys_with_the_same_tag_share_a_slot()
    {
        HashSlot.Of("{user1000}.following").Should().Be(HashSlot.Of("{user1000}.followers"));
        HashSlot.Of("{user1000}.following").Should().Be(HashSlot.Of("user1000"));
    }

    [Fact]
    public void An_empty_tag_hashes_the_whole_key()
    {
        const string key = "foo{}{bar}";
        HashSlot.Of(key).Should().Be(HashSlot.Crc16(Encoding.UTF8.GetBytes(key)) & (HashSlot.Count - 1));
        HashSlot.Of(key).Should().NotBe(HashSlot.Of("bar"));
    }

    [Fact]
    public void The_first_open_brace_and_the_first_close_after_it_make_the_tag()
    {
        HashSlot.Of("foo{{bar}}zap").Should().Be(HashSlot.Of("{bar"));
    }

    [Theory]
    [InlineData("", "key")]
    [InlineData("app:", "s:{cache:k1}")]
    [InlineData("{shard}:", "plain")]
    [InlineData("a{b", "c}d")]
    public void Prefix_and_key_hash_as_their_concatenation(string prefix, string key)
    {
        HashSlot.Of(prefix, key).Should().Be(HashSlot.Of(prefix + key));
    }
}
