using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Tests.Telemetry;

/// <summary>Refuses one event name, and optionally every metric or exception.</summary>
internal sealed class RefusingTelemetryProvider(string failingEvent, bool refuseMetrics = false, bool refuseExceptions = false) : ICachingTelemetryProvider
{
    public static readonly InvalidOperationException Failure = new("telemetry sink refused");

    private readonly object _gate = new();
    private readonly List<string> _events = [];
    private readonly List<Exception> _exceptions = [];

    public IReadOnlyList<string> Events => Snapshot(_events);

    public IReadOnlyList<Exception> Exceptions => Snapshot(_exceptions);

    public void TrackDependency(string type, string target, string name, string data, DateTimeOffset startTime, TimeSpan duration, string resultCode, bool success, ReadOnlySpan<KeyValuePair<string, string>> properties = default, ReadOnlySpan<KeyValuePair<string, double>> metrics = default)
    {
    }

    public void TrackEvent(string eventName, ReadOnlySpan<KeyValuePair<string, string>> properties = default, ReadOnlySpan<KeyValuePair<string, double>> metrics = default)
    {
        if (eventName == failingEvent)
        {
            throw Failure;
        }

        Record(_events, eventName);
    }

    public void TrackException(Exception ex, ReadOnlySpan<KeyValuePair<string, string>> properties = default, ReadOnlySpan<KeyValuePair<string, double>> metrics = default)
    {
        if (refuseExceptions)
        {
            throw Failure;
        }

        Record(_exceptions, ex);
    }

    public void TrackMetric(string name, double value, ReadOnlySpan<KeyValuePair<string, string>> properties = default)
    {
        if (refuseMetrics)
        {
            throw Failure;
        }
    }

    private void Record<T>(List<T> target, T record)
    {
        lock (_gate)
        {
            target.Add(record);
        }
    }

    private T[] Snapshot<T>(List<T> source)
    {
        lock (_gate)
        {
            return source.ToArray();
        }
    }
}
