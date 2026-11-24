using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

public sealed class WritePermitsTests
{
    [Fact]
    public async Task A_permit_without_an_answer_comes_back_only_after_its_deadline()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch.AddDays(20000));
        using var permits = new WritePermits(clock, 1);
        await permits.WaitAsync(CancellationToken.None);

        permits.ReleaseAt(clock.GetUtcNow() + TimeSpan.FromSeconds(7));
        var waiting = permits.WaitAsync(CancellationToken.None);

        waiting.IsCompleted.Should().BeFalse();
        while (!clock.HasTimers)
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        clock.Advance(TimeSpan.FromSeconds(8));
        await waiting.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        permits.InFlight.Should().Be(1);
    }

    [Fact]
    public async Task A_deadline_that_has_passed_returns_the_permit_at_once()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch.AddDays(20000));
        using var permits = new WritePermits(clock, 1);
        await permits.WaitAsync(CancellationToken.None);

        permits.ReleaseAt(clock.GetUtcNow() - TimeSpan.FromSeconds(1));

        permits.InFlight.Should().Be(0);
    }
}
