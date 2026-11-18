namespace UiPath.Caching.Migration;

internal sealed class Router : IRouter, IAsyncDisposable
{
    private static readonly TimeSpan PhaseLimit = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CleanupLimit = TimeSpan.FromSeconds(20);

    private readonly RouterCore _core;
    private readonly Dictionary<string, (Type Type, object Cache)> _caches = new(StringComparer.Ordinal);
#if NET9_0_OR_GREATER
    private readonly Lock _stopGate = new();
#else
    private readonly object _stopGate = new();
#endif
    private Task? _stop;

    public Router(RouterCore core, IEnumerable<Func<RouterContext, (string Name, Type Type, object Cache)>> registrations, RouterContext context)
        : this(core, registrations.Select(create => create(context)))
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

    /// <summary>Refuses new calls and cleans up once, whoever asks and whatever they cancel: waiting callers stop waiting, the cleanup carries on.</summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        Task stop;
        lock (_stopGate)
        {
            stop = _stop ??= CleanupAsync();
        }

        return stop.WaitAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None).ConfigureAwait(false);

    private async Task CleanupAsync()
    {
        _core.BeginStop();
        try
        {
            await StepsAsync().WaitAsync(CleanupLimit, _core.Clock).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Whatever is still running keeps the resources it uses; they are left to the finalizers.
        }
    }

    private async Task StepsAsync()
    {
        var stoppables = _caches.Values.Select(entry => entry.Cache).OfType<IStoppable>().ToArray();

        // Calls that were admitted finish first; a pending key stays protected until its own command can no longer land.
        var drained = await WithinAsync(_core.OperationsDrained).ConfigureAwait(false);
        var settled = await WithinAsync(Task.WhenAll(stoppables.Select(stoppable => stoppable.WhenPendingSettledAsync()))).ConfigureAwait(false);
        if (!drained || !settled)
        {
            foreach (var stoppable in stoppables)
            {
                await stoppable.InvalidateAsync().ConfigureAwait(false);
            }
        }

        foreach (var stoppable in stoppables)
        {
            await stoppable.FlushAsync().ConfigureAwait(false);
        }

        _core.CancelBackground();
        if (!await WithinAsync(_core.DrainAsync()).ConfigureAwait(false))
        {
            return;
        }

        foreach (var (_, cache) in _caches.Values)
        {
            (cache as IDisposable)?.Dispose();
        }

        _core.DisposeResources();
    }

    private async Task<bool> WithinAsync(Task task)
    {
        try
        {
            await task.WaitAsync(PhaseLimit, _core.Clock).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }
}
