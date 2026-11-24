namespace UiPath.Caching.Migration;

/// <summary>The mover's own measurements, for the health only: the time to move a key over a window, and its timeouts.</summary>
internal sealed class MoverLatency(TimeProvider time)
{
    /// <summary>The fewest samples a p99 is read from; fewer say nothing.</summary>
    public const int MinSamples = 20;

    private const int Capacity = 512;

#if NET9_0_OR_GREATER
    private readonly Lock _gate = new();
#else
    private readonly object _gate = new();
#endif
    private readonly (DateTimeOffset At, TimeSpan Elapsed)[] _ring = new (DateTimeOffset, TimeSpan)[Capacity];
    private int _next;
    private int _count;
    private int _timeouts;

    public void Record(TimeSpan elapsed)
    {
        lock (_gate)
        {
            _ring[_next] = (time.GetUtcNow(), elapsed);
            _next = (_next + 1) % Capacity;
            _count = Math.Min(_count + 1, Capacity);
        }
    }

    public void RecordTimeout()
    {
        lock (_gate)
        {
            _timeouts++;
        }
    }

    /// <summary>The p99 of the samples within <paramref name="window"/>, or null while there are too few; older samples are forgotten.</summary>
    public TimeSpan? P99(TimeSpan window)
    {
        var from = time.GetUtcNow() - window;
        var copy = new List<TimeSpan>(_count);
        lock (_gate)
        {
            for (var i = 0; i < _count; i++)
            {
                if (_ring[i].At >= from)
                {
                    copy.Add(_ring[i].Elapsed);
                }
            }
        }

        if (copy.Count < MinSamples)
        {
            return null;
        }

        copy.Sort();
        return copy[(int)Math.Ceiling(copy.Count * 0.99) - 1];
    }

    /// <summary>The timeouts since the last call.</summary>
    public int TakeTimeouts()
    {
        lock (_gate)
        {
            var taken = _timeouts;
            _timeouts = 0;
            return taken;
        }
    }
}
