using UiPath.Caching.KeyMove;
using UiPath.Caching.Redis;

namespace UiPath.Caching.Tests.KeyMove;

// The names the mover keeps: a unit's lease, cursor and pass record share a slot, and the progress hash sits with the group's state.
public class MoverNamesTests
{
    [Theory]
    [InlineData("")]
    [InlineData("{app}:")]
    public void A_units_lease_cursor_and_pass_record_share_a_slot_and_sit_under_the_library_prefix(string connectorPrefix)
    {
        var names = new MoveKeyNames(connectorPrefix, "lib:", true);
        var unit = "07c37dfeb235213a872192d90877d0cd55635b91";

        var keys = new[] { names.UnitLease("g1", unit), names.UnitCursor("g1", unit, "fwd", 7), names.UnitPass("g1", unit, "fwd", 7) };

        keys[0].ToString().Should().Be($"lib:mg:{{g1:{unit}}}:lease");
        keys[1].ToString().Should().Be($"lib:mg:{{g1:{unit}}}:cur:fwd:7");
        keys[2].ToString().Should().Be($"lib:mg:{{g1:{unit}}}:pass:fwd:7");
        keys.Select(k => names.SlotOf(k.ToString())).Distinct().Should().ContainSingle();
        names.Progress("g1").ToString().Should().Be("lib:mg:{g1}:prog");
        names.SlotOf(names.Progress("g1").ToString()).Should().Be(names.SlotOf(names.State("g1").ToString()));
        ReservedKeys.IsReserved("lib:", keys[0].ToString()).Should().BeTrue();
    }

    [Theory]
    [InlineData("a}b")]
    [InlineData("a{b")]
    [InlineData("")]
    public void A_unit_id_with_a_brace_cannot_name_a_key(string unit)
    {
        var names = new MoveKeyNames(string.Empty, "lib:", true);

        var name = () => names.UnitLease("g1", unit);

        name.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void The_minimum_life_of_the_mover_is_the_cache_s_then_the_group_s_then_ten_minutes()
    {
        static KeyMoveCache Cache(string name, string group, TimeSpan? min) => new(name, group, false, false, false, null, $"l:{name}:*", k => k, _ => null, $"n:{name}:*", k => k, _ => null, MoveMinTtl: min);
        var space = new RegisteredKeySpace(
            [Cache("a", "g1", null), Cache("b", "g1", TimeSpan.Zero), Cache("c", "g2", null)],
            "n:",
            groupMinTtl: new Dictionary<string, TimeSpan> { ["g1"] = TimeSpan.FromMinutes(2) });

        space.MoveMinTtl("g1", "a").Should().Be(TimeSpan.FromMinutes(2));
        space.MoveMinTtl("g1", "b").Should().Be(TimeSpan.Zero);
        space.MoveMinTtl("g2", "c").Should().Be(TimeSpan.FromMinutes(10));
        space.MoveMinTtl("g9", "x").Should().Be(TimeSpan.FromMinutes(10));
    }

    [Theory]
    [InlineData(1, false, false, false)]
    [InlineData(1, true, false, true)]
    [InlineData(5, false, true, true)]
    [InlineData(15, false, false, true)]
    [InlineData(null, false, false, true)]
    public void A_key_with_less_life_than_the_minimum_is_not_movable_going_forward_but_never_for_a_cache_with_no_origin_or_a_zero_minimum(int? minutes, bool zeroMinimum, bool noOrigin, bool movable)
    {
        var cache = new KeyMoveCache("a", "g1", noOrigin, false, false, null, "l:a:*", k => "l:a:" + k, k => k.StartsWith("l:a:", StringComparison.Ordinal) ? k[4..] : null, "n:a:*", k => "n:a:" + k, k => k.StartsWith("n:a:", StringComparison.Ordinal) ? k[4..] : null, MoveMinTtl: zeroMinimum ? TimeSpan.Zero : TimeSpan.FromMinutes(10));
        var space = new RegisteredKeySpace([cache], "n:");

        space.IsMovable("g1", MoveSide.Legacy, "a", "l:a:k", minutes is { } m ? TimeSpan.FromMinutes(m) : null).Should().Be(movable);
    }
}
