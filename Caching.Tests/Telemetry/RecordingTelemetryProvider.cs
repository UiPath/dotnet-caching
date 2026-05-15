using UiPath.Platform.Caching.Telemetry;

namespace UiPath.Platform.Caching.Tests.Telemetry;

internal sealed class RecordingTelemetryProvider : ICachingTelemetryProvider
{
    public List<EventRecord> Events { get; } = new();
    public List<MetricRecord> Metrics { get; } = new();
    public List<DependencyRecord> Dependencies { get; } = new();
    public List<ExceptionRecord> Exceptions { get; } = new();

    public void TrackDependency(string type, string target, string name, string data, DateTimeOffset startTime, TimeSpan duration, string resultCode, bool success, ReadOnlySpan<KeyValuePair<string, string>> properties = default, ReadOnlySpan<KeyValuePair<string, double>> metrics = default) =>
        Dependencies.Add(new(type, target, name, data, startTime, duration, resultCode, success, TelemetryTags.ToDictionaryOrNull(properties), TelemetryTags.ToDictionaryOrNull(metrics)));

    public void TrackEvent(string eventName, ReadOnlySpan<KeyValuePair<string, string>> properties = default, ReadOnlySpan<KeyValuePair<string, double>> metrics = default) =>
        Events.Add(new(eventName, TelemetryTags.ToDictionaryOrNull(properties), TelemetryTags.ToDictionaryOrNull(metrics)));

    public void TrackException(Exception ex, ReadOnlySpan<KeyValuePair<string, string>> properties = default, ReadOnlySpan<KeyValuePair<string, double>> metrics = default) =>
        Exceptions.Add(new(ex, TelemetryTags.ToDictionaryOrNull(properties), TelemetryTags.ToDictionaryOrNull(metrics)));

    public void TrackMetric(string name, double value, ReadOnlySpan<KeyValuePair<string, string>> properties = default) =>
        Metrics.Add(new(name, value, TelemetryTags.ToDictionaryOrNull(properties)));
}

internal sealed record EventRecord(string Name, Dictionary<string, string>? Properties, Dictionary<string, double>? Metrics);
internal sealed record MetricRecord(string Name, double Value, Dictionary<string, string>? Properties);
internal sealed record DependencyRecord(string Type, string Target, string Name, string Data, DateTimeOffset StartTime, TimeSpan Duration, string ResultCode, bool Success, Dictionary<string, string>? Properties, Dictionary<string, double>? Metrics);
internal sealed record ExceptionRecord(Exception Exception, Dictionary<string, string>? Properties, Dictionary<string, double>? Metrics);
