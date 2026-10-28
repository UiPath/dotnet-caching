using StackExchange.Redis;

namespace UiPath.Caching.Tests.Redis;

public class RecentTouchesTests
{
    private static readonly RedisKey Key = "k";
    private static readonly RedisKey Other = "other";

    private readonly TestClock _clock = new();

    [Fact]
    public void An_untouched_key_is_not_guarded()
    {
        new RecentTouches(_clock, RecentTouches.DefaultWindow).IsGuarded(Key).Should().BeFalse();
    }

    [Fact]
    public void A_touched_key_is_guarded_for_the_window_only()
    {
        var sut = new RecentTouches(_clock, RecentTouches.DefaultWindow);
        sut.Touch(Key);

        sut.IsGuarded(Key).Should().BeTrue();
        sut.IsGuarded(Other).Should().BeFalse();
        _clock.Now += RecentTouches.DefaultWindow - TimeSpan.FromTicks(1);
        sut.IsGuarded(Key).Should().BeTrue();
        _clock.Now += TimeSpan.FromTicks(1);
        sut.IsGuarded(Key).Should().BeFalse();
    }

    [Fact]
    public void A_touch_again_restarts_the_window()
    {
        var sut = new RecentTouches(_clock, RecentTouches.DefaultWindow);
        sut.Touch(Key);
        _clock.Now += RecentTouches.DefaultWindow / 2;
        sut.Touch(Key);
        _clock.Now += RecentTouches.DefaultWindow / 2;

        sut.IsGuarded(Key).Should().BeTrue();
    }

    [Fact]
    public void A_batch_is_guarded_when_any_key_is_and_not_when_none_is()
    {
        var sut = new RecentTouches(_clock, RecentTouches.DefaultWindow);
        sut.Touch([Key]);

        sut.IsGuarded([Other, Key]).Should().BeTrue();
        sut.IsGuarded([Other, "another"]).Should().BeFalse();
    }

    [Fact]
    public void Touching_everything_guards_every_key_for_the_window()
    {
        var sut = new RecentTouches(_clock, RecentTouches.DefaultWindow);
        sut.TouchAll();

        sut.IsGuarded(Key).Should().BeTrue();
        sut.IsGuarded([Key, Other]).Should().BeTrue();
        _clock.Now += RecentTouches.DefaultWindow;
        sut.IsGuarded(Key).Should().BeFalse();
        sut.IsGuarded([Key, Other]).Should().BeFalse();
    }

    [Fact]
    public void A_full_tracker_guards_every_key_until_the_window_drains()
    {
        var sut = Fill(new RecentTouches(_clock, RecentTouches.DefaultWindow));
        sut.Touch("one too many");

        sut.IsGuarded(Key).Should().BeTrue();
        sut.IsGuarded([Key, Other]).Should().BeTrue();
        sut.Count.Should().Be(RecentTouches.MaxKeys, "a full tracker keeps what it holds and guards the rest as a whole");
        _clock.Now += RecentTouches.DefaultWindow;
        sut.IsGuarded(Key).Should().BeFalse();
        sut.IsGuarded("one too many").Should().BeFalse();
    }

    [Fact]
    public void A_full_tracker_makes_room_by_dropping_expired_entries_without_guarding_everything()
    {
        var sut = Fill(new RecentTouches(_clock, RecentTouches.DefaultWindow));
        _clock.Now += RecentTouches.DefaultWindow;

        sut.Touch("fresh");

        sut.IsGuarded("fresh").Should().BeTrue();
        sut.IsGuarded(Key).Should().BeFalse();
        sut.Count.Should().Be(1);
    }

    [Fact]
    public void A_full_tracker_still_renews_a_key_it_holds()
    {
        var sut = Fill(new RecentTouches(_clock, RecentTouches.DefaultWindow));
        _clock.Now += RecentTouches.DefaultWindow - TimeSpan.FromTicks(1);

        sut.Touch("filler:0");

        sut.IsGuarded(Key).Should().BeFalse();
        sut.Count.Should().Be(RecentTouches.MaxKeys);
        _clock.Now += TimeSpan.FromTicks(2);
        sut.IsGuarded("filler:0").Should().BeTrue();
    }

    [Fact]
    public void A_configured_window_guards_for_its_length()
    {
        var sut = new RecentTouches(_clock, TimeSpan.FromSeconds(5));
        sut.Touch(Key);

        _clock.Now += TimeSpan.FromSeconds(5) - TimeSpan.FromTicks(1);
        sut.IsGuarded(Key).Should().BeTrue();
        _clock.Now += TimeSpan.FromTicks(1);
        sut.IsGuarded(Key).Should().BeFalse();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void A_wall_clock_step_neither_ends_nor_stretches_the_window(int hours)
    {
        var sut = new RecentTouches(_clock, RecentTouches.DefaultWindow);
        sut.Touch(Key);

        _clock.WallStep += TimeSpan.FromHours(hours);
        sut.IsGuarded(Key).Should().BeTrue();
        _clock.Now += RecentTouches.DefaultWindow;
        sut.IsGuarded(Key).Should().BeFalse();
    }

    private static RecentTouches Fill(RecentTouches sut)
    {
        for (var i = 0; i < RecentTouches.MaxKeys; i++)
        {
            sut.Touch((RedisKey)$"filler:{i}");
        }

        return sut;
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        /// <summary>Moves the wall clock alone, as an NTP correction does; timestamps keep to <see cref="Now"/>.</summary>
        public TimeSpan WallStep { get; set; }

        public override DateTimeOffset GetUtcNow() => Now + WallStep;

        public override long GetTimestamp() => Now.UtcTicks;
    }
}
