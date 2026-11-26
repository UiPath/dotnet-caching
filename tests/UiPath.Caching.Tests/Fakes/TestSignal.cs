namespace UiPath.Caching.Tests.Fakes;

/// <summary>A pulse that wakes waiters; it allocates only while someone waits, so allocation tests do not count it.</summary>
internal sealed class TestSignal
{
    private readonly object _gate = new();
    private TaskCompletionSource? _next;

    public void Pulse()
    {
        TaskCompletionSource? previous;
        lock (_gate)
        {
            previous = _next;
            _next = null;
        }

        previous?.TrySetResult();
    }

    public async Task UntilAsync(Func<bool> condition, CancellationToken token)
    {
        while (true)
        {
            Task next;
            lock (_gate)
            {
                next = (_next ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            }

            if (condition())
            {
                return;
            }

            await next.WaitAsync(token);
        }
    }
}
