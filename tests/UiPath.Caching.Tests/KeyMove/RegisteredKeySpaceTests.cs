using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Tests.KeyMove;

public class RegisteredKeySpaceTests
{
    private const string Library = RegisteredTraitsTests.Library;
    private static readonly string[] Both = ["a", "b"];

    [Fact]
    public void Patterns_are_the_distinct_patterns_of_the_group_s_caches_on_the_side()
    {
        var space = Space(RegisteredTraitsTests.Cache("a"), RegisteredTraitsTests.Cache("b"), RegisteredTraitsTests.Cache("c", group: "other"));

        space.Patterns("g1", MoveSide.Native).Should().BeEquivalentTo($"{Library}n:a:*", $"{Library}n:b:*");
        space.Patterns("g1", MoveSide.Legacy).Should().BeEquivalentTo("l:a:*", "l:b:*");
        space.Patterns("none", MoveSide.Native).Should().BeEmpty();
    }

    [Fact]
    public void The_owner_is_a_managed_cache_of_the_group_whose_predicate_accepts_the_key()
    {
        var space = Space(RegisteredTraitsTests.Cache("a"), RegisteredTraitsTests.Cache("b"), RegisteredTraitsTests.Cache("c"));

        space.OwnersOf("g1", MoveSide.Native, $"{Library}n:b:k1").Should().Equal("b");
        space.OwnersOf("g1", MoveSide.Legacy, "l:a:k1").Should().Equal("a");
        space.OwnersOf("g1", MoveSide.Native, $"{Library}n:c:k1").Should().Equal("c");
        space.OwnersOf("g1", MoveSide.Native, "l:a:k1").Should().BeEmpty("a key of the other side is not native");
        space.OwnersOf("g2", MoveSide.Native, $"{Library}n:a:k1").Should().Equal(["a"], "ownership is judged across every registration, whatever the group asks");
    }

    [Fact]
    public void A_key_two_caches_accept_has_both_owners_and_is_never_movable_even_when_only_one_of_them_manages_it()
    {
        var a = RegisteredTraitsTests.Cache("a") with { OwnsNative = k => k.StartsWith(Library + "n:", StringComparison.Ordinal) };
        var b = RegisteredTraitsTests.Cache("b") with { OwnsNative = k => k.StartsWith(Library + "n:", StringComparison.Ordinal) };
        var space = Space(b, a);

        space.OwnersOf("g1", MoveSide.Native, $"{Library}n:a:k").Should().Equal("a", "b");
        space.IsMovable("g1", MoveSide.Native, "a", $"{Library}n:a:k", null).Should().BeFalse();
    }

    [Fact]
    public void The_keys_of_the_move_itself_are_excluded_before_any_ownership_delegate_runs()
    {
        var asked = new List<string>();
        var greedy = RegisteredTraitsTests.Cache("a") with
        {
            OwnsNative = k =>
            {
                asked.Add(k);
                return true;
            },
            OwnsInPlace = k =>
            {
                asked.Add(k);
                return true;
            },
        };
        var space = Space(greedy);
        var nonce = new string('a', 32);

        foreach (var reserved in new[] { $"{Library}mg:{{g1}}:state", $"{Library}mg:{{ab}}:ctl:g1", $"mg:{Library}n:a:k", $"{Library}n:a:k:stg:{nonce}", $"{Library}mg:{{ab}}:l:xyz", $"{Library}mg:{{ab}}:s:{nonce}"})
        {
            space.OwnersOf("g1", MoveSide.Native, reserved).Should().BeEmpty(reserved);
            space.OwnersOf("g1", MoveSide.Legacy, reserved).Should().BeEmpty(reserved);
        }

        asked.Should().BeEmpty();
        space.OwnersOf("g1", MoveSide.Native, $"{Library}n:a:fine").Should().Equal("a");
    }

    [Fact]
    public void A_native_key_outside_the_library_prefix_is_never_owned_or_movable()
    {
        var space = Space(RegisteredTraitsTests.Cache("a") with { OwnsNative = _ => true, ParseNative = k => k, FormatNative = k => k });

        space.OwnersOf("g1", MoveSide.Native, "other-app:n:a:k").Should().BeEmpty();
        space.IsMovable("g1", MoveSide.Native, "a", "other-app:n:a:k", null).Should().BeFalse();
    }

    [Fact]
    public void A_key_that_does_not_round_trip_is_owned_but_not_movable()
    {
        var head = $"{Library}n:a:";
        var lossy = RegisteredTraitsTests.Cache("a") with { ParseNative = k => k.StartsWith(head, StringComparison.Ordinal) ? k[head.Length..].ToLowerInvariant() : null };
        var space = Space(lossy);

        space.OwnersOf("g1", MoveSide.Native, head + "KEY").Should().Equal("a");
        space.IsMovable("g1", MoveSide.Native, "a", head + "KEY", null).Should().BeFalse("K1N(K2N(N)) differs");
        space.IsMovable("g1", MoveSide.Native, "a", head + "key", null).Should().BeTrue();
        space.IsMovable("g1", MoveSide.Legacy, "a", "l:a:key", null).Should().BeTrue();
        space.IsMovable("g1", MoveSide.Native, "missing", head + "key", null).Should().BeFalse();
    }

