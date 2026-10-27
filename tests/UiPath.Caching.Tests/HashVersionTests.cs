namespace UiPath.Caching.Tests;

/// <summary>An entry stands on the hash's version with this node's own writes laid over it, and on nothing once one of them cannot be placed.</summary>
public class HashVersionTests
{
    [Fact]
    public void Writes_that_returned_one_after_another_from_the_version_are_counted()
    {
        Stand(5, Write(6), Write(7)).TryResolve(out var number, out var ahead).Should().BeTrue();

        number.Should().Be(7);
        ahead.Should().BeEmpty();
    }

    [Fact]
    public void A_write_that_returned_past_the_next_version_waits_for_the_writes_between_and_so_does_what_follows_it()
    {
        var late = Write(9);
        var after = Write(10);

        Stand(5, Write(6), late, after).TryResolve(out var number, out var ahead).Should().BeTrue();

        number.Should().Be(6);
        ahead.Should().Equal(late, after);
    }

    [Fact]
    public void A_write_that_has_not_returned_waits_and_holds_the_version_as_it_is()
    {
        var pending = Write();

        Stand(5, pending).TryResolve(out var number, out var ahead).Should().BeTrue();

        number.Should().Be(5);
        ahead.Should().Equal(pending);
    }

    [Fact]
    public void A_write_that_failed_or_returned_a_version_the_entry_is_past_leaves_it_standing_on_nothing()
    {
        Stand(5, Write(failed: true)).TryResolve(out _, out _).Should().BeFalse();
        Stand(5, Write(5)).TryResolve(out _, out _).Should().BeFalse();
        Stand(5, Write(4)).TryResolve(out _, out _).Should().BeFalse();
        Stand(5, Write(6), Write(failed: true)).TryResolve(out _, out _).Should().BeFalse();
    }

    [Fact]
    public void A_replace_gives_the_entry_its_version_and_the_merges_after_it_follow()
    {
        Stand(0, Write(3, replace: true), Write(4)).TryResolve(out var number, out var ahead).Should().BeTrue();

        number.Should().Be(4);
        ahead.Should().BeEmpty();
    }

    [Fact]
    public void A_replace_that_has_not_returned_leaves_the_entry_with_no_version_and_a_merge_over_it_cannot_be_placed_before_it_returns()
    {
        var replace = Write(replace: true);
        var merge = Write(4);

        Stand(0, replace, merge).TryResolve(out var number, out var ahead).Should().BeTrue();

        number.Should().Be(0);
        ahead.Should().Equal(replace, merge);
        Stand(0, Write(4)).TryResolve(out _, out _).Should().BeFalse();
        Stand(5, Write(7, replace: true)).TryResolve(out _, out _).Should().BeFalse();
    }

    [Fact]
    public void A_write_that_returned_in_another_epoch_than_the_entry_stands_on_leaves_it_standing_on_nothing_whatever_its_number()
    {
        var held = Epoch(7, 3);

        Stand(held, Write(Epoch(8, 4))).TryResolve(out _, out _).Should().BeFalse();
        Stand(held, Write(Epoch(6, 4))).TryResolve(out _, out _).Should().BeFalse();
        Stand(held, Write(Epoch(7, 4))).TryResolve(out var number, out _).Should().BeTrue();
        number.Should().Be(Epoch(7, 4));
        Stand(held, Write(5)).TryResolve(out _, out _).Should().BeFalse();
        Stand(3, Write(Epoch(7, 4))).TryResolve(out _, out _).Should().BeFalse();
        Stand(3, Write(4)).TryResolve(out _, out _).Should().BeTrue();
    }

    [Theory]
    [InlineData(3, 2, 3, 2, true)]
    [InlineData(3, 2, 3, 9, true)]
    [InlineData(3, 2, 4, 2, false)]
    [InlineData(0, 5, 0, 2, false)]
    [InlineData(0, 5, 3, 2, false)]
    [InlineData(3, 5, 0, 2, false)]
    public void Two_versions_share_an_epoch_only_when_both_carry_the_same_one(long heldEpoch, long held, long eventEpoch, long counted, bool expected) =>
        HashVersion.SameEpoch(Epoch(heldEpoch, held), Epoch(eventEpoch, counted)).Should().Be(expected);

    [Fact]
    public void A_new_epoch_is_drawn_above_zero_and_counts_from_the_low_bits()
    {
        var seen = new HashSet<long>();
        for (var i = 0; i < 1000; i++)
        {
            var seed = HashVersion.NewEpochSeed();
            seed.Should().BePositive();
            (seed & ((1L << HashVersion.CounterBits) - 1)).Should().Be(0);
            (seed >> HashVersion.CounterBits).Should().BeGreaterThan(0);
            (seed + 1).Should().BeLessThan(long.MaxValue - (1L << 40));
            seen.Add(seed);
        }

        seen.Count.Should().BeGreaterThan(990);
    }

    private static long Epoch(long epoch, long counted) =>
        (epoch << HashVersion.CounterBits) + counted;

    private static OwnWrite Write(long version = 0, bool replace = false, bool failed = false) =>
        new(["f"], replace) { Version = version, Failed = failed };

    private static HashVersion<string> Stand(long number, params OwnWrite[] own) =>
        new HashVersion<string>(number, own, null);
}
