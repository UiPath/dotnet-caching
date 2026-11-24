using UiPath.Caching.KeyMove;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

public sealed class NegativeHintsTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_hint_is_read_only_under_the_identity_and_side_it_was_made_under()
    {
        var hints = new NegativeHints(new ManualTimeProvider(Start), TimeSpan.FromSeconds(5), 10);

        hints.Set("k", "1:id1:fwd", MoveSide.Legacy);

        hints.Has("k", "1:id1:fwd", MoveSide.Legacy).Should().BeTrue();
        hints.Has("k", "1:id1:fwd", MoveSide.Native).Should().BeFalse("a hint that L is absent says nothing about N");
        hints.Has("k", "2:id2:rev", MoveSide.Legacy).Should().BeFalse();
    }

    [Fact]
    public void A_hint_for_the_other_side_replaces_the_one_before_it()
    {
        var hints = new NegativeHints(new ManualTimeProvider(Start), TimeSpan.FromSeconds(5), 10);

        hints.Set("k", "2:id2:rev", MoveSide.Legacy);
        hints.Set("k", "2:id2:rev", MoveSide.Native);

        hints.Has("k", "2:id2:rev", MoveSide.Native).Should().BeTrue();
        hints.Has("k", "2:id2:rev", MoveSide.Legacy).Should().BeFalse();
        hints.Count.Should().Be(1);
    }

    [Fact]
    public void A_hint_lapses_after_its_lifetime()
    {
        var clock = new ManualTimeProvider(Start);
        var hints = new NegativeHints(clock, TimeSpan.FromSeconds(5), 10);
        hints.Set("k", "1:id1:fwd", MoveSide.Native);

        clock.Advance(TimeSpan.FromSeconds(5));

        hints.Has("k", "1:id1:fwd", MoveSide.Native).Should().BeFalse();
    }
}
