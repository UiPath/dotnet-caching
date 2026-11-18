namespace UiPath.Caching.Migration;

/// <summary>Lets any number of writes of one cache run together, and a clear run alone: it waits for the writes under way and holds new ones back until it ends.</summary>
internal sealed class WriteFence
{
#if NET9_0_OR_GREATER
    private readonly Lock _gate = new();
#else
    private readonly object _gate = new();
#endif
    private readonly SemaphoreSlim _clearSerial = new(1, 1);
    private int _writers;
    private bool _clearing;
    private TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource? _idle;

    public async ValueTask EnterWriteAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task wait;
            lock (_gate)
            {
                if (!_clearing)
                {
                    _writers++;
                    return;
                }

                wait = _open.Task;
            }

            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public void ExitWrite()
    {
        lock (_gate)
        {
            if (--_writers == 0)
            {
                _idle?.TrySetResult();
            }
        }
    }

    public async Task EnterClearAsync(CancellationToken cancellationToken)
    {
        await _clearSerial.WaitAsync(cancellationToken).ConfigureAwait(false);
        Task idle;
        lock (_gate)
        {
            _clearing = true;
            _open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_writers == 0)
            {
                idle = Task.CompletedTask;
            }
            else
            {
                _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                idle = _idle.Task;
            }
        }

        try
        {
            await idle.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            ExitClear();
            throw;
        }
    }

    public void ExitClear()
    {
        lock (_gate)
        {
            _clearing = false;
            _idle = null;
            _open.TrySetResult();
        }

        _clearSerial.Release();
    }
}
