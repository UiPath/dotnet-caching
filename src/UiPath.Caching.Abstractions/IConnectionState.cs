namespace UiPath.Caching;

public interface IConnectionState
{
    event EventHandler? OnConnectionFailed;

    event EventHandler? OnConnectionRestored;

    event EventHandler? OnReconnected;

    /// <summary>Non-blocking snapshot of the current connection state; returns false while connecting or after a failed connect, and never blocks or throws.</summary>
    bool IsConnected { get; }
}
