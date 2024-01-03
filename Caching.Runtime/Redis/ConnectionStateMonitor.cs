namespace UiPath.Platform.Caching.Redis;

public sealed class ConnectionStateMonitor : IConnectionState
{
    private readonly IConnectionState[] _connectionStates;
    private Lazy<bool> _isConnected = default!;
    public ConnectionStateMonitor(params IConnectionState[] connectionStates)
    {
        _connectionStates = connectionStates;
        ResetIsConnected();
        foreach (var connectionState in _connectionStates)
        {
            connectionState.OnConnectionFailed += InternalOnConnectionFailed;
            connectionState.OnConnectionRestored += InternalOnConnectionRestored;
            connectionState.OnReconnected += InternalOnReconnected;
        }
    }

    public event EventHandler? OnConnectionFailed;

    public event EventHandler? OnConnectionRestored;

    public event EventHandler? OnReconnected;

    public bool IsConnected => _isConnected.Value;

    public void Dispose()
    {
        foreach (var connectionState in _connectionStates)
        {
            connectionState.OnConnectionFailed -= InternalOnConnectionFailed;
            connectionState.OnConnectionRestored -= InternalOnConnectionRestored;
            connectionState.OnReconnected -= InternalOnReconnected;
        }
    }

    private void InternalOnConnectionRestored(object? sender, EventArgs e)
    {
        ResetIsConnected();
        OnConnectionRestored?.Invoke(this, EventArgs.Empty);
    }

    private void InternalOnConnectionFailed(object? sender, EventArgs e)
    {
        ResetIsConnected();
        OnConnectionFailed?.Invoke(sender, e);
    }

    private void InternalOnReconnected(object? sender, EventArgs e)
    {
        ResetIsConnected();
        OnReconnected?.Invoke(sender, e);
    }
    private void ResetIsConnected() =>
        _isConnected = new Lazy<bool>(() => _connectionStates.All(static x => x.IsConnected));
}
