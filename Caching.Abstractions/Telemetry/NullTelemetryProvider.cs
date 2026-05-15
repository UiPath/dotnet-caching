namespace UiPath.Platform.Caching.Telemetry;

[ExcludeFromCodeCoverage]
#pragma warning disable IDE0060 // Remove unused parameter
public sealed class NullTelemetryProvider : ICachingTelemetryProvider
{
#pragma warning disable IDE1006 // Naming Styles
    public static readonly NullTelemetryProvider Instance = new();
#pragma warning restore IDE1006 // Naming Styles

#pragma warning disable S2325 // Methods and properties that don't access instance data should be static
#pragma warning disable S2326 // Unused type parameters should be removed
#pragma warning disable CA1822 // Mark members as static
    public ITelemetryOperation StartOperation<TCache>(string methodName = "")
        => NullTelemetryOperation.Instance;

    public ITelemetryOperation StartOperation<TCache, TSource>(string methodName = "")
        => NullTelemetryOperation.Instance;

    public ITelemetryOperation StartOperation(Type cacheClass, string methodName = "")
        => NullTelemetryOperation.Instance;

    public ITelemetryOperation StartOperation(Type cacheClass, Type? cacheObject, string methodName = "")
        => NullTelemetryOperation.Instance;
#pragma warning restore S2325 // Methods and properties that don't access instance data should be static
#pragma warning restore S2326 // Unused type parameters should be removed
#pragma warning restore CA1822 // Mark members as static

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
