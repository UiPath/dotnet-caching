namespace UiPath.Caching.Tests.KeyMove;

internal static class TimeSteps
{
    public static Task ArmedAsync(ManualTimeProvider time) => time.WaitForTimersAsync(() => time.HasTimers);

    public static Task DelayArmedAfterAsync(ManualTimeProvider time, int createdAtRead) =>
        time.WaitForTimersAsync(() => time.TimersCreated > createdAtRead && time.TimerCount == 1);

    public static async Task AdvanceArmedAsync(ManualTimeProvider time, TimeSpan by)
    {
        await ArmedAsync(time);
        time.Advance(by);
    }
}
