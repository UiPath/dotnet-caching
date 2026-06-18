namespace UiPath.Caching;

public interface IConnectionState
{
    event EventHandler? OnConnectionFailed;

    event EventHandler? OnConnectionRestored;

    event EventHandler? OnReconnected;

    bool IsConnected { get; }
}
