namespace UiPath.Platform.Caching.Telemetry;

public interface ICachingTelemetryProvider
{
    public ITelemetryOperation StartOperation(string providerName, Type cacheObject, string methodName = "")
    {
        var ret = new TelemetryOperation(providerName, methodName, cacheObject, this);
        ret.Start();
        return ret;
    }

    void TrackDependency(string type, string target, string name, string data, DateTimeOffset startTime, TimeSpan duration, string resultCode, bool success, IDictionary<string, string>? properties = null, IDictionary<string, double>? metrics = null);

    void TrackEvent(string eventName, IDictionary<string, string>? properties = null, IDictionary<string, double>? metrics = null);

    void TrackException(Exception ex, IDictionary<string, string>? properties = null, IDictionary<string, double>? metrics = null);

    void TrackMetric(string name, double value, IDictionary<string, string>? properties = null);
}
