using Microsoft.Extensions.Logging;

namespace UiPath.Caching.Migration;

/// <summary>
/// Keeps the router's entry in the node hash: written every interval and at once on a mode change, retried with backoff while Redis is down, never
/// throwing to a caller. It needs no started library. Readers count an entry for <see cref="Fresh"/>; routers prune entries older than <see cref="Gone"/>.
/// </summary>
internal sealed class RouterAdvertiser(
    RouterAdvertisementStore store,
    string nodeId,
    string build,
    IReadOnlyList<IAdvertisementTokens> sources,
    TimeSpan interval,
    TimeSpan livenessWindow,
    TimeProvider clock,
    ILogger logger)
{
    private const int PruneEvery = 10;
    private static readonly TimeSpan FirstRetry = TimeSpan.FromMilliseconds(500);

    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _writing = new(1, 1);
    private TaskCompletionSource _nudge = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _loop = Task.CompletedTask;
    private int _left;

    public string Field { get; } = nodeId + RouterAdvertisement.FieldSuffix;

    /// <summary>As the library's advertisements: the liveness window plus two intervals.</summary>
    public TimeSpan Fresh { get; } = livenessWindow + TimeSpan.FromTicks(interval.Ticks * 2);

    public TimeSpan Gone => TimeSpan.FromTicks(Math.Max(interval.Ticks * 10, Fresh.Ticks * 2));

    public void Start() => _loop = Task.Run(LoopAsync, CancellationToken.None);

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
        catch (Exception ex) when (ex is not OutOfMemoryException && !_stop.IsCancellationRequested)
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
        await _stop.CancelAsync().ConfigureAwait(false);
        await _loop.ConfigureAwait(false);
        Volatile.Write(ref _left, 1);
        using var timeout = new CancellationTokenSource(bound, clock);
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

    internal static TimeSpan Backoff(int failures, TimeSpan interval) =>
        TimeSpan.FromTicks(Math.Min(interval.Ticks, FirstRetry.Ticks << Math.Min(failures - 1, 16)));

    private async Task LoopAsync()
    {
        var failures = 0;
        var writes = 0;
        var delay = TimeSpan.Zero;
        while (!_stop.IsCancellationRequested)
        {
            if (!await WaitAsync(delay).ConfigureAwait(false))
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
    private async Task<bool> WaitAsync(TimeSpan delay)
    {
        var nudge = Volatile.Read(ref _nudge);
        using var wake = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        try
        {
            await Task.WhenAny(Task.Delay(delay, clock, wake.Token), nudge.Task).ConfigureAwait(false);
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
