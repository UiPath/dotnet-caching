namespace UiPath.Caching.Telemetry;

[ExcludeFromCodeCoverage]
#pragma warning disable IDE0060 // Remove unused parameter
public sealed class NullTelemetryProvider : ICachingTelemetryProvider
{
#pragma warning disable IDE1006 // Naming Styles
    public static readonly NullTelemetryProvider Instance = new();
#pragma warning restore IDE1006 // Naming Styles

    public void TrackDependency(string type, string target, string name, string data, DateTimeOffset startTime, TimeSpan duration, string resultCode, bool success, ReadOnlySpan<KeyValuePair<string, string>> properties = default, ReadOnlySpan<KeyValuePair<string, double>> metrics = default)
    {
        // noop
    }

    public void TrackEvent(string eventName, ReadOnlySpan<KeyValuePair<string, string>> properties = default, ReadOnlySpan<KeyValuePair<string, double>> metrics = default)
    {
        // noop
    }

    public void TrackException(Exception ex, ReadOnlySpan<KeyValuePair<string, string>> properties = default, ReadOnlySpan<KeyValuePair<string, double>> metrics = default)
    {
        // noop
    }

    public void TrackMetric(string name, double value, ReadOnlySpan<KeyValuePair<string, string>> properties = default)
    {
        // noop
    }
}
#pragma warning restore IDE0060 // Remove unused parameter
