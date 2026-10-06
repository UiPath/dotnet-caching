namespace UiPath.Caching.Telemetry;

public interface ICachingTelemetryProvider
{
    void TrackDependency(string type, string target, string name, string data, DateTimeOffset startTime, TimeSpan duration, string resultCode, bool success, TelemetryTags<string> properties = default, TelemetryTags<double> metrics = default);

    void TrackEvent(string eventName, TelemetryTags<string> properties = default, TelemetryTags<double> metrics = default);

    void TrackException(Exception ex, TelemetryTags<string> properties = default, TelemetryTags<double> metrics = default);

    void TrackMetric(string name, double value, TelemetryTags<string> properties = default);
}
