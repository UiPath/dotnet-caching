using System.Diagnostics.Metrics;
using BenchmarkDotNet.Attributes;
using UiPath.Caching.OpenTelemetry;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Benchmarks;

/// <summary>The telemetry cost of a Redis-tier operation scope and of a topic metric, with a meter listener attached or not, on a plain or a named stack.</summary>
[MemoryDiagnoser]
public class TelemetryScopeBenchmark
{
    private readonly CachingTelemetryProvider _inner = new();
    private ICachingTelemetryProvider _provider = default!;
    private MeterListener? _listener;

    [Params(false, true)]
    public bool Listening { get; set; }

    [Params(false, true)]
    public bool NamedStack { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _provider = NamedStack ? new StackTelemetryProvider(_inner, "Stack") : _inner;
        if (!Listening)
        {
            return;
        }

        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == CachingTelemetryProvider.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        _listener.SetMeasurementEventCallback<double>(static (_, _, _, _) => { });
        _listener.Start();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _listener?.Dispose();
        _inner.Dispose();
    }

    [Benchmark]
    public void ReadHit()
    {
        var scope = new TelemetryScope(_provider, TimeProvider.System, "Redis", "GetAsync", typeof(string));
        scope.Stop();
        scope.Track(hit: true);
    }

    [Benchmark]
    public void TopicRead() => _provider.TrackTopicReadMetric("topic", "1700000000000-5");

    [Benchmark]
    public void TopicWrite() => _provider.TrackTopicWriteMetric("topic", "1700000000000-5");
}
