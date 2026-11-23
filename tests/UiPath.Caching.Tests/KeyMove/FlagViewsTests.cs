using UiPath.Caching.KeyMove;
using static UiPath.Caching.Tests.KeyMove.PlannerKit;

namespace UiPath.Caching.Tests.KeyMove;

// The flag view rules on the leader's monotonic clock: FlagLowerHold, the lowest persisted view, CloseConfirm.
public class FlagViewsTests
{
    private readonly ManualTimeProvider _time = new(DateTimeOffset.UnixEpoch);

    [Fact]
    public void A_view_below_the_stage_for_less_than_FlagLowerHold_asks_no_way_back_and_for_longer_it_does()
    {
        var views = Views();
        var fresh = new[] { Advert("n1", fl: 3), Advert("n2", fl: 2) };

        views.Sample(Group, fresh, 3).Lowered.Should().BeNull();
        _time.Advance(TimeSpan.FromSeconds(59));
        views.Sample(Group, fresh, 3).Lowered.Should().BeNull();
        _time.Advance(TimeSpan.FromSeconds(1));

        views.Sample(Group, fresh, 3).Lowered.Should().Be(2);
    }

    [Fact]
    public void A_view_that_comes_back_to_the_stage_restarts_the_hold()
    {
        var views = Views();
        views.Sample(Group, [Advert("n1", fl: 2)], 3);
        _time.Advance(TimeSpan.FromSeconds(50));
        views.Sample(Group, [Advert("n1", fl: 3)], 3);
        _time.Advance(TimeSpan.FromSeconds(30));
        views.Sample(Group, [Advert("n1", fl: 2)], 3).Lowered.Should().BeNull();

        _time.Advance(TimeSpan.FromSeconds(60));

        views.Sample(Group, [Advert("n1", fl: 2)], 3).Lowered.Should().Be(2);
    }

    [Fact]
    public void The_way_back_goes_to_the_lowest_view_that_persisted_and_a_view_that_changed_restarts_its_own_hold()
    {
        var views = Views();
        views.Sample(Group, [Advert("n1", fl: 2), Advert("n2", fl: 1)], 4);
        _time.Advance(TimeSpan.FromSeconds(40));
        views.Sample(Group, [Advert("n1", fl: 2), Advert("n2", fl: 0)], 4);
        _time.Advance(TimeSpan.FromSeconds(30));

        views.Sample(Group, [Advert("n1", fl: 2), Advert("n2", fl: 0)], 4).Lowered.Should().Be(2, "the 0 of n2 has persisted for only 30 seconds");

        _time.Advance(TimeSpan.FromSeconds(30));
        views.Sample(Group, [Advert("n1", fl: 2), Advert("n2", fl: 0)], 4).Lowered.Should().Be(0);
    }

    [Fact]
    public void A_pod_without_a_view_blocks_moving_forward_and_never_starts_a_way_back()
    {
        var views = Views();
        var fresh = new[] { Advert("n1", fl: 3), Advert("n2", fl: null) };

        var first = views.Sample(Group, fresh, 3);
        _time.Advance(TimeSpan.FromHours(1));
        var later = views.Sample(Group, fresh, 3);

        first.Should().Match<FlagVerdict>(v => v.Missing && v.Min == 3 && !v.AllAtLeast(3));
        later.Lowered.Should().BeNull();
    }

    [Fact]
    public void A_node_that_leaves_the_view_takes_its_hold_with_it_and_a_state_change_restarts_every_hold()
    {
        var views = Views();
        views.Sample(Group, [Advert("n1", fl: 3), Advert("n2", fl: 2)], 3);
        _time.Advance(TimeSpan.FromSeconds(61));
        views.Sample(Group, [Advert("n1", fl: 3)], 3).Lowered.Should().BeNull();

        views.Sample(Group, [Advert("n1", fl: 3), Advert("n2", fl: 2)], 3);
        _time.Advance(TimeSpan.FromSeconds(61));
        views.Sample(Group, [Advert("n1", fl: 3), Advert("n2", fl: 2)], 3).Lowered.Should().Be(2);
        views.Sample(Group, [Advert("n1", fl: 3), Advert("n2", fl: 2)], 2).Lowered.Should().BeNull("the stage changed: S0 stands for 2");
    }

    [Fact]
    public void CloseConfirm_measures_how_long_every_view_has_shown_the_level_and_one_that_falls_restarts_it()
    {
        var views = Views();
        var at5 = new[] { Advert("n1", fl: 5), Advert("n2", fl: 5) };

        views.Sample(Group, at5, 4, confirmLevel: 5).Confirmed.Should().Be(TimeSpan.Zero);
        _time.Advance(TimeSpan.FromSeconds(90));
        views.Sample(Group, at5, 4, confirmLevel: 5).Confirmed.Should().Be(TimeSpan.FromSeconds(90));
        views.Sample(Group, [Advert("n1", fl: 5), Advert("n2", fl: 4)], 4, confirmLevel: 5).Confirmed.Should().Be(TimeSpan.Zero);
        _time.Advance(TimeSpan.FromSeconds(30));
        views.Sample(Group, at5, 4, confirmLevel: 5).Confirmed.Should().Be(TimeSpan.Zero);
        _time.Advance(TimeSpan.FromSeconds(120));

        views.Sample(Group, at5, 4, confirmLevel: 5).Confirmed.Should().Be(TimeSpan.FromSeconds(120));
    }

    [Fact]
    public void A_pod_that_joins_showing_the_level_restarts_the_confirmation_of_every_view()
    {
        var views = Views();
        var old = new[] { Advert("n1", fl: 5), Advert("n2", fl: 5) };
        views.Sample(Group, old, 4, confirmLevel: 5);
        _time.Advance(TimeSpan.FromMinutes(3));
        views.Sample(Group, old, 4, confirmLevel: 5).Confirmed.Should().Be(TimeSpan.FromMinutes(3));

        views.Sample(Group, [.. old, Advert("n3", fl: 5)], 4, confirmLevel: 5).Confirmed.Should().Be(TimeSpan.Zero);
        _time.Advance(TimeSpan.FromSeconds(30));

        views.Sample(Group, [.. old, Advert("n3", fl: 5)], 4, confirmLevel: 5).Confirmed.Should().Be(TimeSpan.FromSeconds(30), "the least time any pod has shown 5");
    }

    [Fact]
    public void A_pod_that_has_not_read_the_flag_does_not_count_as_confirmed()
    {
        var views = Views();

        views.Sample(Group, [Advert("n1", fl: 5), Advert("n2", fl: null)], 4, confirmLevel: 5);
        _time.Advance(TimeSpan.FromMinutes(5));

        views.Sample(Group, [Advert("n1", fl: 5), Advert("n2", fl: null)], 4, confirmLevel: 5).Confirmed.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void The_stability_of_the_view_restarts_when_a_node_joins_or_lapses()
    {
        var stability = new ViewStability(_time);
        var two = new[] { Advert("n1"), Advert("n2") };

        stability.Observe(Group, two);
        _time.Advance(TimeSpan.FromMinutes(5));
        stability.Observe(Group, two).Should().Be(TimeSpan.FromMinutes(5));
        stability.Observe(Group, [.. two, Advert("n3")]).Should().Be(TimeSpan.Zero);
        _time.Advance(TimeSpan.FromMinutes(1));
        stability.Observe(Group, [.. two, Advert("n3")]).Should().Be(TimeSpan.FromMinutes(1));
        stability.Observe(Group, two).Should().Be(TimeSpan.Zero);
    }

    private FlagViews Views() => new(new StageDriverOptions(), _time);
}
