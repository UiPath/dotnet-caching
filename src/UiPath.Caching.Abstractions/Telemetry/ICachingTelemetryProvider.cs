namespace UiPath.Caching.Telemetry;

public interface ICachingTelemetryProvider
{
    public ITelemetryOperation StartOperation(string providerName, Type cacheObject, string methodName = "")
    {
        var ret = new TelemetryOperation(providerName, methodName, cacheObject, this);
        ret.Start();
        return ret;
    }

    [ExcludeFromCodeCoverage(Justification = "Default no-op body kept only to dodge Castle.Proxies mock-gen for ref-struct parameters; every real implementer overrides it.")]
    void TrackDependency(string type, string target, string name, string data, DateTimeOffset startTime, TimeSpan duration, string resultCode, bool success, ReadOnlySpan<KeyValuePair<string, string>> properties = default, ReadOnlySpan<KeyValuePair<string, double>> metrics = default)
    {
    }

    [ExcludeFromCodeCoverage(Justification = "Default no-op body kept only to dodge Castle.Proxies mock-gen for ref-struct parameters; every real implementer overrides it.")]
    void TrackEvent(string eventName, ReadOnlySpan<KeyValuePair<string, string>> properties = default, ReadOnlySpan<KeyValuePair<string, double>> metrics = default)
    {
    }

    [ExcludeFromCodeCoverage(Justification = "Default no-op body kept only to dodge Castle.Proxies mock-gen for ref-struct parameters; every real implementer overrides it.")]
    void TrackException(Exception ex, ReadOnlySpan<KeyValuePair<string, string>> properties = default, ReadOnlySpan<KeyValuePair<string, double>> metrics = default)
    {
    }

    [ExcludeFromCodeCoverage(Justification = "Default no-op body kept only to dodge Castle.Proxies mock-gen for ref-struct parameters; every real implementer overrides it.")]
    void TrackMetric(string name, double value, ReadOnlySpan<KeyValuePair<string, string>> properties = default)
    {
    }
}
