namespace UiPath.Caching;

/// <summary>
/// Runs a removal a fixed delay after each request, in request order. One timer serves the cache: it is armed when
/// the queue gains its first item, re-armed for the next item's remaining delay, and idle while the queue is empty.
/// </summary>
internal sealed class RepeatInvalidations : IDisposable
{
    private readonly Lock _gate = new();
    private readonly Queue<(long Requested, string Key)> _queue = new();
    private readonly TimeProvider _clock;
    private readonly TimeSpan _delay;
    private readonly Action<string> _remove;
    private ITimer? _timer;
    private bool _armed;
    private bool _disposed;

    public RepeatInvalidations(TimeProvider clock, TimeSpan delay, Action<string> remove)
    {
        _clock = clock;
        _delay = delay;
        _remove = remove;
    }

    /// <summary>Removes <paramref name="key"/> again once the delay has passed. A key requested twice is removed twice.</summary>
    public void Schedule(string key)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _queue.Enqueue((_clock.GetTimestamp(), key));
            if (!_armed)
            {
                Arm(_delay);
            }
        }
    }

    public void Dispose()
    {
        ITimer? timer;
        lock (_gate)
        {
            _disposed = true;
            _queue.Clear();
            timer = _timer;
            _timer = null;
        }

        timer?.Dispose();
    }

    // Called with the gate held.
    private void Arm(TimeSpan due)
    {
        _timer ??= _clock.CreateTimer(static state => ((RepeatInvalidations)state!).Tick(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _armed = true;
        _timer.Change(due, Timeout.InfiniteTimeSpan);
    }

    private void Tick()
    {
        while (true)
        {
            string key;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                if (!_queue.TryPeek(out var next))
                {
                    _armed = false;
                    return;
                }

                var remaining = _delay - _clock.GetElapsedTime(next.Requested);
                if (remaining > TimeSpan.Zero)
                {
                    Arm(remaining);
                    return;
                }

                key = _queue.Dequeue().Key;
            }

            try
            {
                _remove(key);
            }
            catch (Exception)
            {
                // A removal that fails must not stop the ones queued behind it, nor end the timer thread.
            }
        }
    }
}
