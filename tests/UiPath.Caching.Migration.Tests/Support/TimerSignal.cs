namespace UiPath.Caching.Migration.Tests.Support;

internal static class TimerSignal
{
    public static Task<TimeSpan> NextTimerWhileRunningAsync(
        this ManualTimeProvider clock, Task operation, CancellationToken token) =>
        ReadWhileRunningAsync(ct => clock.NextTimerAsync(ct).AsTask(), operation, token);

    public static async Task<T> ReadWhileRunningAsync<T>(
        Func<CancellationToken, Task<T>> read, Task operation, CancellationToken token)
    {
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(token);
        var timer = read(waiting.Token);
        await Task.WhenAny(timer, operation);
        if (!timer.IsCompleted)
        {
            await waiting.CancelAsync();
            await operation;
            Assert.Fail("The operation completed before the expected timer was created.");
        }

        return await timer;
    }
}
