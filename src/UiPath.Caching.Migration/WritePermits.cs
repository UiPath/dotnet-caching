namespace UiPath.Caching.Migration;

/// <summary>The forward marked writes a node has outstanding, over all its caches: each logical write holds one from before its first marked set until its outcome has settled.</summary>
internal sealed class WritePermits(TimeProvider clock, int max) : IDisposable
{
    private static readonly TimeSpan HoldPoll = TimeSpan.FromSeconds(1);

    private readonly SemaphoreSlim _free = new(max, max);
    private readonly CancellationTokenSource _stop = new();
    private int _disposed;

    public int InFlight => max - _free.CurrentCount;

    // A disposed source reads as stopped, so a caller after Dispose behaves as before the source was released.
    private CancellationToken StopToken
    {
        get
        {
            try
            {
                return _stop.Token;
            }
            catch (ObjectDisposedException)
            {
                return new CancellationToken(true);
            }
        }
    }

    public Task WaitAsync(CancellationToken cancellationToken) => _free.WaitAsync(cancellationToken);

    public void Release() => _free.Release();

    /// <summary>Keeps the permit until <paramref name="until"/>, then returns it.</summary>
    public void ReleaseAt(DateTimeOffset until) => ReleaseWhen(() => false, until);

    /// <summary>Keeps the permit until <paramref name="done"/> holds or <paramref name="cap"/> has passed, then returns it.</summary>
    public void ReleaseWhen(Func<bool> done, DateTimeOffset cap)
    {
        if (clock.GetUtcNow() >= cap || done())
        {
            Release();
            return;
        }

        _ = Task.Run(
            async () =>
            {
                try
                {
                    while (clock.GetUtcNow() < cap && !done())
                    {
                        await Task.Delay(HoldPoll < cap - clock.GetUtcNow() ? HoldPoll : cap - clock.GetUtcNow(), clock, StopToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Stopped: the node is going away with its permits.
                }

                Release();
            },
            CancellationToken.None);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _stop.Cancel();
            _stop.Dispose();
        }
    }
}
