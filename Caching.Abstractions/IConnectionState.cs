namespace UiPath.Platform.Caching;

public interface IConnectionState : IDisposable
{
    event EventHandler? OnConnectionFailed;

    event EventHandler? OnConnectionRestored;

    event EventHandler? OnReconnected;

    bool IsConnected { get; }
}
