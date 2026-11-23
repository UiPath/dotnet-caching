using System.Collections.Concurrent;
using System.Net;

namespace UiPath.Caching.KeyMove;

/// <summary>The masters that failed a barrier and when, so the way back skips such a master's barrier at once.</summary>
internal sealed class BarrierLedger(KeyMoveStateOptions options, TimeProvider time)
{
    private readonly ConcurrentDictionary<EndPoint, long> _failed = new();

    public void Record(EndPoint master) => _failed[master] = time.GetTimestamp();

    public bool FailedRecently(EndPoint master) =>
        _failed.TryGetValue(master, out var at) && time.GetElapsedTime(at) < options.BarrierFailureWindow;
}
