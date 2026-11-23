using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace UiPath.Caching.Migration;

/// <summary>What the caches of one node share: the clock, the local tier, the path predicate and the background work.</summary>
internal sealed partial class RouterCore(TimeProvider clock, Func<string, bool> useLibrary, ILogger logger)
{
    private static readonly TimeSpan LogInterval = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<Task, byte> _background = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly TaskCompletionSource _operationsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _lastLoggedAt = long.MinValue;
    private int _stopped;
    private int _active;

    public TimeProvider Clock { get; } = clock;

    public LocalTier Tier { get; } = new(clock);

    public bool IsIdle => _background.IsEmpty;

    public int BackgroundCount => _background.Count;

    public bool IsStopped => Volatile.Read(ref _stopped) != 0;

    /// <summary>Completes once the router is stopped and every admitted operation has ended.</summary>
    public Task OperationsDrained => _operationsDrained.Task;

    public void ThrowIfStopped() => ObjectDisposedException.ThrowIf(IsStopped, typeof(IRouter));

    public void Run(Func<CancellationToken, Task> work)
    {
        if (_stopping.IsCancellationRequested)
        {
            return;
        }

        var task = Task.Run(() => work(_stopping.Token), CancellationToken.None);
        _background.TryAdd(task, 0);
        _ = task.ContinueWith(
            done =>
            {
                _background.TryRemove(done, out _);
                return done.Exception;
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public void BeginStop()
    {
        Interlocked.Exchange(ref _stopped, 1);
        if (Volatile.Read(ref _transitions) == 0)
        {
            _transitionsEnded.TrySetResult();
        }

        if (Volatile.Read(ref _active) == 0)
        {
            _operationsDrained.TrySetResult();
        }
    }

    public void CancelBackground() => _stopping.Cancel();

    /// <summary>Completes when the background work has ended; a task that fails or is stopped counts as ended.</summary>
    public async Task DrainAsync()
    {
        while (!_background.IsEmpty)
        {
            await Task.WhenAll(_background.Keys.Select(Ended)).ConfigureAwait(false);
        }
    }

    public void LogPublishFailed(string cache)
    {
        var now = Clock.GetTimestamp();
        var last = Interlocked.Read(ref _lastLoggedAt);
        if (last != long.MinValue && Clock.GetElapsedTime(last, now) < LogInterval)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _lastLoggedAt, now, last) == last)
        {
            PublishFailedLog(cache);
        }
    }

    /// <summary>Only after <see cref="DrainAsync"/> has completed: no task is left to use them.</summary>
    public void DisposeResources()
    {
        _stopping.Dispose();
        if (Volatile.Read(ref _transitions) == 0)
        {
            _transition.Dispose();
        }

        Tier.Dispose();
    }

    private static async Task Ended(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Whatever ended the task, it has ended.
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Publishing an invalidation for cache {Cache} failed; peers may serve a local copy until its cap, and the publish is retried.")]
    private partial void PublishFailedLog(string cache);
}
