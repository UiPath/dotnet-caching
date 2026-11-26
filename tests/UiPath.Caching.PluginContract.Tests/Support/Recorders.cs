using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.PluginContract.Tests.Support;

internal sealed class RecordingTelemetry : ICachingTelemetryProvider
{
    private readonly ConcurrentQueue<(string Name, Dictionary<string, string> Tags)> _metrics = [];
    private readonly Channel<(string Name, double Value, Dictionary<string, string> Tags)> _observed =
        Channel.CreateUnbounded<(string, double, Dictionary<string, string>)>();

    public async Task WaitForMetricAsync(string name, string tag, string value, CancellationToken token, int? sample = null)
    {
        while (true)
        {
            var (metricName, recordedValue, tags) = await _observed.Reader.ReadAsync(token);
            if (metricName == name && (sample is null || Math.Abs(recordedValue - sample.Value) < 0.5) && tags.GetValueOrDefault(tag) == value)
            {
                return;
            }
        }
    }

    public int Count(string name, string tag, string value) =>
        _metrics.Count(m => m.Name == name && m.Tags.TryGetValue(tag, out var found) && found == value);

    public void TrackDependency(string type, string target, string name, string data, DateTimeOffset startTime, TimeSpan duration, string resultCode, bool success, TelemetryTags<string> properties = default, TelemetryTags<double> metrics = default)
    {
    }

    public void TrackEvent(string eventName, TelemetryTags<string> properties = default, TelemetryTags<double> metrics = default)
    {
    }

    public void TrackException(Exception ex, TelemetryTags<string> properties = default, TelemetryTags<double> metrics = default)
    {
    }

    public void TrackMetric(string name, double value, TelemetryTags<string> properties = default)
    {
        var tags = properties.ToDictionary(p => p.Key, p => p.Value);
        _metrics.Enqueue((name, tags));
        _observed.Writer.TryWrite((name, value, tags));
    }
}

internal sealed class RecordingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = [];

    public IReadOnlyCollection<(LogLevel Level, string Message)> Entries => _entries;

    public ILogger CreateLogger(string categoryName) => new Recorder(_entries);

    public void Dispose()
    {
    }

    private sealed class Recorder(ConcurrentQueue<(LogLevel Level, string Message)> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            entries.Enqueue((logLevel, formatter(state, exception)));
    }
}
