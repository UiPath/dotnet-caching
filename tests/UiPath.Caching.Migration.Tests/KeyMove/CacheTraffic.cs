namespace UiPath.Caching.Migration.Tests.KeyMove;

/// <summary>Runs independent cache operations and joins them when the test ends traffic.</summary>
internal sealed class CacheTraffic : IAsyncDisposable
{
    private static readonly TimeSpan WorkerTimeout = TimeSpan.FromSeconds(10);
    private readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
    private readonly Task _started;
    private readonly Task _workers;
    private int _finishing;

    public CacheTraffic(IReadOnlyList<Func<CancellationToken, Task>> operations)
    {
        var started = operations.Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        _workers = Task.WhenAll(operations.Select((operation, index) => RunAsync(operation, started[index], _stop.Token)));
        _started = Task.WhenAll(started.Select(worker => worker.Task));
    }

    public Task WaitForStartAsync() => WaitForWorkersAsync(_started, "Cache traffic workers did not finish their first operation.");

    public async Task RunUntilAsync(Task condition)
    {
        var finished = await Task.WhenAny(condition, _workers);
        await finished;
    }

    public Task FinishAsync()
    {
        Volatile.Write(ref _finishing, 1);
        return WaitForWorkersAsync(_workers, "Cache traffic workers did not finish their current operations.");
    }

    public async ValueTask DisposeAsync()
    {
        using (_stop)
        {
            await WaitForWorkersAsync(Task.WhenAll(_stop.CancelAsync(), _workers), "Cache traffic workers did not stop after cancellation.");
        }
    }

    private static async Task WaitForWorkersAsync(Task workers, string message)
    {
        try
        {
            await workers.WaitAsync(WorkerTimeout, MovingCacheHost.Clock);
        }
        catch (TimeoutException ex) when (!workers.IsCompleted)
        {
            throw new TimeoutException(message, ex);
        }
    }

    private async Task RunAsync(Func<CancellationToken, Task> operation, TaskCompletionSource started, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested && Volatile.Read(ref _finishing) == 0)
            {
                await operation(token);
                started.TrySetResult();
                await Task.Delay(TimeSpan.FromMilliseconds(20), MovingCacheHost.Clock, token);
            }
            started.TrySetCanceled(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            started.TrySetCanceled(token);
        }
        catch (Exception ex)
        {
            started.TrySetException(ex);
            await _stop.CancelAsync();
            throw;
        }
    }
}
