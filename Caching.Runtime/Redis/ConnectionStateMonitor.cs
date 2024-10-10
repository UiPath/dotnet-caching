using System.Globalization;
using UiPath.Platform.Caching.Telemetry;

namespace UiPath.Platform.Caching.Redis;

public sealed class ConnectionStateMonitor : IConnectionState, IDisposable
{
    private readonly IConnectionState[] _connectionStates;
    private Lazy<bool> _isConnected = default!;
    private readonly ICachingTelemetryProvider _telemetryProvider;
    private Timer? _timer;
    private TimeSpan _monitorInterval;

    public ConnectionStateMonitor(
        ICachingTelemetryProvider telemetryProvider,
        TimeSpan monitorInterval,
        params IConnectionState[] connectionStates)
    {
        _telemetryProvider = telemetryProvider;
        _monitorInterval = monitorInterval;
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
        _timer?.Dispose();
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
    private void ResetIsConnected(bool addTimer = true)
    {
        _isConnected = new Lazy<bool>(() => {
            var ret = Array.TrueForAll(_connectionStates, static x => x.IsConnected);
            TrackEvent("EvaluateConnected", new KeyValuePair<string, string>("connected", ret.ToString()));
            return ret;
        });

        if (addTimer)
        {
            _timer = new Timer(_ => EvaluateConnected(), null, _monitorInterval, _monitorInterval);
        }
    }

    private void EvaluateConnected()
    {
        if (_isConnected.Value)
        {
            _timer?.Dispose();
            _timer = null;
        }
        else
        {
            ResetIsConnected(false);
        }
    }

    private void TrackEvent(string eventName, params KeyValuePair<string, string>[] data)
    {
        var properties = new Dictionary<string, string>
        {
            ["Now"] = Environment.TickCount.ToString(CultureInfo.InvariantCulture),
        };

        foreach (var item in data)
        {
            properties[item.Key] = item.Value;
        }

        _telemetryProvider.TrackEvent($"Redis.{eventName}", properties, null);
    }
}
