namespace UiPath.Caching.Tests.KeyMove;

internal static class TimeSteps
{
    // Waits until a loop has armed its next delay, so the clock is moved only when the loop can see it.
    public static async Task ArmedAsync(ManualTimeProvider time)
    {
        for (var tries = 0; !time.HasTimers; tries++)
        {
            tries.Should().BeLessThan(2000, "the loop never armed a delay");
            await Task.Delay(5);
        }
    }

    // A poll holds a timer for its read timeout until the read settles; the loop's delay is the first timer created after the read began, and once it is the only live one the clock can move.
    public static async Task DelayArmedAfterAsync(ManualTimeProvider time, int createdAtRead)
    {
        for (var tries = 0; time.TimersCreated <= createdAtRead || time.TimerCount != 1; tries++)
        {
            tries.Should().BeLessThan(2000, "the loop never armed its delay");
            await Task.Delay(5);
        }
    }

    public static async Task AdvanceArmedAsync(ManualTimeProvider time, TimeSpan by)
    {
        await ArmedAsync(time);
        time.Advance(by);
    }

    public static async Task UntilAsync(Func<bool> condition)
    {
        for (var tries = 0; !condition(); tries++)
        {
            tries.Should().BeLessThan(2000, "the condition never held");
            await Task.Delay(5);
        }
    }
}
