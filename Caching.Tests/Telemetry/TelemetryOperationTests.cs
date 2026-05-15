using UiPath.Platform.Caching.Telemetry;

namespace UiPath.Platform.Caching.Tests.Telemetry;

public class TelemetryOperationTests
{
    [Fact]
    public void Track_hit_emits_Hits_metric_with_composed_key()
    {
        var telemetry = new RecordingTelemetryProvider();
        var op = new TelemetryOperation("ProviderX", "MethodY", typeof(string), telemetry);

        op.Start();
        op.Stop();
        op.Track(hit: true);

        telemetry.Metrics.Should().ContainSingle(m =>
            m.Name == "Caching.Stats.Hits.ProviderX.MethodY.String");
    }

    [Fact]
    public void Track_miss_emits_Misses_metric_with_composed_key()
    {
        var telemetry = new RecordingTelemetryProvider();
        var op = new TelemetryOperation("ProviderX", "MethodY", typeof(string), telemetry);

        op.Start();
        op.Stop();
        op.Track(hit: false);

        telemetry.Metrics.Should().ContainSingle(m =>
            m.Name == "Caching.Stats.Misses.ProviderX.MethodY.String");
    }

    [Fact]
    public void Stop_before_Track_yields_non_negative_elapsed()
    {
        var telemetry = new RecordingTelemetryProvider();
        var op = new TelemetryOperation("p", "m", typeof(int), telemetry);

        op.Start();
        op.Stop();
        op.Track(hit: true);

        telemetry.Metrics[0].Value.Should().BeGreaterThanOrEqualTo(0);
    }
}
