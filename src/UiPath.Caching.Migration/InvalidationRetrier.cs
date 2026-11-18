namespace UiPath.Caching.Migration;

/// <summary>One worker per cache for invalidations that could not be published: each key once, the time since its first failure counted against the local cap, and one topic invalidation instead of more than <see cref="MaxKeys"/> keys.</summary>
internal sealed class InvalidationRetrier(
    RouterCore core,
    TimeSpan cap,
    Func<string, ValueTask<bool>> publishKey,
    Func<ValueTask<bool>> publishClear,
    Action<Route> failed)
{
    internal const int MaxKeys = 1024;

    private static readonly TimeSpan FirstDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(5);

#if NET9_0_OR_GREATER
    private readonly Lock _gate = new();
#else
    private readonly object _gate = new();
#endif
    private readonly Dictionary<string, Waiting> _keys = new(StringComparer.Ordinal);
    private Waiting? _clear;
    private bool _running;

    public int WaitingKeys
    {
        get
        {
            lock (_gate)
            {
                return _keys.Count;
            }
        }
    }

    public bool ClearWaiting
    {
        get
        {
            lock (_gate)
            {
                return _clear is not null;
            }
        }
    }

    public void ReportKey(string key, Route route)
    {
        if (cap <= TimeSpan.Zero)
        {
            return;
        }

        lock (_gate)
        {
            var expires = core.Clock.GetUtcNow() + cap;
            if (_clear is not null || (_keys.Count >= MaxKeys && !_keys.ContainsKey(key)))
            {
                _clear = new Waiting(expires, route);
                _keys.Clear();
            }
            else
            {
                _keys[key] = new Waiting(expires, route);
            }

            Start();
        }
    }

    public void ReportClear(Route route)
    {
        if (cap <= TimeSpan.Zero)
        {
            return;
        }

        lock (_gate)
        {
            _clear = new Waiting(core.Clock.GetUtcNow() + cap, route);
            _keys.Clear();
            Start();
        }
    }

    /// <summary>At stop: one pass over what waits, best effort.</summary>
    public Task FlushAsync() => AttemptAsync();

    private static TimeSpan Backoff(TimeSpan delay) => delay + delay < MaxDelay ? delay + delay : MaxDelay;

    private static async Task<bool> TryAsync(Func<ValueTask<bool>> publish)
    {
        try
        {
            return await publish().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    private void Start()
    {
        if (!_running)
        {
            _running = true;
            core.Run(WorkAsync);
        }
    }

    private async Task WorkAsync(CancellationToken stopping)
    {
        var delay = FirstDelay;
        while (true)
        {
            TimeSpan wait;
            lock (_gate)
            {
                Prune();
                if (_clear is null && _keys.Count == 0)
                {
                    _running = false;
                    return;
                }

                var last = _clear?.Expires ?? _keys.Values.Max(w => w.Expires);
                var remaining = last - core.Clock.GetUtcNow();
                wait = delay < remaining ? delay : remaining;
            }

            try
            {
                await Task.Delay(wait, core.Clock, stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                lock (_gate)
                {
                    _running = false;
                }

                return;
            }

            var succeeded = await AttemptAsync().ConfigureAwait(false);
            delay = succeeded ? FirstDelay : Backoff(delay);
        }
    }

    private async Task<bool> AttemptAsync()
    {
        Waiting? clear;
        KeyValuePair<string, Waiting>[] keys;
        lock (_gate)
        {
            Prune();
            clear = _clear;
            keys = [.. _keys];
        }

        if (clear is not null)
        {
            if (!await TryAsync(publishClear).ConfigureAwait(false))
            {
                failed(clear.Route);
                return false;
            }

            lock (_gate)
            {
                if (ReferenceEquals(_clear, clear))
                {
                    _clear = null;
                }
            }

            return true;
        }

        foreach (var (key, waiting) in keys)
        {
            if (!await TryAsync(() => publishKey(key)).ConfigureAwait(false))
            {
                failed(waiting.Route);
                return false;
            }

            lock (_gate)
            {
                if (_keys.TryGetValue(key, out var current) && ReferenceEquals(current, waiting))
                {
                    _keys.Remove(key);
                }
            }
        }

        return true;
    }

    private void Prune()
    {
        var now = core.Clock.GetUtcNow();
        if (_clear is not null && _clear.Expires <= now)
        {
            _clear = null;
        }

        foreach (var key in _keys.Where(pair => pair.Value.Expires <= now).Select(pair => pair.Key).ToArray())
        {
            _keys.Remove(key);
        }
    }

    private sealed record Waiting(DateTimeOffset Expires, Route Route);
}
