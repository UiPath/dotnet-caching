using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Tests.Telemetry;

/// <summary>Records telemetry for assertions; access is serialized and readers get an immutable snapshot.</summary>
internal sealed class RecordingTelemetryProvider : ICachingTelemetryProvider
{
    private readonly object _gate = new();
    private readonly List<EventRecord> _events = [];
    private readonly List<MetricRecord> _metrics = [];
    private readonly List<DependencyRecord> _dependencies = [];
    private readonly List<ExceptionRecord> _exceptions = [];

    public IReadOnlyList<EventRecord> Events => Snapshot(_events);
    public IReadOnlyList<MetricRecord> Metrics => Snapshot(_metrics);
    public IReadOnlyList<DependencyRecord> Dependencies => Snapshot(_dependencies);
    public IReadOnlyList<ExceptionRecord> Exceptions => Snapshot(_exceptions);

    public void TrackDependency(string type, string target, string name, string data, DateTimeOffset startTime, TimeSpan duration, string resultCode, bool success, ReadOnlySpan<KeyValuePair<string, string>> properties = default, ReadOnlySpan<KeyValuePair<string, double>> metrics = default) =>
        Record(_dependencies, new(type, target, name, data, startTime, duration, resultCode, success, TelemetryTags.ToDictionaryOrNull(properties), TelemetryTags.ToDictionaryOrNull(metrics)));

    public void TrackEvent(string eventName, ReadOnlySpan<KeyValuePair<string, string>> properties = default, ReadOnlySpan<KeyValuePair<string, double>> metrics = default) =>
        Record(_events, new(eventName, TelemetryTags.ToDictionaryOrNull(properties), TelemetryTags.ToDictionaryOrNull(metrics)));

    public void TrackException(Exception ex, ReadOnlySpan<KeyValuePair<string, string>> properties = default, ReadOnlySpan<KeyValuePair<string, double>> metrics = default) =>
        Record(_exceptions, new(ex, TelemetryTags.ToDictionaryOrNull(properties), TelemetryTags.ToDictionaryOrNull(metrics)));

    public void TrackMetric(string name, double value, ReadOnlySpan<KeyValuePair<string, string>> properties = default) =>
        Record(_metrics, new(name, value, TelemetryTags.ToDictionaryOrNull(properties)));

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

internal sealed record EventRecord(string Name, Dictionary<string, string>? Properties, Dictionary<string, double>? Metrics);
internal sealed record MetricRecord(string Name, double Value, Dictionary<string, string>? Properties);
internal sealed record DependencyRecord(string Type, string Target, string Name, string Data, DateTimeOffset StartTime, TimeSpan Duration, string ResultCode, bool Success, Dictionary<string, string>? Properties, Dictionary<string, double>? Metrics);
internal sealed record ExceptionRecord(Exception Exception, Dictionary<string, string>? Properties, Dictionary<string, double>? Metrics);
