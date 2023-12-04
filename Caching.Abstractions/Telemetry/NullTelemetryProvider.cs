using System.Diagnostics.CodeAnalysis;

namespace UiPath.Platform.Caching.Telemetry;

[ExcludeFromCodeCoverage]
public sealed class NullTelemetryProvider : ICachingTelemetryProvider
{
    public static readonly NullTelemetryProvider Instance = new();

    public ITelemetryOperation StartOperation<TCache>(string methodName = "")
        => NullTelemetryOperation.Instance;

    public ITelemetryOperation StartOperation<TCache, TSource>(string methodName = "")
        => NullTelemetryOperation.Instance;

    public ITelemetryOperation StartOperation(Type cacheClass, string methodName = "")
        => NullTelemetryOperation.Instance;

    public ITelemetryOperation StartOperation(Type cacheClass, Type? cacheObject, string methodName = "")
        => NullTelemetryOperation.Instance;

    public void TrackDependency(string type, string target, string name, string data, DateTimeOffset startTime, TimeSpan duration, string resultCode, bool success, IDictionary<string, string>? properties = null, IDictionary<string, double>? metrics = null)
    {
        // noop
    }

    public void TrackEvent(string eventName, IDictionary<string, string>? properties = null, IDictionary<string, double>? metrics = null)
    {
        // noop
    }

    public void TrackException(Exception ex, IDictionary<string, string>? properties = null, IDictionary<string, double>? metrics = null)
    {
        // noop
    }

    public void TrackMetric(string name, double value, IDictionary<string, string>? properties = null)
    {
        // noop
    }
}
