using System.Buffers;

namespace UiPath.Caching.Telemetry;

/// <summary>Names the stack on everything a named caching stack reports, so its telemetry stays apart from the application's.</summary>
internal sealed class StackTelemetryProvider(ICachingTelemetryProvider inner, string stack) : ICachingTelemetryProvider
{
    internal const string StackProperty = "CachingStack";

    public void TrackDependency(string type, string target, string name, string data, DateTimeOffset startTime, TimeSpan duration, string resultCode, bool success, ReadOnlySpan<KeyValuePair<string, string>> properties = default, ReadOnlySpan<KeyValuePair<string, double>> metrics = default)
    {
        var tags = Rent(properties, out var length);
        try
        {
            inner.TrackDependency(type, target, name, data, startTime, duration, resultCode, success, tags.AsSpan(0, length), metrics);
        }
        finally
        {
            Return(tags, length);
        }
    }

    public void TrackEvent(string eventName, ReadOnlySpan<KeyValuePair<string, string>> properties = default, ReadOnlySpan<KeyValuePair<string, double>> metrics = default)
    {
        var tags = Rent(properties, out var length);
        try
        {
            inner.TrackEvent(eventName, tags.AsSpan(0, length), metrics);
        }
        finally
        {
            Return(tags, length);
        }
    }

    public void TrackException(Exception ex, ReadOnlySpan<KeyValuePair<string, string>> properties = default, ReadOnlySpan<KeyValuePair<string, double>> metrics = default)
    {
        var tags = Rent(properties, out var length);
        try
        {
            inner.TrackException(ex, tags.AsSpan(0, length), metrics);
        }
        finally
        {
            Return(tags, length);
        }
    }

    public void TrackMetric(string name, double value, ReadOnlySpan<KeyValuePair<string, string>> properties = default)
    {
        var tags = Rent(properties, out var length);
        try
        {
            inner.TrackMetric(name, value, tags.AsSpan(0, length));
        }
        finally
        {
            Return(tags, length);
        }
    }

    private static void Return(KeyValuePair<string, string>[] tags, int length)
    {
        tags.AsSpan(0, length).Clear();
        ArrayPool<KeyValuePair<string, string>>.Shared.Return(tags);
    }

    private KeyValuePair<string, string>[] Rent(ReadOnlySpan<KeyValuePair<string, string>> properties, out int length)
    {
        length = properties.Length + 1;
        var tags = ArrayPool<KeyValuePair<string, string>>.Shared.Rent(length);
        properties.CopyTo(tags);
        tags[properties.Length] = new(StackProperty, stack);
        return tags;
    }
}
