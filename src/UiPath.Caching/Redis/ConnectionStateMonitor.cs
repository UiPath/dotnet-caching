using System.Globalization;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Redis;

public sealed class ConnectionStateMonitor : IConnectionState, IDisposable
{
    private const string EventConnectionRestored = "Redis.ConnectionRestored";
    private const string EventConnectionFailed = "Redis.ConnectionFailed";
    private const string EventMaintenanceHandoff = "Redis.MaintenanceHandoff";
    private const string EventReconnected = "Redis.Reconnected";
    private const string EventEvaluateConnected = "Redis.EvaluateConnected";
    private const string PropNow = "Now";
    private const string PropConnected = "connected";

    private static readonly TimeSpan DefaultMonitorInterval = TimeSpan.FromSeconds(5);

    private readonly IConnectionState[] _connectionStates;
    private readonly ICachingTelemetryProvider _telemetryProvider;
    private readonly TimeProvider _timeProvider;
    // Guards the outage mark, the observed flag and the timer, so failures, restores and polling change them one at a time.
    // Never held while reading _isConnected: a Lazy holds its own lock while it evaluates, and its factory marks outages.
    private readonly object _gate = new();
    private Lazy<bool> _isConnected = default!;
    private ITimer? _timer;
    private bool _disposed;
    private TimeSpan _monitorInterval;
    private bool _down;
    private bool _observed;
    private EventHandler? _recovered;

    public ConnectionStateMonitor(
        ICachingTelemetryProvider telemetryProvider,
        TimeSpan monitorInterval,
        params IConnectionState[] connectionStates)
        : this(telemetryProvider, monitorInterval, TimeProvider.System, connectionStates)
    {
    }

