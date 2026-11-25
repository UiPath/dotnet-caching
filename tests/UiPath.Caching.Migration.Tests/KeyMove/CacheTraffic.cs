namespace UiPath.Caching.Migration.Tests.KeyMove;

/// <summary>Runs independent cache operations and joins them when the test ends traffic.</summary>
internal sealed class CacheTraffic : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
    private readonly Task _started;
    private readonly Task _workers;
    private readonly TaskCompletionSource<bool> _nextRound = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public CacheTraffic(IReadOnlyList<Func<CancellationToken, Task>> operations)
    {
        var started = operations.Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        _workers = Task.WhenAll(operations.Select((operation, index) => Task.Run(() => RunAsync(operation, started[index], _stop.Token), CancellationToken.None)));
        _started = Task.WhenAll(started.Select(worker => worker.Task));
    }

    public Task WaitForStartAsync() => RunUntilAsync(_started.WaitAsync(TestContext.Current.CancellationToken));

    public async Task RunUntilAsync(Task condition)
    {
        if (await Task.WhenAny(condition, _workers) == _workers)
        {
            await _workers;
        }

        await condition;
    }

    public void Continue() => _nextRound.TrySetResult(true);

    public Task FinishAsync()
    {
        _nextRound.TrySetResult(false);
        return _workers.WaitAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        using (_stop)
        {
            await Task.WhenAll(_stop.CancelAsync(), _workers);
        }
    }

    private async Task RunAsync(Func<CancellationToken, Task> operation, TaskCompletionSource started, CancellationToken token)
    {
        try
        {
            await operation(token);
            started.SetResult();
            if (await _nextRound.Task.WaitAsync(token))
            {
                await operation(token);
            }
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
