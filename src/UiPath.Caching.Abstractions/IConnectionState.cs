namespace UiPath.Caching;

public interface IConnectionState
{
    event EventHandler? OnConnectionFailed;

    event EventHandler? OnConnectionRestored;

    event EventHandler? OnReconnected;

    /// <summary>Non-blocking snapshot of the current connection state; never blocks or throws. A connector that connects on first use reports true until it has a connection that is down, so that first command can open it.</summary>
    bool IsConnected { get; }
}
