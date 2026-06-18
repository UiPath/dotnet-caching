using System.Diagnostics;
using System.Diagnostics.Metrics;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.OpenTelemetry;

/// <summary>
/// An <see cref="ICachingTelemetryProvider"/> backed by <see cref="System.Diagnostics.ActivitySource"/>
/// and <see cref="System.Diagnostics.Metrics.Meter"/>. Register it with
/// <c>ICachingBuilder.AddOpenTelemetry()</c> and subscribe to the <see cref="ActivitySourceName"/>
/// source / <see cref="MeterName"/> meter in your OpenTelemetry pipeline.
/// </summary>
public sealed class CachingTelemetryProvider : ICachingTelemetryProvider, IDisposable
{
    public const string ActivitySourceName = "UiPath.Caching";
    public const string MeterName = "UiPath.Caching";

    private readonly ActivitySource _activitySource = new(ActivitySourceName);
    private readonly Meter _meter = new(MeterName);
    private readonly Histogram<double> _metric;
    private readonly Counter<long> _events;
    private readonly Counter<long> _exceptions;

    public CachingTelemetryProvider()
    {
        _metric = _meter.CreateHistogram<double>("uipath.caching.metric");
        _events = _meter.CreateCounter<long>("uipath.caching.event");
        _exceptions = _meter.CreateCounter<long>("uipath.caching.exception");
    }

    public void TrackMetric(string name, double value, ReadOnlySpan<KeyValuePair<string, string>> properties = default)
    {
        var tags = new TagList { { "metric.name", name } };
        foreach (var p in properties)
        {
            tags.Add(p.Key, p.Value);
        }
        _metric.Record(value, tags);
    }

    public void TrackEvent(string eventName, ReadOnlySpan<KeyValuePair<string, string>> properties = default, ReadOnlySpan<KeyValuePair<string, double>> metrics = default)
    {
        var tags = new TagList { { "event.name", eventName } };
        foreach (var p in properties)
        {
            tags.Add(p.Key, p.Value);
        }
        _events.Add(1, tags);
        foreach (var m in metrics)
        {
            _metric.Record(m.Value, new TagList { { "metric.name", m.Key }, { "event.name", eventName } });
        }
    }

    public void TrackException(Exception ex, ReadOnlySpan<KeyValuePair<string, string>> properties = default, ReadOnlySpan<KeyValuePair<string, double>> metrics = default)
    {
        var tags = new TagList { { "exception.type", ex.GetType().FullName ?? string.Empty } };
        foreach (var p in properties)
        {
            tags.Add(p.Key, p.Value);
        }
        _exceptions.Add(1, tags);

        // BCL-only (net8 + net10): record an exception event on the current span rather than the
        // net9+ Activity.AddException API.
        Activity.Current?.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
        {
            { "exception.type", ex.GetType().FullName },
            { "exception.message", ex.Message },
            { "exception.stacktrace", ex.ToString() },
        }));
    }

    public void TrackDependency(string type, string target, string name, string data, DateTimeOffset startTime, TimeSpan duration, string resultCode, bool success, ReadOnlySpan<KeyValuePair<string, string>> properties = default, ReadOnlySpan<KeyValuePair<string, double>> metrics = default)
    {
        var tags = new ActivityTagsCollection
        {
            { "dependency.type", type },
            { "dependency.target", target },
            { "dependency.data", data },
            { "dependency.result_code", resultCode },
            { "dependency.success", success },
        };
        foreach (var p in properties)
        {
            tags[p.Key] = p.Value;
        }

        using var activity = _activitySource.StartActivity(name, ActivityKind.Client, parentContext: default, tags: tags, startTime: startTime);
        if (activity is null)
        {
            return;
        }
        activity.SetEndTime(startTime.Add(duration).UtcDateTime);
        activity.SetStatus(success ? ActivityStatusCode.Ok : ActivityStatusCode.Error);
        foreach (var m in metrics)
        {
            activity.SetTag(m.Key, m.Value);
        }
    }

    public void Dispose()
    {
        _meter.Dispose();
        _activitySource.Dispose();
    }
}
