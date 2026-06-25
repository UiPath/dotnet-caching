using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Tests.Telemetry;

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

    [Fact]
    public async Task TrackKeyReads_dependency_start_time_is_captured_at_Start_not_at_emit()
    {
        var telemetry = new RecordingTelemetryProvider();
        var op = new TelemetryOperation("ProviderX", "MethodY", typeof(string), telemetry);

        op.Start();
        op.Stop();
        var afterStop = DateTimeOffset.UtcNow;
        await Task.Delay(50);
        op.TrackKeyReads([("k1", true)]);

        telemetry.Dependencies.Single().StartTime.Should().BeOnOrBefore(afterStop);
    }

    [Fact]
    public void Track_without_key_count_defaults_keys_to_one()
    {
        var telemetry = new RecordingTelemetryProvider();
        var op = new TelemetryOperation("ProviderX", "MethodY", typeof(string), telemetry);

        op.Start();
        op.Stop();
        op.Track(hit: true);

        telemetry.Metrics.Should().ContainSingle(m =>
            m.Name == "Caching.Stats.Hits.ProviderX.MethodY.String"
            && m.Properties![TelemetryOperation.KeysTag] == "1");
    }

    [Fact]
    public void Track_with_key_count_emits_a_single_metric_carrying_the_count()
    {
        var telemetry = new RecordingTelemetryProvider();
        var op = new TelemetryOperation("ProviderX", "MethodY", typeof(string), telemetry);

        op.Start();
        op.Stop();
        op.Track(hit: true, keyCount: 5);

        telemetry.Metrics.Should().ContainSingle(m =>
            m.Name == "Caching.Stats.Hits.ProviderX.MethodY.String"
            && m.Properties![TelemetryOperation.KeysTag] == "5");
    }

    [Fact]
    public void TrackKeyReads_emits_a_dependency_per_key_sharing_one_batch_id()
    {
        var telemetry = new RecordingTelemetryProvider();
        var op = new TelemetryOperation("ProviderX", "MethodY", typeof(string), telemetry);

        op.Start();
        op.Stop();
        op.TrackKeyReads([("k1", true), ("k2", false)]);

        telemetry.Metrics.Should().BeEmpty();
        telemetry.Dependencies.Should().HaveCount(2);
        var hit = telemetry.Dependencies.Single(d => d.Data == "k1");
        var miss = telemetry.Dependencies.Single(d => d.Data == "k2");
        hit.Type.Should().Be(TelemetryOperation.DependencyType);
        hit.Name.Should().Be("MethodY");
        hit.Target.Should().Be("ProviderX");
        hit.Success.Should().BeTrue();
        hit.ResultCode.Should().Be("Hit");
        hit.Properties![TelemetryOperation.TypeTag].Should().Be("String");
        miss.Success.Should().BeTrue();
        miss.ResultCode.Should().Be("Miss");
        hit.Properties![TelemetryOperation.BatchIdTag].Should().Be(miss.Properties![TelemetryOperation.BatchIdTag]);
        hit.Properties![TelemetryOperation.BatchIdTag].Should().NotBeNullOrEmpty();
    }
}
