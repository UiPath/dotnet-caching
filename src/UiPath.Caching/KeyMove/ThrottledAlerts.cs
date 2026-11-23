using System.Collections.Concurrent;

namespace UiPath.Caching.KeyMove;

/// <summary>Passes an alert on at most once for each group, code and detail in a period, so a scan that meets the same key every pass raises it once per poll.</summary>
internal sealed class ThrottledAlerts(Action<KeyMoveAlert>? inner, TimeProvider time, TimeSpan period)
{
    private const int PruneAbove = 10_000;

    private readonly ConcurrentDictionary<string, long> _sent = new(StringComparer.Ordinal);

    public void Raise(KeyMoveAlert alert)
    {
        if (inner is null)
        {
            return;
        }

        var now = time.GetTimestamp();
        var key = $"{alert.Group}|{alert.Code}|{alert.Detail}";
        if (_sent.TryGetValue(key, out var at) && time.GetElapsedTime(at, now) < period)
        {
            return;
        }

        if (_sent.Count > PruneAbove)
        {
            foreach (var old in _sent.Where(e => time.GetElapsedTime(e.Value, now) >= period).Select(e => e.Key).ToArray())
            {
                _sent.TryRemove(old, out _);
            }
        }

        _sent[key] = now;
        inner(alert);
    }
}
