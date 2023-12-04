namespace UiPath.Platform.Caching.Telemetry;

public interface ICachingTelemetryProvider
{
    public ITelemetryOperation StartOperation<TCache>(string methodName = "") =>
        StartOperation(typeof(TCache), methodName);

    public ITelemetryOperation StartOperation<TCache, TCacheObject>(string methodName = "") =>
        StartOperation(typeof(TCache), typeof(TCacheObject), methodName);

    public ITelemetryOperation StartOperation(Type cacheClass, string methodName = "") =>
        StartOperation(cacheClass, null, methodName);

    public ITelemetryOperation StartOperation(Type cacheClass, Type? cacheObject, string methodName = "");

    void TrackDependency(string type, string target, string name, string data, DateTimeOffset startTime, TimeSpan duration, string resultCode, bool success, IDictionary<string, string>? properties = null, IDictionary<string, double>? metrics = null);

    void TrackEvent(string eventName, IDictionary<string, string>? properties = null, IDictionary<string, double>? metrics = null);

    void TrackException(Exception ex, IDictionary<string, string>? properties = null, IDictionary<string, double>? metrics = null);

    void TrackMetric(string name, double value, IDictionary<string, string>? properties = null);
}
