using System.Globalization;
using UiPath.Platform.Caching.Telemetry;

namespace UiPath.Platform.Caching.Redis;

public sealed class ConnectionStateMonitor : IConnectionState
{
    private readonly IConnectionState[] _connectionStates;
    private Lazy<bool> _isConnected = default!;
    private readonly ICachingTelemetryProvider _telemetryProvider;
    public ConnectionStateMonitor(
        ICachingTelemetryProvider telemetryProvider,
        params IConnectionState[] connectionStates)
    {
        _telemetryProvider = telemetryProvider;
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
        TrackEvent("ConnectionRestored");
        ResetIsConnected();
        OnConnectionRestored?.Invoke(this, EventArgs.Empty);
    }

    private void InternalOnConnectionFailed(object? sender, EventArgs e)
    {
        TrackEvent("ConnectionFailed");
        ResetIsConnected();
        OnConnectionFailed?.Invoke(sender, e);
    }

    private void InternalOnReconnected(object? sender, EventArgs e)
    {
        TrackEvent("Reconnected");
        ResetIsConnected();
        OnReconnected?.Invoke(sender, e);
    }
    private void ResetIsConnected() =>
        _isConnected = new Lazy<bool>(() => Array.TrueForAll(_connectionStates, static x => x.IsConnected));

    private void TrackEvent(string eventName)
    {
        _telemetryProvider.TrackEvent(
            $"Redis.{eventName}",
            new Dictionary<string, string>
            {
                ["Now"] = Environment.TickCount.ToString(CultureInfo.InvariantCulture),
            },
            null);
    }
}
