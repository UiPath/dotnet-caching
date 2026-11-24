using System.Collections.Concurrent;
using System.Reflection;
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
    public async Task Concurrent_units_raise_one_missing_target_alert_per_cache_in_each_period()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var simultaneous = new Barrier(2);
        var clock = new ConcurrentClock(simultaneous, cancellationToken);
        var period = TimeSpan.FromSeconds(1);
        var seen = 0;
        var throttle = new ThrottledAlerts(_ => Interlocked.Increment(ref seen), clock, period);
        var alert = new KeyMoveAlert("group", "mover_no_target", "cache");
        throttle.Raise(alert);
        clock.Timestamp = period.Ticks;

        await Task.WhenAll(RaiseFromUnit(), RaiseFromUnit());

        seen.Should().Be(2);

        Task RaiseFromUnit() => Task.Factory.StartNew(() => throttle.Raise(alert), cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    [Fact]
    public void Without_a_handler_nothing_is_kept()
    {
        var throttle = new ThrottledAlerts(null, new ManualTimeProvider(DateTimeOffset.UnixEpoch), TimeSpan.FromSeconds(2));

        var act = () => throttle.Raise(Alert("k"));

        act.Should().NotThrow();
        var sent = (ConcurrentDictionary<string, long>)typeof(ThrottledAlerts)
            .GetField("_sent", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(throttle)!;
        sent.Should().BeEmpty();
    }

    private static KeyMoveAlert Alert(string detail) => new("g1", "ambiguous_ownership", detail);

    private sealed class ConcurrentClock(Barrier simultaneous, CancellationToken cancellationToken) : TimeProvider
    {
        private int _reads;

        public long Timestamp { get; set; }

        public override long TimestampFrequency
        {
            get
            {
                if (Interlocked.Increment(ref _reads) <= 2)
                {
                    simultaneous.SignalAndWait(cancellationToken);
                }

                return TimeSpan.TicksPerSecond;
            }
        }

        public override long GetTimestamp() => Timestamp;
    }
}
