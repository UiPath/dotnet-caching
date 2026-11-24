using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Migration;

internal sealed class Router : IRouter, IAsyncDisposable
{
    private static readonly TimeSpan PhaseLimit = TimeSpan.FromSeconds(5);

    private readonly RouterCore _core;
    private readonly Dictionary<string, (Type Type, object Cache)> _caches = new(StringComparer.Ordinal);
    private readonly IReadOnlyDictionary<string, string> _excluded;
#if NET9_0_OR_GREATER
    private readonly Lock _stopGate = new();
#else
    private readonly object _stopGate = new();
#endif
    private long _stopBegan;
    private TaskCompletionSource? _stop;
    private TaskCompletionSource? _cleaned;

    public Router(RouterCore core, IEnumerable<Func<RouterCore, (string Name, Type Type, object Cache)>> registrations, IReadOnlyDictionary<string, string>? excluded = null)
        : this(core, registrations.Select(create => create(core)), excluded)
    {
    }

    public Router(RouterCore core, IEnumerable<(string Name, Type Type, object Cache)> caches, IReadOnlyDictionary<string, string>? excluded = null)
    {
        _core = core;
        _excluded = excluded ?? new Dictionary<string, string>();
        foreach (var (name, type, cache) in caches)
        {
            _caches.Add(name, (type, cache));
        }
    }

    public event Action<RouterMode, RouterModeReason>? ModeChanged
    {
        add => _core.ModeChanged += value;
        remove => _core.ModeChanged -= value;
    }

    public RouterMode Mode => _core.Mode;

    public RouterModeReason ModeReason => _core.ModeReason;

    internal RouterAdvertiser? Advertiser { get; init; }

    internal ISerializerProxy<byte[]>? Envelope { get; init; }

    internal TimeProvider Clock => _core.Clock;

    internal KeyMoveNode? KeyMove { get; init; }

    public async Task ClearAsync(string cacheName, CancellationToken cancellationToken = default)
    {
        _core.ThrowIfStopped();
        if (!_caches.TryGetValue(cacheName, out var entry))
        {
            throw new KeyNotFoundException($"No cache named '{cacheName}' is registered with the router.");
        }

        await ((IRouterClear)entry.Cache).ClearAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task EnterBypassAsync(CancellationToken cancellationToken = default)
    {
        _core.ThrowIfStopped();
        return _core.EnterBypassAsync(cancellationToken);
    }

    public Task LeaveBypassAsync(CancellationToken cancellationToken = default)
    {
        _core.ThrowIfStopped();
        return _core.LeaveBypassAsync(cancellationToken);
    }


    public ICache<T> Get<T>(string cacheName)
        where T : class
    {
        _core.ThrowIfStopped();
        if (_excluded.TryGetValue(cacheName, out var reason))
        {
            throw new InvalidOperationException($"Cache '{cacheName}' is excluded from the router and stays on the application's own client: {reason}");
        }

        if (!_caches.TryGetValue(cacheName, out var entry))
        {
            throw new KeyNotFoundException($"No cache named '{cacheName}' is registered with the router.");
        }

        return entry.Cache as ICache<T>
            ?? throw new InvalidOperationException($"Cache '{cacheName}' holds {entry.Type.Name}, not {typeof(T).Name}.");
    }

    /// <summary>Refuses new calls and cleans up once, whoever asks and whatever they cancel. The wait is bounded; a call or a pending key that outlives the bound keeps the router's resources alive until it has ended.</summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource stopped;
        var first = false;
        lock (_stopGate)
        {
            if (_stop is null)
            {
                _stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _cleaned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                first = true;
            }

            stopped = _stop;
        }

        if (first)
        {
            _ = CleanupAsync(stopped, _cleaned!);
        }

        return stopped.Task.WaitAsync(cancellationToken);
    }