    [Fact]
    public void A_key_whose_destination_falls_outside_the_namespace_or_on_a_reserved_key_is_not_movable_and_alerts_once()
    {
        var alerts = new List<KeyMoveAlert>();
        var head = $"{Library}n:a:";
        var stray = RegisteredTraitsTests.Cache("a") with { FormatNative = k => k == "special" ? "other:n:special" : head + k };
        var space = new RegisteredKeySpace([stray], Library, alerts.Add);

        space.IsMovable("g1", MoveSide.Legacy, "a", "l:a:plain", null).Should().BeTrue();
        space.IsMovable("g1", MoveSide.Legacy, "a", "l:a:special", null).Should().BeFalse("its native form is outside the library prefix");
        space.IsMovable("g1", MoveSide.Legacy, "a", "l:a:special", null).Should().BeFalse();

        alerts.Should().ContainSingle(a => a.Code == "unmovable_destination" && a.Group == "g1" && a.Detail.StartsWith("a:", StringComparison.Ordinal));
        var reserved = new RegisteredKeySpace([RegisteredTraitsTests.Cache("a") with { FormatNative = _ => $"{Library}mg:{{g1}}:state" }], Library);
        reserved.IsMovable("g1", MoveSide.Legacy, "a", "l:a:any", null).Should().BeFalse("a native form on a reserved key is never written");
    }

    [Fact]
    public void In_reverse_the_in_place_destination_is_checked_the_same_way()
    {
        var head = $"{Library}n:a:";
        var space = new RegisteredKeySpace([RegisteredTraitsTests.Cache("a") with { FormatInPlace = k => k == "bad" ? $"{Library}mg:{{g1}}:ctl:g1" : "l:a:" + k, ParseInPlace = k => k.StartsWith("l:a:", StringComparison.Ordinal) ? k[4..] : null }], Library);

        space.IsMovable("g1", MoveSide.Native, "a", head + "fine", null).Should().BeTrue();
        space.IsMovable("g1", MoveSide.Native, "a", head + "bad", null).Should().BeFalse();
    }

    [Fact]
    public void A_mapping_into_another_cache_s_keys_or_to_a_key_that_reads_back_differently_is_an_invalid_mapping_in_both_directions()
    {
        var alerts = new List<KeyMoveAlert>();
        var a = RegisteredTraitsTests.Cache("a") with { FormatNative = k => k == "x" ? $"{Library}n:b:x" : $"{Library}n:a:{k}", FormatInPlace = k => k == "x" ? "l:b:x" : $"l:a:{k}" };
        var b = RegisteredTraitsTests.Cache("b");
        var space = new RegisteredKeySpace([a, b], Library, alerts.Add);

        space.HasValidDestination("g1", MoveSide.Legacy, "a", "l:a:x").Should().BeFalse("its native form is one of b's keys");
        space.IsMovable("g1", MoveSide.Legacy, "a", "l:a:x", null).Should().BeFalse();
        space.HasValidDestination("g1", MoveSide.Native, "a", $"{Library}n:a:x").Should().BeFalse("its in-place form is one of b's keys");
        space.IsMovable("g1", MoveSide.Native, "a", $"{Library}n:a:x", null).Should().BeFalse();
        space.IsMovable("g1", MoveSide.Legacy, "a", "l:a:fine", null).Should().BeTrue();
        space.IsMovable("g1", MoveSide.Native, "a", $"{Library}n:a:fine", null).Should().BeTrue();

        var skewed = new RegisteredKeySpace([RegisteredTraitsTests.Cache("a") with { ParseNative = k => k.StartsWith($"{Library}n:a:", StringComparison.Ordinal) ? "always-the-same" : null }], Library);
        skewed.IsMovable("g1", MoveSide.Legacy, "a", "l:a:one", null).Should().BeFalse("the native key reads back as another caller key");
    }

    [Fact]
    public void An_in_place_key_under_the_library_prefix_is_the_library_s_own_unless_the_cache_says_it_owns_such_keys()
    {
        var greedy = RegisteredTraitsTests.Cache("a") with { ParseInPlace = k => k };
        var explicitOwner = greedy with { OwnsInPlace = _ => true };

        new RegisteredKeySpace([greedy], Library).OwnersOf("g1", MoveSide.Legacy, $"{Library}n:a:k").Should().BeEmpty();
        new RegisteredKeySpace([explicitOwner], Library).OwnersOf("g1", MoveSide.Legacy, $"{Library}n:a:k").Should().Equal("a");
    }

    [Fact]
    public void A_cache_that_maps_validly_again_alerts_again_after_a_new_defect()
    {
        var alerts = new List<KeyMoveAlert>();
        var broken = true;
        var cache = RegisteredTraitsTests.Cache("a") with { FormatNative = k => broken ? "other:" + k : $"{Library}n:a:{k}" };
        var space = new RegisteredKeySpace([cache], Library, alerts.Add);

        space.IsMovable("g1", MoveSide.Legacy, "a", "l:a:k", null).Should().BeFalse();
        broken = false;
        space.IsMovable("g1", MoveSide.Legacy, "a", "l:a:k", null).Should().BeTrue();
        broken = true;
        space.IsMovable("g1", MoveSide.Legacy, "a", "l:a:k", null).Should().BeFalse();

        alerts.Should().HaveCount(2, "the alert re-arms once the mapping was valid");
    }

    [Fact]
    public void The_source_life_comes_from_the_registration()
    {
        var space = Space(RegisteredTraitsTests.Cache("a", ttl: TimeSpan.FromMinutes(7)));

        space.MaxSourceTtl("g1", "a").Should().Be(TimeSpan.FromMinutes(7));
        space.MaxSourceTtl("g1", "b").Should().BeNull();
    }

    private static RegisteredKeySpace Space(params KeyMoveCache[] caches) => new(caches, Library);
}
