using System.Diagnostics.Metrics;
using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Tests.KeyMove;

// Records the measurements of one instrument of the key move meter, with their tags, for the group a test runs.
internal sealed class KeyMoveMeterRecorder : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly List<(long Value, IReadOnlyDictionary<string, string?> Tags)> _measured = [];

    public KeyMoveMeterRecorder(string instrument, string group)
    {
        _listener.InstrumentPublished = (published, listener) =>
        {
            if (published.Meter.Name == KeyMoveTelemetry.MeterName && published.Name == instrument)
            {
                listener.EnableMeasurementEvents(published);
            }
        };
        _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            var named = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var tag in tags)
            {
                named[tag.Key] = tag.Value?.ToString();
            }

            if (named.GetValueOrDefault("group") == group)
            {
                lock (_measured)
                {
                    _measured.Add((value, named));
                }
            }
        });
        _listener.Start();
    }

    public IReadOnlyList<(long Value, IReadOnlyDictionary<string, string?> Tags)> Measured
    {
        get
        {
            lock (_measured)
            {
                return [.. _measured];
            }
        }
    }

    public void Dispose() => _listener.Dispose();
}
