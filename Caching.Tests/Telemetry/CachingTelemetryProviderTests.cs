using UiPath.Platform.Caching.Telemetry;
using UiPath.Platform.Telemetry;

namespace UiPath.Platform.Caching.Tests.Telemetry;

public class CachingTelemetryProviderTests
{
    private readonly ITelemetryProvider _upstream = Substitute.For<ITelemetryProvider>();
    private CachingTelemetryProvider Sut => new(_upstream);

    [Fact]
    public void TrackEvent_forwards_materialized_properties_and_metrics()
    {
        Sut.TrackEvent("evt", [new("k", "v"), new("k2", "v2")], [new("m", 1.5)]);

        _upstream.Received(1).TrackEvent("evt",
            Arg.Is<IDictionary<string, string>>(d => d.Count == 2 && d["k"] == "v" && d["k2"] == "v2"),
            Arg.Is<IDictionary<string, double>>(d => d.Count == 1 && d["m"] == 1.5));
    }

    [Fact]
    public void TrackEvent_with_empty_spans_forwards_null_dictionaries()
    {
        Sut.TrackEvent("evt");

        _upstream.Received(1).TrackEvent("evt", (IDictionary<string, string>?)null, (IDictionary<string, double>?)null);
    }

    [Fact]
    public void TrackException_forwards_materialized_properties_and_metrics()
    {
        var ex = new InvalidOperationException("boom");
        Sut.TrackException(ex, [new("op", "acquire")], [new("count", 3.0)]);

        _upstream.Received(1).TrackException(ex,
            Arg.Is<IDictionary<string, string>>(d => d["op"] == "acquire"),
            Arg.Is<IDictionary<string, double>>(d => d["count"] == 3.0));
    }

    [Fact]
    public void TrackMetric_forwards_materialized_properties()
    {
        Sut.TrackMetric("m", 42.0, [new("tag", "x")]);

        _upstream.Received(1).TrackMetric("m", 42.0,
            Arg.Is<IDictionary<string, string>>(d => d["tag"] == "x"));
    }

    [Fact]
    public void TrackMetric_with_empty_span_forwards_null_dictionary()
    {
        Sut.TrackMetric("m", 7.0);

        _upstream.Received(1).TrackMetric("m", 7.0, (IDictionary<string, string>?)null);
    }

    [Fact]
    public void StartOperation_default_impl_returns_started_TelemetryOperation()
    {
        ICachingTelemetryProvider sut = new CachingTelemetryProvider(_upstream);
        var op = sut.StartOperation("provider", typeof(string), "method");

        op.Should().NotBeNull();
        op.Should().BeOfType<TelemetryOperation>();
    }

    [Fact]
    public void TrackDependency_forwards_all_arguments_and_materialized_tags()
    {
        var start = DateTimeOffset.UtcNow;
        var duration = TimeSpan.FromMilliseconds(123);
        Sut.TrackDependency("Redis", "redis://host", "GET", "key", start, duration, "200", success: true,
            [new("p", "q")], [new("dur", 123.0)]);

        _upstream.Received(1).TrackDependency("Redis", "redis://host", "GET", "key", start, duration, "200", true,
            Arg.Is<IDictionary<string, string>>(d => d["p"] == "q"),
            Arg.Is<IDictionary<string, double>>(d => d["dur"] == 123.0));
    }
}
