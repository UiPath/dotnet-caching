namespace UiPath.Caching.Tests.Fakes;

/// <summary>A pulse that wakes waiters; it allocates only while someone waits, so allocation tests do not count it.</summary>
internal sealed class AsyncTestSignal
{
    private TaskCompletionSource? _changed;

    public void Pulse() => Interlocked.Exchange(ref _changed, null)?.TrySetResult();

    public async Task WaitUntil(Func<bool> condition)
    {
        while (true)
        {
            var changed = Next();
            if (condition())
            {
                return;
            }

            await changed.WaitAsync(TestContext.Current.CancellationToken);
        }
    }

    private Task Next()
    {
        while (true)
        {
            if (Volatile.Read(ref _changed) is { } current)
            {
                return current.Task;
            }

            var created = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (Interlocked.CompareExchange(ref _changed, created, null) is null)
            {
                return created.Task;
            }
        }
    }
}
