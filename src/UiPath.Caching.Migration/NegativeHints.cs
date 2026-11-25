using System.Collections.Concurrent;
using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Migration;

/// <summary>"The source side is absent for K", kept for a few seconds per state identity and side; a hint made under one identity or side is never read under another, and a local event or a gap drops it.</summary>
internal sealed class NegativeHints(TimeProvider clock, TimeSpan lifetime, int capacity)
{
    private readonly ConcurrentDictionary<string, (string Identity, MoveSide Side, long Until)> _hints = new(StringComparer.Ordinal);

    public int Count => _hints.Count;

    public bool Has(string key, string identity, MoveSide side) =>
        _hints.TryGetValue(key, out var hint) && hint.Identity == identity && hint.Side == side && clock.GetTimestamp() < hint.Until;

    public void Set(string key, string identity, MoveSide side)
    {
        if (_hints.Count >= capacity)
        {
            var now = clock.GetTimestamp();
            foreach (var pair in _hints.Where(p => p.Value.Until <= now))
            {
                _hints.TryRemove(pair.Key, out _);
            }

            if (_hints.Count >= capacity)
            {
                return;
            }
        }

        _hints[key] = (identity, side, clock.GetTimestamp() + (long)(lifetime.TotalSeconds * clock.TimestampFrequency));
    }

    public void Drop(string key) => _hints.TryRemove(key, out _);

    public void Clear() => _hints.Clear();
}
