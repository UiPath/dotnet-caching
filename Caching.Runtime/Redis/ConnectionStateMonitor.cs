using System.Globalization;
using UiPath.Platform.Caching.Telemetry;

namespace UiPath.Platform.Caching.Redis;

public sealed class ConnectionStateMonitor : IConnectionState, IDisposable
{
    private const string EventConnectionRestored = "Redis.ConnectionRestored";
    private const string EventConnectionFailed = "Redis.ConnectionFailed";
    private const string EventReconnected = "Redis.Reconnected";
    private const string EventEvaluateConnected = "Redis.EvaluateConnected";
    private const string PropNow = "Now";
    private const string PropConnected = "connected";

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
        TrackEvent(EventConnectionRestored);
        ResetIsConnected();
        OnConnectionRestored?.Invoke(this, EventArgs.Empty);
    }

    private void InternalOnConnectionFailed(object? sender, EventArgs e)
    {
        TrackEvent(EventConnectionFailed);
        ResetIsConnected();
        OnConnectionFailed?.Invoke(sender, e);
    }

    private void InternalOnReconnected(object? sender, EventArgs e)
    {
        TrackEvent(EventReconnected);
        ResetIsConnected();
        OnReconnected?.Invoke(sender, e);
    }
    private void ResetIsConnected(bool addTimer = true)
    {
        _isConnected = new Lazy<bool>(() => {
            var ret = Array.TrueForAll(_connectionStates, static x => x.IsConnected);
            TrackEvent(EventEvaluateConnected, new KeyValuePair<string, string>(PropConnected, ret.ToString()));
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
        var properties = new KeyValuePair<string, string>[data.Length + 1];
        properties[0] = new(PropNow, Environment.TickCount.ToString(CultureInfo.InvariantCulture));
        Array.Copy(data, 0, properties, 1, data.Length);
        _telemetryProvider.TrackEvent(eventName, properties);
    }
}
