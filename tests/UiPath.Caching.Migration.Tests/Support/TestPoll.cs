namespace UiPath.Caching.Migration.Tests.Support;

/// <summary>Polls a test condition within a bounded interval.</summary>
internal static class TestPoll
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(50);

    public static async Task<bool> UntilAsync(Func<Task<bool>> condition, TimeSpan timeout, TimeProvider clock)
    {
        var start = clock.GetTimestamp();
        var token = TestContext.Current.CancellationToken;
        var remaining = timeout;
        while (remaining > TimeSpan.Zero)
        {
            var pending = condition();
            try
            {
                if (await pending.WaitAsync(remaining, clock, token))
                {
                    return true;
                }
            }
            catch (TimeoutException) when (!pending.IsFaulted)
            {
                return false;
            }
            finally
            {
                pending.Observe();
            }

            remaining = timeout - clock.GetElapsedTime(start);
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining < Interval ? remaining : Interval, clock, token);
                remaining = timeout - clock.GetElapsedTime(start);
            }
        }

        return false;
    }

    public static async Task WaitAsync(Func<Task<bool>> condition, Func<string> description)
    {
        if (!await UntilAsync(condition, TimeSpan.FromSeconds(30), TimeProvider.System))
        {
            throw new Xunit.Sdk.XunitException(description());
        }
    }
}
