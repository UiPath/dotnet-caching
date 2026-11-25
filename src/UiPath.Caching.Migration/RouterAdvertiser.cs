using Microsoft.Extensions.Logging;

namespace UiPath.Caching.Migration;

/// <summary>Keeps the router's entry in the node hash: every interval, at once on a mode change, retried with backoff, never throwing to a caller.</summary>
internal sealed class RouterAdvertiser(
    RouterAdvertisementStore store,
    string nodeId,
    string build,
    IReadOnlyList<IAdvertisementTokens> sources,
    TimeSpan interval,
    TimeSpan livenessWindow,
    TimeProvider clock,
    ILogger logger) : IDisposable
{
    private const int PruneEvery = 10;
    private static readonly TimeSpan FirstRetry = TimeSpan.FromMilliseconds(500);

#if NET9_0_OR_GREATER
    private readonly Lock _lifetimeGate = new();
#else
    private readonly object _lifetimeGate = new();
#endif
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _writing = new(1, 1);
    private TaskCompletionSource _nudge = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _loop;
    private int _left;

    public string Field { get; } = nodeId + RouterAdvertisement.FieldSuffix;

    /// <summary>As the library's advertisements: the liveness window plus two intervals.</summary>
    public TimeSpan Fresh { get; } = livenessWindow + TimeSpan.FromTicks(interval.Ticks * 2);

    public TimeSpan Gone => TimeSpan.FromTicks(Math.Max(interval.Ticks * 10, Fresh.Ticks * 2));

    public bool Stopped => Volatile.Read(ref _left) == 1;

    internal Task Shutdown { get; private set; } = Task.CompletedTask;

    public void Start()
    {
        lock (_lifetimeGate)
        {
            if (!Stopped && _loop is null)
            {
                _loop = Task.Run(LoopAsync, CancellationToken.None);
            }
        }
    }

    /// <summary>Asks for a write now, for a change a reader should see before the next interval.</summary>
    public void Nudge() => Volatile.Read(ref _nudge).TrySetResult();

    public RouterAdvertisement Current()
    {
        var tokens = new List<string>();
        foreach (var source in sources)
        {
            source.AddTo(tokens);
        }

        return new RouterAdvertisement(nodeId, build, tokens, Volatile.Read(ref _left) == 1);
    }

    /// <summary>One write; false when it failed, which is logged and left to the next attempt.</summary>
    public async Task<bool> WriteAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _writing.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await store.WriteAsync(Field, Current(), cancellationToken).ConfigureAwait(false);
                return true;
            }
            finally
            {
                _writing.Release();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && !Stopped)
        {
            logger.LogWarning(ex, "The router's node advertisement could not be written; retrying.");
            return false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    /// <summary>Stops the loop and writes the departing entry once, within <paramref name="bound"/>; an entry that is not replaced expires on its own.</summary>
    public async Task StopAsync(TimeSpan bound)
    {
        if (!TryBeginStop(out var loop))
        {
            return;
        }

        var cancellation = CancelAndDisposeWhenDone(loop);
        using var timeout = new CancellationTokenSource(bound, clock);
        await cancellation.ConfigureAwait(false);
        try
        {
            await loop.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A loop stuck past the bound is left behind, and one that faulted was logged; neither holds the stop.
        }

        try
        {
            await _writing.WaitAsync(timeout.Token).ConfigureAwait(false);
            try
            {
                await store.WriteAsync(Field, Current(), timeout.Token).ConfigureAwait(false);
            }
            finally
            {
                _writing.Release();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogDebug(ex, "The router's departing advertisement could not be written; its entry expires on its own.");
        }
    }

    public void Dispose()
    {
        if (TryBeginStop(out var loop))
        {
            _ = CancelAndDisposeWhenDone(loop);
        }
    }

    internal static TimeSpan Backoff(int failures, TimeSpan interval) =>
        TimeSpan.FromTicks(Math.Min(interval.Ticks, FirstRetry.Ticks << Math.Min(failures - 1, 16)));

    private bool TryBeginStop(out Task loop)
    {
        lock (_lifetimeGate)
        {
            loop = _loop ?? Task.CompletedTask;
            if (Stopped)
            {
                return false;
            }

            Volatile.Write(ref _left, 1);
            return true;
        }
    }

    private Task CancelAndDisposeWhenDone(Task loop)
    {
        var cancellation = _stop.CancelAsync();
        Shutdown = Task.WhenAll(loop, cancellation).ContinueWith(
            DisposeStopSource, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return cancellation;
    }

    private void DisposeStopSource(Task completed)
    {
        _ = completed.Exception;
        _stop.Dispose();
    }

    private async Task LoopAsync()
    {
        try
        {
            await RunAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && !_stop.IsCancellationRequested)
        {
            logger.LogError(ex, "The router's node advertisement loop stopped; its entry expires on its own.");
        }
    }

    private async Task RunAsync()
    {
        var failures = 0;
        var writes = 0;
        var delay = TimeSpan.Zero;
        while (!_stop.IsCancellationRequested)
        {
            // After a failure the backoff holds even when a change asks for a write; the retry carries the change.
            if (!await WaitAsync(delay, honorNudge: failures == 0).ConfigureAwait(false))
            {
                return;
            }

            if (await WriteAsync(_stop.Token).ConfigureAwait(false))
            {
                failures = 0;
                delay = interval;
                if (++writes % PruneEvery == 0)
                {
                    await PruneAsync().ConfigureAwait(false);
                }
            }
            else
            {
                delay = Backoff(++failures, interval);
            }
        }
    }

    /// <summary>False once stopping.</summary>
    private async Task<bool> WaitAsync(TimeSpan delay, bool honorNudge)
    {
        var nudge = Volatile.Read(ref _nudge);
        using var wake = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        try
        {
            var delayed = Task.Delay(delay, clock, wake.Token);
            await (honorNudge ? Task.WhenAny(delayed, nudge.Task) : Task.WhenAny(delayed)).ConfigureAwait(false);
        }
        finally
        {
            await wake.CancelAsync().ConfigureAwait(false);
        }

        if (nudge.Task.IsCompleted)
        {
            Interlocked.CompareExchange(ref _nudge, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), nudge);
        }

        return !_stop.IsCancellationRequested;
    }

    private async Task PruneAsync()
    {
        try
        {
            var entries = await store.ReadAsync(_stop.Token).ConfigureAwait(false);
            await store.PruneAsync([.. entries.Where(e => e.Age > Gone).Select(e => (e.Field, e.Stamp))], _stop.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogDebug(ex, "Stale router advertisements could not be pruned; the next pass retries.");
        }
    }
}
