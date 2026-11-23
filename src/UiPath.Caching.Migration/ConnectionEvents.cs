using StackExchange.Redis;

namespace UiPath.Caching.Migration;

/// <summary>Counts the connection restores and configuration changes of a multiplexer, so a promotion can tell that one happened since its capture.</summary>
internal sealed class ConnectionEvents
{
    private IConnectionMultiplexer? _attached;
    private long _count;

    public long Count() => Interlocked.Read(ref _count);

    public void Attach(IConnectionMultiplexer mux)
    {
        if (ReferenceEquals(_attached, mux))
        {
            return;
        }

        mux.ConnectionRestored += (_, _) => Interlocked.Increment(ref _count);
        mux.ConfigurationChanged += (_, _) => Interlocked.Increment(ref _count);
        mux.ConfigurationChangedBroadcast += (_, _) => Interlocked.Increment(ref _count);

        // A replaced multiplexer is itself a connection event.
        Interlocked.Increment(ref _count);
        _attached = mux;
    }
}
