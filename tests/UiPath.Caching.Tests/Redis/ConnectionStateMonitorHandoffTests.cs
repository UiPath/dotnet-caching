using System.Net;
using System.Reflection;
using StackExchange.Redis;
using UiPath.Caching.Redis;
using UiPath.Caching.Tests.Telemetry;

namespace UiPath.Caching.Tests.Redis;

public class ConnectionStateMonitorHandoffTests
{
    private readonly RecordingTelemetryProvider _telemetry = new();
    private readonly FakeConnectionState _source = new();

    [Fact]
    public void A_handoff_is_forwarded_without_the_failure_event()
    {
        // The monitor sees the same args again on the way to subscribers, so it has to classify them too.
        using var sut = new ConnectionStateMonitor(_telemetry, Timeout.InfiniteTimeSpan, _source);
        var forwarded = 0;
        sut.OnConnectionFailed += (_, _) => forwarded++;

        _source.RaiseConnectionFailed(FailedArgs(ConnectionFailureType.MaintenanceHandoff));

        forwarded.Should().Be(1, "the connection did drop, so subscribers still need to hear about it");
        _telemetry.Events.Should().NotContain(e => e.Name == "Redis.ConnectionFailed");
        _telemetry.Events.Should().Contain(e => e.Name == "Redis.MaintenanceHandoff");
    }

    [Fact]
    public void A_real_failure_still_reports_as_one()
    {
        using var sut = new ConnectionStateMonitor(_telemetry, Timeout.InfiniteTimeSpan, _source);

        _source.RaiseConnectionFailed(FailedArgs(ConnectionFailureType.SocketFailure));

        _telemetry.Events.Should().Contain(e => e.Name == "Redis.ConnectionFailed");
        _telemetry.Events.Should().NotContain(e => e.Name == "Redis.MaintenanceHandoff");
    }

    private static ConnectionFailedEventArgs FailedArgs(ConnectionFailureType failureType) =>
        (ConnectionFailedEventArgs)Activator.CreateInstance(
            typeof(ConnectionFailedEventArgs),
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            [null, null, new DnsEndPoint("node", 6379), ConnectionType.Interactive, failureType, null, null],
            null)!;

    private sealed class FakeConnectionState : IConnectionState
    {
        public event EventHandler? OnConnectionFailed;

        public event EventHandler? OnConnectionRestored;

        public event EventHandler? OnReconnected;

        public bool IsConnected => true;

        public void RaiseConnectionFailed(EventArgs e) => OnConnectionFailed?.Invoke(this, e);

        public void RaiseUnused()
        {
            OnConnectionRestored?.Invoke(this, EventArgs.Empty);
            OnReconnected?.Invoke(this, EventArgs.Empty);
        }
    }
}
