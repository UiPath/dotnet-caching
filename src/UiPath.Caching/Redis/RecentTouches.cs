namespace UiPath.Caching.Redis;

/// <summary>The keys a node wrote or saw invalidated within the window, which a replica may still hold the old value of.</summary>
internal sealed class RecentTouches(TimeProvider clock, TimeSpan window)
{
    internal const int MaxKeys = 10_000;

    internal static readonly TimeSpan DefaultWindow = TimeSpan.FromSeconds(1);

    private readonly long _window = window.Ticks;
    // Monotonic: a wall-clock step would end, or stretch, every window at once.
    private readonly long _start = clock.GetTimestamp();
    private readonly Dictionary<RedisKey, long> _until = [];
    private readonly Lock _gate = new();
    private long _allUntil;
    private long _purgedAt;

    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _until.Count;
            }
        }
    }

    internal void Touch(RedisKey key)
    {
        var now = Now();
        lock (_gate)
        {
            TouchLocked(key, now);
        }
    }

    internal void Touch(RedisKey[] keys)
    {
        var now = Now();
        lock (_gate)
        {
            foreach (var key in keys)
            {
                TouchLocked(key, now);
            }
        }
    }

    internal void TouchAll()
    {
        var now = Now();
        lock (_gate)
        {
            _allUntil = now + _window;
        }
    }

    internal bool IsGuarded(RedisKey key)
    {
        var now = Now();
        lock (_gate)
        {
            return now < _allUntil || (_until.TryGetValue(key, out var until) && now < until);
        }
    }

    internal bool IsGuarded(RedisKey[] keys)
    {
        var now = Now();
        lock (_gate)
        {
            if (now < _allUntil)
            {
                return true;
            }

            foreach (var key in keys)
            {
                if (_until.TryGetValue(key, out var until) && now < until)
                {
                    return true;
                }
            }

            return false;
        }
    }

    private long Now() => clock.GetElapsedTime(_start).Ticks;

    private void TouchLocked(RedisKey key, long now)
    {
        if (_until.Count >= MaxKeys && !_until.ContainsKey(key) && !PurgeExpired(now))
        {
            // Full of live entries: every read goes to the master until the window drains, rather than forgetting a key.
            _allUntil = now + _window;
            return;
        }

        _until[key] = now + _window;
    }

    private bool PurgeExpired(long now)
    {
        // Purging walks every entry, so a flood of new keys does it at most once a quarter window.
        if (now - _purgedAt >= _window / 4)
        {
            _purgedAt = now;
            foreach (var (key, until) in _until)
            {
                if (until <= now)
                {
                    _until.Remove(key);
                }
            }
        }

        return _until.Count < MaxKeys;
    }
}
