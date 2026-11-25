namespace UiPath.Caching.Migration.Tests.Support;

/// <summary>Polls a test condition within a bounded interval.</summary>
internal static class TestPoll
{
    public static async Task<bool> UntilAsync(Func<Task<bool>> condition, TimeSpan timeout, TimeProvider clock)
    {
        var start = clock.GetTimestamp();
        var token = TestContext.Current.CancellationToken;
        do
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), clock, token);
        }
        while (clock.GetElapsedTime(start) < timeout);

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
