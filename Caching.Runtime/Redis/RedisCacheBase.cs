using UiPath.Platform.Caching.Telemetry;

namespace UiPath.Platform.Caching.Redis;

public abstract class RedisCacheBase(IRedisConnector redis, ICachingTelemetryProvider telemetryProvider, bool monitorConnection) : IConnectionState, IDisposable
{
    private bool _disposed;
    private readonly IConnectionState _connectionState = monitorConnection ? redis : NullConnectionStateMonitor.Instance;

    protected ICachingTelemetryProvider Telemetry { get; } = telemetryProvider;

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

    protected IDatabase Database => redis.Database;

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
                // Dispose managed resources
            }
            _disposed = true;
        }
    }
}