    internal ConnectionStateMonitor(
        ICachingTelemetryProvider telemetryProvider,
        TimeSpan monitorInterval,
        TimeProvider timeProvider,
        params IConnectionState[] connectionStates)
    {
        _telemetryProvider = telemetryProvider;
        _timeProvider = timeProvider;
        // A zero period makes a one-shot timer, which could miss a recovery for good; infinite stays the explicit opt-out.
        _monitorInterval = monitorInterval == TimeSpan.Zero ? DefaultMonitorInterval : monitorInterval;
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

    /// <summary>Raised when every monitored state is connected again after an outage.</summary>
    internal event EventHandler? Recovered
    {
        add
        {
            lock (_gate)
            {
                _recovered += value;
            }
        }

        remove
        {
            lock (_gate)
            {
                _recovered -= value;
            }
        }
    }

    public bool IsConnected => _isConnected.Value;

    public void Dispose()
    {
        foreach (var connectionState in _connectionStates)
        {
            connectionState.OnConnectionFailed -= InternalOnConnectionFailed;
            connectionState.OnConnectionRestored -= InternalOnConnectionRestored;
            connectionState.OnReconnected -= InternalOnReconnected;
        }

        lock (_gate)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }

    private void InternalOnConnectionRestored(object? sender, EventArgs e)
    {
        TrackEvent(EventConnectionRestored);
        lock (_gate)
        {
            // Before the aggregate was ever observed, a restore is the only sign of an outage that began before this monitor.
            // After that, failures and evaluations mark outages, so a restore forwarded twice by two facades clears once.
            if (!_observed)
            {
                _down = true;
            }
        }

        ResetIsConnected();
        OnConnectionRestored.TryRaise(_telemetryProvider, handler => handler(this, EventArgs.Empty));
        RaiseIfRecovered();
    }

    private void InternalOnConnectionFailed(object? sender, EventArgs e)
    {
        // Classified again here: the monitor sees the same args on the way to subscribers.
        TrackEvent(e is ConnectionFailedEventArgs { FailureType: ConnectionFailureType.MaintenanceHandoff }
            ? EventMaintenanceHandoff
            : EventConnectionFailed);
        lock (_gate)
        {
            Accept(false);
        }

        ResetIsConnected();
        OnConnectionFailed.TryRaise(_telemetryProvider, handler => handler(sender, e));
    }

    private void InternalOnReconnected(object? sender, EventArgs e)
    {
        TrackEvent(EventReconnected);
        lock (_gate)
        {
            // A forced swap can drop publications without ever reading as down. One swap heard through two facades
            // recovers twice in a row; the second clear finds the local tier already empty.
            _down = true;
        }

        ResetIsConnected();
        OnReconnected.TryRaise(_telemetryProvider, handler => handler(sender, e));
        RaiseIfRecovered();
    }

    private void ResetIsConnected(bool addTimer = true)
    {
        Lazy<bool> isConnected = null!;
        isConnected = new Lazy<bool>(() => {
            var ret = Array.TrueForAll(_connectionStates, static x => x.IsConnected);
            lock (_gate)
            {
                // A replaced value may have been read before a restore that has since raised Recovered; marking it would raise it twice.
                if (ReferenceEquals(_isConnected, isConnected))
                {
                    Accept(ret);
                }
            }

            TrackEvent(EventEvaluateConnected, new KeyValuePair<string, string>(PropConnected, ret.ToString()));
            return ret;
        });
        _isConnected = isConnected;

        if (!addTimer)
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            // Each failure or restore replaces the timer: stop the one it replaces, or it keeps polling for good.
            // Each callback knows its own timer, so a late callback of a replaced one cannot stop its successor.
            // Created disarmed and armed once published, so even a zero interval cannot fire before the callback has its timer.
            _timer?.Dispose();
            ITimer timer = null!;
            timer = _timeProvider.CreateTimer(_ => EvaluateConnected(timer), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _timer = timer;
            timer.Change(_monitorInterval, _monitorInterval);
        }
    }

    private void EvaluateConnected(ITimer caller)
    {
        var connected = AllConnected();
        lock (_gate)
        {
            if (!ReferenceEquals(_timer, caller))
            {
                // Replaced or disposed since it fired: its successor, if any, polls now, and this sample may predate a restore.
                caller.Dispose();
                return;
            }

            // Confirmed under the gate before the timer stops: a state that dropped since the sample, with no event to arm
            // another timer, keeps this one polling.
            connected = connected && AllConnected();

            // A poll that finds the tier down marks the outage too, or a monitor started during one would never see it end.
            Accept(connected);
            ResetIsConnected(false);

            // Stopped only once recovery holds: a state that dropped again before the final check keeps this one polling.
            if (connected && RaiseIfRecovered())
            {
                _timer = null;
                caller.Dispose();
            }
        }
    }

    /// <summary>Whether no outage is left to recover from.</summary>
    private bool RaiseIfRecovered()
    {
        // Decided and raised under the gate, so a failure cannot slip between the check and the notification.
        lock (_gate)
        {
            if (!_down)
            {
                return true;
            }

            var connected = AllConnected();
            Accept(connected);
            if (!connected)
            {
                return false;
            }

            _down = false;
            _recovered.TryRaise(_telemetryProvider, handler => handler(this, EventArgs.Empty));
            return true;
        }
    }

    /// <summary>The states themselves, read without evaluating the cached <see cref="IsConnected"/>, which would fix its value earlier than its readers expect.</summary>
    private bool AllConnected() => Array.TrueForAll(_connectionStates, static x => x.IsConnected);

    /// <summary>Records a sample the monitor acts on, under the gate: only then does a restore stop being the outage's sole sign, so one read meanwhile is not lost.</summary>
    private void Accept(bool connected)
    {
        _observed = true;
        _down |= !connected;
    }

    private void TrackEvent(string eventName, params KeyValuePair<string, string>[] data)
    {
        var properties = new KeyValuePair<string, string>[data.Length + 1];
        properties[0] = new(PropNow, Environment.TickCount.ToString(CultureInfo.InvariantCulture));
        Array.Copy(data, 0, properties, 1, data.Length);
        // Guarded here rather than at each call site: every one of them is followed by state to reset and
        // subscribers to notify, and one of them runs inside the connector's own multicast.
        _telemetryProvider.TryTrackEvent(eventName, properties);
    }
}
