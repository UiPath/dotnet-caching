using UiPath.Caching.PhaseB;

namespace UiPath.Caching.Tests.PhaseB;

public class SlotTagsTests
{
    private static readonly Lazy<string?[]> All = new(SlotTags.BuildAll);

    [Fact]
    public void Every_slot_has_a_short_tag_that_hashes_to_it()
    {
        var all = All.Value;

        all.Should().HaveCount(HashSlot.Count);
        for (var slot = 0; slot < all.Length; slot++)
        {
            var tag = all[slot];
            tag.Should().NotBeNull($"slot {slot} must be reachable");
            tag!.Length.Should().BeInRange(1, SlotTags.MaxLength);
            tag.Should().MatchRegex("^[0-9a-z]+$");
            HashSlot.Of(tag).Should().Be(slot);
        }
    }

    [Fact]
    public void For_agrees_with_the_one_pass_build()
    {
        var all = All.Value;
        var random = new Random(20260101);
        int[] slots = [0, 1, 8191, 16383, .. Enumerable.Range(0, 50).Select(_ => random.Next(HashSlot.Count))];

        foreach (var slot in slots)
        {
            SlotTags.For(slot).Should().Be(all[slot]);
        }
    }

    [Fact]
    public void For_returns_the_same_tag_on_a_repeat()
    {
        SlotTags.For(4242).Should().Be(SlotTags.For(4242));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(16384)]
    public void For_rejects_a_slot_outside_the_range(int slot)
    {
        var act = () => SlotTags.For(slot);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
