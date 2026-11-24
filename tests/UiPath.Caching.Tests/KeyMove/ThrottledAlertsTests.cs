using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Tests.KeyMove;

public class ThrottledAlertsTests
{
    [Fact]
    public void The_same_alert_passes_once_per_period_and_a_different_one_passes_at_once()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var seen = new List<string>();
        var throttle = new ThrottledAlerts(a => seen.Add(a.Detail), time, TimeSpan.FromSeconds(2));
        var one = Alert("key-1");

        throttle.Raise(one);
        throttle.Raise(one);
        throttle.Raise(Alert("key-2"));
        time.Advance(TimeSpan.FromSeconds(1));
        throttle.Raise(one);
        time.Advance(TimeSpan.FromSeconds(2));
        throttle.Raise(one);

        seen.Should().Equal("key-1", "key-2", "key-1");
    }

    [Fact]
    public void Without_a_handler_nothing_is_kept()
    {
        var throttle = new ThrottledAlerts(null, new ManualTimeProvider(DateTimeOffset.UnixEpoch), TimeSpan.FromSeconds(2));

        var act = () => throttle.Raise(Alert("k"));

        act.Should().NotThrow();
    }

    private static KeyMoveAlert Alert(string detail) => new("g1", "ambiguous_ownership", detail);
}
