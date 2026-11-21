using StackExchange.Redis;
using UiPath.Caching.Redis;

namespace UiPath.Caching.PhaseB;

/// <summary>Counts connection failures, restores, internal errors and configuration changes on every multiplexer of a move.</summary>
/// <remarks>
/// A promotion that captured the count before CAP abandons when it changed (PB step 10); for the instance move both servers count. Over connectors it
/// follows a reconnect: the replacement counts as a change, the old multiplexer is detached and the new one attached.
/// </remarks>
internal sealed class ConnectionChanges : IDisposable
{
    private readonly object _gate = new();
    private readonly IConnectionMultiplexer[] _multiplexers;
    private readonly IRedisConnector[] _connectors;
    private readonly EventHandler[] _handlers;
    private long _count;
    private bool _disposed;

    public ConnectionChanges(params IConnectionMultiplexer[] multiplexers)
    {
        _multiplexers = [.. multiplexers];
        _connectors = [];
        _handlers = [];
        foreach (var multiplexer in _multiplexers)
        {
            Attach(multiplexer);
        }
    }

    public ConnectionChanges(params IRedisConnector[] connectors)
    {
        _connectors = [.. connectors];
        _multiplexers = new IConnectionMultiplexer[connectors.Length];
        _handlers = new EventHandler[connectors.Length];
        lock (_gate)
        {
            for (var i = 0; i < _connectors.Length; i++)
            {
                var index = i;
                _handlers[i] = (_, _) => OnReconnected(index);

                // Subscribe before the multiplexer is captured, then check again: a reconnect in between is followed, not missed.
                _connectors[i].OnReconnected += _handlers[i];
                _multiplexers[i] = _connectors[i].Database.Multiplexer;
                Attach(_multiplexers[i]);
                Follow(index);
            }
        }
    }

    public long Current => Interlocked.Read(ref _count);

    public bool ChangedSince(long mark) => Current != mark;

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            for (var i = 0; i < _connectors.Length; i++)
            {
                _connectors[i].OnReconnected -= _handlers[i];
            }

            foreach (var multiplexer in _multiplexers)
            {
                Detach(multiplexer);
            }
        }
    }

    internal void Observe() => Interlocked.Increment(ref _count);

    private void OnReconnected(int index)
    {
        Observe();
        lock (_gate)
        {
            if (!_disposed)
            {
                Follow(index);
            }
        }
    }

    private void Follow(int index)
    {
        var current = _connectors[index].Database.Multiplexer;
        if (ReferenceEquals(current, _multiplexers[index]))
        {
            return;
        }

        Detach(_multiplexers[index]);
        _multiplexers[index] = current;
        Attach(current);
    }

    private void Attach(IConnectionMultiplexer multiplexer)
    {
        multiplexer.ConnectionFailed += OnConnection;
        multiplexer.ConnectionRestored += OnConnection;
        multiplexer.InternalError += OnInternalError;
        multiplexer.ConfigurationChanged += OnEndPoint;
        multiplexer.ConfigurationChangedBroadcast += OnEndPoint;
    }

    private void Detach(IConnectionMultiplexer multiplexer)
    {
        multiplexer.ConnectionFailed -= OnConnection;
        multiplexer.ConnectionRestored -= OnConnection;
        multiplexer.InternalError -= OnInternalError;
        multiplexer.ConfigurationChanged -= OnEndPoint;
        multiplexer.ConfigurationChangedBroadcast -= OnEndPoint;
    }

    private void OnConnection(object? sender, ConnectionFailedEventArgs e) => Observe();

    private void OnInternalError(object? sender, InternalErrorEventArgs e) => Observe();

    private void OnEndPoint(object? sender, EndPointEventArgs e) => Observe();
}
