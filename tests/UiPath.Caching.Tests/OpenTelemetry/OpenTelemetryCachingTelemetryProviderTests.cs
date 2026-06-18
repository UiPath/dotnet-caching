using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using UiPath.Caching.Config;
using UiPath.Caching.OpenTelemetry;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Tests.OpenTelemetry;

public class OpenTelemetryCachingTelemetryProviderTests
{
    private static List<(double Value, Dictionary<string, object?> Tags)> RecordDouble(string instrumentName, Action<CachingTelemetryProvider> act)
    {
        using var provider = new CachingTelemetryProvider();
        var measurements = new List<(double, Dictionary<string, object?>)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == CachingTelemetryProvider.MeterName && instrument.Name == instrumentName)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
        {
            var dict = new Dictionary<string, object?>();
            foreach (var t in tags) dict[t.Key] = t.Value;
            measurements.Add((value, dict));
        });
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            var dict = new Dictionary<string, object?>();
            foreach (var t in tags) dict[t.Key] = t.Value;
            measurements.Add((value, dict));
        });
        listener.Start();
        act(provider);
        return measurements;
    }

    [Fact]
    public void TrackMetric_records_value_and_tags_to_meter()
    {
        var m = RecordDouble("uipath.caching.metric", p =>
            p.TrackMetric("hits.test", 42.5, new[] { new KeyValuePair<string, string>("region", "eu") }));

        m.Should().ContainSingle();
        m[0].Value.Should().Be(42.5);
        m[0].Tags["metric.name"].Should().Be("hits.test");
        m[0].Tags["region"].Should().Be("eu");
    }

    [Fact]
    public void TrackEvent_increments_event_counter_with_event_name_tag()
    {
        var m = RecordDouble("uipath.caching.event", p => p.TrackEvent("cache.flushed"));

        m.Should().ContainSingle();
        m[0].Value.Should().Be(1);
        m[0].Tags["event.name"].Should().Be("cache.flushed");
    }

    [Fact]
    public void TrackException_increments_exception_counter_with_type_tag()
    {
        var m = RecordDouble("uipath.caching.exception", p => p.TrackException(new InvalidOperationException("boom")));

        m.Should().ContainSingle();
        m[0].Value.Should().Be(1);
        m[0].Tags["exception.type"].Should().Be("System.InvalidOperationException");
    }

    [Fact]
    public void TrackDependency_creates_client_activity()
    {
        using var provider = new CachingTelemetryProvider();
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == CachingTelemetryProvider.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activities.Add,
        };
        ActivitySource.AddActivityListener(listener);

        provider.TrackDependency("redis", "localhost", "GET", "GET key",
            DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(5), "OK", success: true);

        activities.Should().ContainSingle();
        activities[0].OperationName.Should().Be("GET");
        activities[0].Kind.Should().Be(ActivityKind.Client);
        activities[0].Status.Should().Be(ActivityStatusCode.Ok);
        activities[0].Tags.Should().Contain(t => t.Key == "dependency.target" && t.Value == "localhost");
    }

    [Fact]
    public void AddOpenTelemetry_when_enabled_registers_provider()
    {
        var services = new ServiceCollection();
        var builder = new CachingBuilder(services) { Enabled = true };

        builder.AddOpenTelemetry(enabled: true);

        var provider = services.BuildServiceProvider().GetRequiredService<ICachingTelemetryProvider>();
        provider.Should().BeOfType<CachingTelemetryProvider>();
    }

    [Fact]
    public void AddOpenTelemetry_when_disabled_registers_null_provider()
    {
        var services = new ServiceCollection();
        var builder = new CachingBuilder(services) { Enabled = true };

        builder.AddOpenTelemetry(enabled: false);

        var provider = services.BuildServiceProvider().GetRequiredService<ICachingTelemetryProvider>();
        provider.Should().BeOfType<NullTelemetryProvider>();
    }
}
