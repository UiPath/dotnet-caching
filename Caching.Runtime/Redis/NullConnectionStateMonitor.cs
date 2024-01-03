namespace UiPath.Platform.Caching.Redis;

public sealed class NullConnectionStateMonitor : IConnectionState
{
    public static readonly NullConnectionStateMonitor Instance = new();

    public event EventHandler? OnConnectionFailed
    {
        add { }
        remove { }
    }

    public event EventHandler? OnConnectionRestored
    {
        add { }
        remove { }
    }

    public event EventHandler? OnReconnected
    {
        add { }
        remove { }
    }

    public bool IsConnected => true;

    public void Dispose()
    {
    }
}
