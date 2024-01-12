namespace UiPath.Platform.Caching.Redis;

public abstract class RedisCacheBase : IConnectionState
{
    private bool _disposed;
    private readonly IRedisConnector _redis;
    private readonly IConnectionState _connectionState;

    protected RedisCacheBase(IRedisConnector redis, bool monitorConnection)
    {
        _redis = redis;
        _connectionState = monitorConnection ? new ConnectionStateMonitor(redis) : NullConnectionStateMonitor.Instance;
    }

    public event EventHandler? OnConnectionFailed
    {
        add => _connectionState.OnConnectionFailed += value;
        remove => _connectionState.OnConnectionFailed -= value;
    }

    public event EventHandler? OnConnectionRestored
    {
        add => _connectionState.OnConnectionRestored += value;
        remove => _connectionState.OnConnectionRestored -= value;
    }

    public event EventHandler? OnReconnected
    {
        add => _connectionState.OnReconnected += value;
        remove => _connectionState.OnReconnected -= value;
    }

    public bool IsConnected => _connectionState.IsConnected;

    protected IDatabase Database => _redis.Database;

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                _connectionState.Dispose();
            }
            _disposed = true;
        }
    }
}
