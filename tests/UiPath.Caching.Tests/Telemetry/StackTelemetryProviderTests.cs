using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Tests.Telemetry;

public class StackTelemetryProviderTests
{
    private const string Stack = "SecondaryRedis";

    private readonly RecordingTelemetryProvider _inner = new();

    private StackTelemetryProvider Sut => new(_inner, Stack);

    [Fact]
    public void Every_call_carries_the_stack_after_its_own_properties()
    {
        var exception = new InvalidOperationException();
        KeyValuePair<string, string>[] properties = [new("k", "v")];
        KeyValuePair<string, double>[] metrics = [new("m", 1d)];

        Sut.TrackDependency("Redis", "target", "GET", "GET key", DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(1), "200", true, properties, metrics);
        Sut.TrackEvent("event", properties, metrics);
        Sut.TrackException(exception, properties, metrics);
        Sut.TrackMetric("metric", 2d, properties);

        var expected = new Dictionary<string, string> { ["k"] = "v", [StackTelemetryProvider.StackProperty] = Stack };
        var dependency = _inner.Dependencies.Should().ContainSingle().Which;
        dependency.Should().BeEquivalentTo(new { Type = "Redis", Target = "target", Name = "GET", Data = "GET key", ResultCode = "200", Success = true });
        dependency.Properties.Should().Equal(expected);
        dependency.Metrics.Should().Equal(new Dictionary<string, double> { ["m"] = 1d });
        var ev = _inner.Events.Should().ContainSingle().Which;
        ev.Name.Should().Be("event");
        ev.Properties.Should().Equal(expected);
        ev.Metrics.Should().Equal(new Dictionary<string, double> { ["m"] = 1d });
        var ex = _inner.Exceptions.Should().ContainSingle().Which;
        ex.Exception.Should().BeSameAs(exception);
        ex.Properties.Should().Equal(expected);
        var metric = _inner.Metrics.Should().ContainSingle().Which;
        metric.Should().BeEquivalentTo(new { Name = "metric", Value = 2d });
        metric.Properties.Should().Equal(expected);
    }

    [Fact]
    public void A_call_without_properties_carries_only_the_stack()
    {
        Sut.TrackMetric("metric", 1d);

        _inner.Metrics.Should().ContainSingle().Which.Properties.Should().Equal(
            new Dictionary<string, string> { [StackTelemetryProvider.StackProperty] = Stack });
    }

    [Fact]
    public void A_call_with_more_properties_than_held_inline_keeps_them_all()
    {
        var properties = Enumerable.Range(0, 12).Select(i => new KeyValuePair<string, string>($"k{i}", $"v{i}")).ToArray();

        Sut.TrackEvent("event", properties);

        _inner.Events.Should().ContainSingle().Which.Properties.Should().Equal(
            properties.Append(new(StackTelemetryProvider.StackProperty, Stack)).ToDictionary());
    }
}
