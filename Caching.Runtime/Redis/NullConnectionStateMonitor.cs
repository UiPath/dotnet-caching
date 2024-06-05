namespace UiPath.Platform.Caching.Redis;

public sealed class NullConnectionStateMonitor : IConnectionState
{
    public static readonly NullConnectionStateMonitor Instance = new();

    public event EventHandler? OnConnectionFailed
    {
        add
        {
            // do nothing
        }
        remove
        {
            // do nothing
        }
    }

    public event EventHandler? OnConnectionRestored
    {
        add
        {
            // do nothing
        }
        remove
        {
            // do nothing
        }
    }

    public event EventHandler? OnReconnected
    {
        add
        {
            // do nothing
        }
        remove 
        {
            // do nothing
        }
    }

    public bool IsConnected => true;

    public void Dispose()
    {
    }
}
