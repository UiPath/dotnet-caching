namespace UiPath.Caching.Migration;

internal sealed class Router : IRouter, IAsyncDisposable
{
    private static readonly TimeSpan PhaseLimit = TimeSpan.FromSeconds(5);

    private readonly RouterCore _core;
    private readonly Dictionary<string, (Type Type, object Cache)> _caches = new(StringComparer.Ordinal);
#if NET9_0_OR_GREATER
    private readonly Lock _stopGate = new();
#else
    private readonly object _stopGate = new();
#endif
    private TaskCompletionSource? _stop;
    private TaskCompletionSource? _cleaned;

    public Router(RouterCore core, IEnumerable<Func<RouterCore, (string Name, Type Type, object Cache)>> registrations)
        : this(core, registrations.Select(create => create(core)))
    {
    }

    public Router(RouterCore core, IEnumerable<(string Name, Type Type, object Cache)> caches)
    {
        _core = core;
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


    public IRoutedCache<T> Get<T>(string cacheName)
        where T : class
    {
        _core.ThrowIfStopped();
        if (!_caches.TryGetValue(cacheName, out var entry))
        {
            throw new KeyNotFoundException($"No cache named '{cacheName}' is registered with the router.");
        }

        return entry.Cache as IRoutedCache<T>
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

    /// <summary>Waits for the whole cleanup, not only the bounded stop: what the router uses is released with the provider, after this returns.</summary>
    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        await _cleaned!.Task.ConfigureAwait(false);
    }

    internal void Start()
    {
        // The switch state is read before the first call is admitted.
        _core.Pins?.Initialize(_core.StartBound);
        _core.Start();
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
        var stoppables = _caches.Values.Select(entry => entry.Cache).OfType<IRouterParticipant>().ToArray();

        // Calls that were admitted finish first; a pending key stays protected until its own command can no longer land.
        var drained = await WithinAsync(_core.OperationsDrained).ConfigureAwait(false);
        var settled = await WithinAsync(PendingSettled(stoppables)).ConfigureAwait(false);
        if (!drained || !settled)
        {
            var invalidate = Task.WhenAll(stoppables.Select(stoppable => stoppable.InvalidateAsync()));
            await WithinAsync(invalidate).ConfigureAwait(false);
            foreground.TrySetResult();
            await invalidate.ConfigureAwait(false);
            await _core.OperationsDrained.ConfigureAwait(false);
            await PendingSettled(stoppables).ConfigureAwait(false);
        }

        var flush = Task.WhenAll(stoppables.Select(stoppable => stoppable.FlushAsync()));
        if (!await WithinAsync(flush).ConfigureAwait(false))
        {
            foreground.TrySetResult();
        }

        await flush.ConfigureAwait(false);
        _core.CancelBackground();
        var background = _core.DrainAsync();
        if (!await WithinAsync(background).ConfigureAwait(false))
        {
            foreground.TrySetResult();
        }

        await background.ConfigureAwait(false);
        if (Advertiser is { } advertiser)
        {
            // Departs only after every call has ended, so a reader never sees this node gone while it still serves.
            var departed = advertiser.StopAsync(PhaseLimit);
            if (!await WithinAsync(departed).ConfigureAwait(false))
            {
                foreground.TrySetResult();
            }

            await departed.ConfigureAwait(false);
        }

        // A transition that an outside caller started holds the lock until it ends; the semaphore is disposed only after every one has.
        await BoundedStop.CompletesAsync(_core.TransitionsEnded, _core.CleanupBound).ConfigureAwait(false);
        foreach (var (_, cache) in _caches.Values)
        {
            (cache as IDisposable)?.Dispose();
        }

        var release = _core.ReleaseLibraryAsync();
        if (!await WithinAsync(release).ConfigureAwait(false))
        {
            foreground.TrySetResult();
        }

        await release.ConfigureAwait(false);
        _core.DisposeResources();
    }

    private async Task<bool> WithinAsync(Task task)
    {
        try
        {
            await task.WaitAsync(PhaseLimit, _core.Clock, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }
}