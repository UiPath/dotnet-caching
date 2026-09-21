using UiPath.Caching.Redis;
using UiPath.Caching.Telemetry;
using UiPath.Caching.Tests.Telemetry;

namespace UiPath.Caching.Tests.Redis;

/// <summary>The monitor's re-multicast sits inside the connector's, so a throw here aborts that list too.</summary>
public class ConnectionStateMonitorMulticastTests
{
    private readonly RecordingTelemetryProvider _telemetry = new();
    private readonly FakeConnectionState _source = new();
    private readonly InvalidOperationException _boom = new("subscriber boom");

    [Fact]
    public void OnReconnected_reaches_every_subscriber_when_one_throws()
    {
        using var sut = new ConnectionStateMonitor(_telemetry, Timeout.InfiniteTimeSpan, _source);
        var reached = 0;
        sut.OnReconnected += (_, _) => throw _boom;
        sut.OnReconnected += (_, _) => reached++;

        var raise = () => _source.RaiseReconnected();

        raise.Should().NotThrow("the connector's multicast continues past this monitor");
        reached.Should().Be(1, "a throwing subscriber must not cost the rest their notification");
        _telemetry.Exceptions.Should().ContainSingle().Which.Exception.Should().BeSameAs(_boom);
    }

    [Fact]
    public void OnConnectionFailed_reaches_every_subscriber_when_one_throws()
    {
        using var sut = new ConnectionStateMonitor(_telemetry, Timeout.InfiniteTimeSpan, _source);
        var reached = 0;
        sut.OnConnectionFailed += (_, _) => throw _boom;
        sut.OnConnectionFailed += (_, _) => reached++;

        var raise = () => _source.RaiseConnectionFailed();

        raise.Should().NotThrow("the connector's multicast continues past this monitor");
        reached.Should().Be(1, "a throwing subscriber must not cost the rest their notification");
        _telemetry.Exceptions.Should().ContainSingle().Which.Exception.Should().BeSameAs(_boom);
    }

    [Fact]
    public void OnConnectionRestored_reaches_every_subscriber_when_one_throws()
    {
        using var sut = new ConnectionStateMonitor(_telemetry, Timeout.InfiniteTimeSpan, _source);
        var reached = 0;
        sut.OnConnectionRestored += (_, _) => throw _boom;
        sut.OnConnectionRestored += (_, _) => reached++;

        var raise = () => _source.RaiseConnectionRestored();

        raise.Should().NotThrow("the connector's multicast continues past this monitor");
        reached.Should().Be(1, "a throwing subscriber must not cost the rest their notification");
        _telemetry.Exceptions.Should().ContainSingle().Which.Exception.Should().BeSameAs(_boom);
    }

    [Fact]
    public void A_refused_record_costs_neither_the_state_reset_nor_the_subscribers()
    {
        // The record runs ahead of both, and this monitor sits inside the connector's own multicast -- the
        // connector can isolate the monitor, but it cannot hand the monitor's subscribers their notification.
        var telemetry = new RefusingTelemetryProvider();
        using var sut = new ConnectionStateMonitor(telemetry, Timeout.InfiniteTimeSpan, _source);
        var reached = 0;
        sut.OnConnectionFailed += (_, _) => reached++;

        var raise = () => _source.RaiseConnectionFailed();

        raise.Should().NotThrow();
        reached.Should().Be(1, "a sink that refuses the record must not swallow the notification");
    }

    /// <summary>Refuses every record, the way a saturated sink would.</summary>
    private sealed class RefusingTelemetryProvider : ICachingTelemetryProvider
    {
        public void TrackEvent(string eventName, ReadOnlySpan<KeyValuePair<string, string>> properties = default, ReadOnlySpan<KeyValuePair<string, double>> metrics = default) =>
            throw new InvalidOperationException("sink boom");
    }

    private sealed class FakeConnectionState : IConnectionState
    {
        public event EventHandler? OnConnectionFailed;

        public event EventHandler? OnConnectionRestored;

        public event EventHandler? OnReconnected;

        public bool IsConnected => true;

        public void RaiseConnectionFailed() => OnConnectionFailed?.Invoke(this, EventArgs.Empty);

        public void RaiseConnectionRestored() => OnConnectionRestored?.Invoke(this, EventArgs.Empty);

        public void RaiseReconnected() => OnReconnected?.Invoke(this, EventArgs.Empty);
    }
}
