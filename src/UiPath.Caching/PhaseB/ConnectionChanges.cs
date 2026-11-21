using StackExchange.Redis;

namespace UiPath.Caching.PhaseB;

/// <summary>
/// Counts connection failures, restores, internal errors and configuration changes on every multiplexer of a move, so a promotion that captured the
/// count before CAP abandons when it changed (PB step 10); for the instance move both servers count.
/// </summary>
internal sealed class ConnectionChanges : IDisposable
{
    private readonly IConnectionMultiplexer[] _multiplexers;
    private long _count;

    public ConnectionChanges(params IConnectionMultiplexer[] multiplexers)
    {
        _multiplexers = multiplexers;
        foreach (var multiplexer in multiplexers)
        {
            multiplexer.ConnectionFailed += OnConnection;
            multiplexer.ConnectionRestored += OnConnection;
            multiplexer.InternalError += OnInternalError;
            multiplexer.ConfigurationChanged += OnEndPoint;
            multiplexer.ConfigurationChangedBroadcast += OnEndPoint;
        }
    }

    public long Current => Interlocked.Read(ref _count);

    public bool ChangedSince(long mark) => Current != mark;

    public void Dispose()
    {
        foreach (var multiplexer in _multiplexers)
        {
            multiplexer.ConnectionFailed -= OnConnection;
            multiplexer.ConnectionRestored -= OnConnection;
            multiplexer.InternalError -= OnInternalError;
            multiplexer.ConfigurationChanged -= OnEndPoint;
            multiplexer.ConfigurationChangedBroadcast -= OnEndPoint;
        }
    }

    internal void Observe() => Interlocked.Increment(ref _count);

    private void OnConnection(object? sender, ConnectionFailedEventArgs e) => Observe();

    private void OnInternalError(object? sender, InternalErrorEventArgs e) => Observe();

    private void OnEndPoint(object? sender, EndPointEventArgs e) => Observe();
}
