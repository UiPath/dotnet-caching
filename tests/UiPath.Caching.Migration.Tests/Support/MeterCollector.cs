using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace UiPath.Caching.Migration.Tests.Support;

/// <summary>Counts the router's measurements for one cache name, so tests running side by side do not see each other's.</summary>
internal sealed class MeterCollector : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly ConcurrentQueue<(string Name, Dictionary<string, string?> Tags)> _measurements = [];

    public MeterCollector(string cache)
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == RouterTelemetry.MeterName)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
        {
            var map = new Dictionary<string, string?>();
            foreach (var tag in tags)
            {
                map[tag.Key] = tag.Value?.ToString();
            }

            if (map.GetValueOrDefault("cache") == cache)
            {
                _measurements.Enqueue((instrument.Name, map));
            }
        });
        _listener.Start();
    }

    public int Count(string name) => _measurements.Count(m => m.Name == name);

    public int Count(string name, string tag, string value) => _measurements.Count(m => m.Name == name && m.Tags.GetValueOrDefault(tag) == value);

    public void Dispose() => _listener.Dispose();
}