    /// <summary>Waits for in-flight writes and retries before releasing router resources.</summary>
    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        await _cleaned!.Task.ConfigureAwait(false);
    }

    internal RoutedCache<T>? Find<T>(string cacheName)
        where T : class =>
        _caches.TryGetValue(cacheName, out var entry) ? entry.Cache as RoutedCache<T> : null;

    internal void Start()
    {
        // The switch state is read before the first call is admitted.
        _core.Pins?.Initialize(_core.StartBound);
        _core.Start();
        AttachBridge();
        if (Advertiser is { } advertiser)
        {
            _core.ModeChanged += (_, _) => advertiser.Nudge();
            if (_core.Pins is { } changing)
            {
                changing.Changed += advertiser.Nudge;
            }

            advertiser.Start();
        }

        if (_core.Pins is { } pins)
        {
            _core.ModeChanged += (_, _) => pins.RequestRefresh();
            pins.Start();
        }
    }

    /// <summary>Delivers the older client's messages to this router's caches; needs no started library.</summary>
    internal void AttachBridge() =>
        _core.Bridge?.Attach(
            name => _caches.TryGetValue(name, out var entry) ? entry.Cache as IRouterParticipant : null,
            () =>
            {
                foreach (var (_, cache) in _caches.Values)
                {
                    (cache as IRouterParticipant)?.ResetLocal();
                }
            });

    private static void WarnInFlight(IRouterParticipant[] stoppables)
    {
        foreach (var stoppable in stoppables)
        {
            stoppable.WarnRetriesInFlight();
        }
    }

    private static Task StopRetries(IRouterParticipant[] stoppables) => Task.WhenAll(stoppables.Select(stoppable => stoppable.StopRetriesAsync()));

    private static Task PendingSettled(IRouterParticipant[] stoppables) => Task.WhenAll(stoppables.Select(stoppable => stoppable.WhenPendingSettledAsync()));

    private async Task CleanupAsync(TaskCompletionSource foreground, TaskCompletionSource cleaned)
    {
        try
        {
            await StepsAsync(foreground).ConfigureAwait(false);
            foreground.TrySetResult();
            cleaned.TrySetResult();
        }
        catch (Exception ex)
        {
            foreground.TrySetException(ex);
            cleaned.TrySetException(ex);
        }
        finally
        {
            // A stop that failed part way still takes the advertisement down; StopAsync never throws and runs once.
            if (Advertiser is { Stopped: false } advertiser)
            {
                await advertiser.StopAsync(PhaseLimit).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Completes <paramref name="foreground"/> once the stop's bounded part has run; whatever overran its bound carries on after that, in order, before anything is disposed.</summary>
    private async Task StepsAsync(TaskCompletionSource foreground)
    {
        _core.BeginStop();
        _stopBegan = _core.Clock.GetTimestamp();
        var stoppables = _caches.Values.Select(entry => entry.Cache).OfType<IRouterParticipant>().ToArray();

        await DrainOperationsAsync(stoppables, foreground).ConfigureAwait(false);
        await FinishStopAsync(stoppables, foreground).ConfigureAwait(false);
    }

    private async Task DrainOperationsAsync(IRouterParticipant[] stoppables, TaskCompletionSource foreground)
    {
        // Calls that were admitted finish first; a pending key stays protected until its own command can no longer land.
        var drained = await WithinAsync(_core.OperationsDrained).ConfigureAwait(false);
        var retries = drained ? StopRetries(stoppables) : Task.CompletedTask;

        var settled = await WithinAsync(PendingSettled(stoppables)).ConfigureAwait(false);
        if (!drained || !settled)
        {
            WarnInFlight(stoppables);
            // A later key-level invalidation covers any write still in flight.
            var invalidate = Task.WhenAll(stoppables.Select(stoppable => stoppable.InvalidateAsync()));
            await WithinAsync(invalidate).ConfigureAwait(false);
            foreground.TrySetResult();
            await invalidate.ConfigureAwait(false);

            await _core.OperationsDrained.ConfigureAwait(false);
            retries = StopRetries(stoppables);
            await _core.SettlePendingAsync(stoppables).ConfigureAwait(false);
        }

        // Foreground stop may time out while a retry still uses the router's resources.
        if (!await WithinAsync(retries).ConfigureAwait(false))
        {
            WarnInFlight(stoppables);
            foreground.TrySetResult();
        }

        await retries.ConfigureAwait(false);
    }

    private async Task FinishStopAsync(IRouterParticipant[] stoppables, TaskCompletionSource foreground)
    {
        var flush = Task.WhenAll(stoppables.Select(stoppable => stoppable.FlushAsync()));
        await CompleteStopPhaseAsync(flush, foreground).ConfigureAwait(false);
        _core.CancelBackground();

        // Started on the pool, so the phase bound runs even if the drain does not yield. The token is None on purpose: the drain runs to the end.
        var background = Task.Run(_core.DrainAsync, CancellationToken.None);
        await CompleteStopPhaseAsync(background, foreground).ConfigureAwait(false);
        _core.Bridge?.Detach();
        if (Advertiser is { } advertiser)
        {
            // Departs only after every call has ended, so a reader never sees this node gone while it still serves.
            var departed = advertiser.StopAsync(PhaseLimit);
            await CompleteStopPhaseAsync(departed, foreground).ConfigureAwait(false);
        }

        // A transition that an outside caller started holds the lock until it ends; the semaphore is disposed only after every one has.
        await BoundedStop.CompletesAsync(_core.TransitionsEnded, _core.CleanupBound).ConfigureAwait(false);
        foreach (var (_, cache) in _caches.Values)
        {
            (cache as IDisposable)?.Dispose();
        }

        var release = _core.ReleaseLibraryAsync();
        await CompleteStopPhaseAsync(release, foreground).ConfigureAwait(false);
        _core.DisposeResources();
    }

    private async Task CompleteStopPhaseAsync(Task phase, TaskCompletionSource foreground)
    {
        if (!await WithinAsync(phase).ConfigureAwait(false))
        {
            foreground.TrySetResult();
        }

        await phase.ConfigureAwait(false);
    }

    // Shutdown phases share the same foreground deadline.
    private async Task<bool> WithinAsync(Task task)
    {
        var left = PhaseLimit - _core.Clock.GetElapsedTime(Volatile.Read(ref _stopBegan));
        if (left <= TimeSpan.Zero)
        {
            return task.IsCompleted;
        }

        try
        {
            await task.WaitAsync(left, _core.Clock, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }
}
