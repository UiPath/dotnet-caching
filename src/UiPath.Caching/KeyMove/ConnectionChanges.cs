using StackExchange.Redis;
using UiPath.Caching.Redis;

namespace UiPath.Caching.KeyMove;

/// <summary>Counts connection failures, restores, internal errors and configuration changes on every multiplexer of a move.</summary>
/// <remarks>
/// A promotion that captured the count before CAP abandons when it changed; for the instance move both servers count. Over connectors it
/// follows a reconnect: the replacement counts as a change, the old multiplexer is detached and the new one attached.
/// </remarks>
internal sealed class ConnectionChanges : IDisposable
{
#if NET9_0_OR_GREATER
    private readonly Lock _gate = new();
#else
    private readonly object _gate = new();
#endif
    private readonly IConnectionMultiplexer[] _multiplexers;
    private readonly IRedisConnector[] _connectors;
    private readonly EventHandler[] _handlers;
    private readonly List<(IConnectionMultiplexer Multiplexer, RecordSide Side)> _watched = [];
    private long _count;
    private long _sourceCount;
    private long _destinationCount;
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

    /// <summary>The events seen on the Src side: the first multiplexer or connector, and every multiplexer watched for the Src.</summary>
    public long SourceCurrent => Interlocked.Read(ref _sourceCount);

    /// <summary>The events seen on the Dst side: every multiplexer or connector after the first, and every multiplexer watched for the Dst.</summary>
    public long DestinationCurrent => Interlocked.Read(ref _destinationCount);

    public bool ChangedSince(long mark) => Current != mark;

    /// <summary>Whether the events of one side changed since <paramref name="mark"/>, a value of <see cref="SourceCurrent"/> or <see cref="DestinationCurrent"/>.</summary>
    public bool ChangedSince(RecordSide side, long mark) => (side == RecordSide.Src ? SourceCurrent : DestinationCurrent) != mark;

    /// <summary>Counts the events of one more multiplexer for a side, for example the dedicated connection of a barrier; the caller detaches nothing, the multiplexer's life is its own.</summary>
    public void Watch(IConnectionMultiplexer multiplexer, RecordSide side)
    {
        ArgumentNullException.ThrowIfNull(multiplexer);
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _watched.Add((multiplexer, side));
            Attach(multiplexer);
        }
    }

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

            foreach (var (multiplexer, _) in _watched)
            {
                Detach(multiplexer);
            }
        }
    }

    internal void Observe() => Observe(RecordSide.Src);

    internal void Observe(RecordSide side)
    {
        Interlocked.Increment(ref _count);
        Interlocked.Increment(ref side == RecordSide.Src ? ref _sourceCount : ref _destinationCount);
    }

    private RecordSide SideOf(object? sender)
    {
        lock (_gate)
        {
            for (var i = 0; i < _multiplexers.Length; i++)
            {
                if (ReferenceEquals(_multiplexers[i], sender))
                {
                    return i == 0 ? RecordSide.Src : RecordSide.Dst;
                }
            }

            return _watched.FirstOrDefault(w => ReferenceEquals(w.Multiplexer, sender)).Side;
        }
    }

    private void OnReconnected(int index)
    {
        Observe(index == 0 ? RecordSide.Src : RecordSide.Dst);
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

    private void OnConnection(object? sender, ConnectionFailedEventArgs e) => Observe(SideOf(sender));

    private void OnInternalError(object? sender, InternalErrorEventArgs e) => Observe(SideOf(sender));

    private void OnEndPoint(object? sender, EndPointEventArgs e) => Observe(SideOf(sender));
}
