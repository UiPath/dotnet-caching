using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Tests.Telemetry;

public class TelemetryScopeTests
{
    private readonly RecordingTelemetryProvider _telemetry = new();
    private readonly ManualClock _clock = new();

    [Fact]
    public void Track_hit_emits_Hits_metric_with_composed_key()
    {
        var scope = NewScope();

        scope.Stop();
        scope.Track(hit: true);

        _telemetry.Metrics.Should().ContainSingle(m => m.Name == "Caching.Stats.Hits.ProviderX.MethodY.String");
    }

    [Fact]
    public void Track_miss_emits_Misses_metric_with_composed_key()
    {
        var scope = NewScope();

        scope.Stop();
        scope.Track(hit: false);

        _telemetry.Metrics.Should().ContainSingle(m => m.Name == "Caching.Stats.Misses.ProviderX.MethodY.String");
    }

    [Fact]
    public void The_metric_carries_the_time_until_Stop()
    {
        var scope = NewScope();

        _clock.Advance(TimeSpan.FromMilliseconds(5));
        scope.Stop();
        _clock.Advance(TimeSpan.FromMilliseconds(50));
        scope.Stop();
        scope.Track(hit: true);

        _telemetry.Metrics.Single().Value.Should().Be(5);
    }

    [Fact]
    public void TrackKeyReads_dependency_start_time_is_the_start_not_the_emit()
    {
        var started = _clock.Now;
        var scope = NewScope();

        _clock.Advance(TimeSpan.FromMilliseconds(5));
        scope.Stop();
        _clock.Advance(TimeSpan.FromMilliseconds(50));
        scope.TrackKeyReads([("k1", true)]);

        var dependency = _telemetry.Dependencies.Single();
        dependency.StartTime.Should().Be(started);
        dependency.Duration.Should().Be(TimeSpan.FromMilliseconds(5));
    }

    [Fact]
    public void Track_without_key_count_defaults_keys_to_one()
    {
        var scope = NewScope();

        scope.Stop();
        scope.Track(hit: true);

        _telemetry.Metrics.Should().ContainSingle(m => m.Properties![TelemetryScope.KeysTag] == "1");
    }

    [Fact]
    public void Track_with_key_count_emits_a_single_metric_carrying_the_count()
    {
        var scope = NewScope();

        scope.Stop();
        scope.Track(hit: true, keyCount: 5);

        _telemetry.Metrics.Should().ContainSingle(m =>
            m.Name == "Caching.Stats.Hits.ProviderX.MethodY.String"
            && m.Properties![TelemetryScope.KeysTag] == "5");
    }

    [Fact]
    public void TrackKeyReads_emits_a_dependency_per_key_sharing_one_batch_id()
    {
        var scope = NewScope();

        scope.Stop();
        scope.TrackKeyReads([("k1", true), ("k2", false)]);

        _telemetry.Metrics.Should().BeEmpty();
        _telemetry.Dependencies.Should().HaveCount(2);
        var hit = _telemetry.Dependencies.Single(d => d.Data == "k1");
        var miss = _telemetry.Dependencies.Single(d => d.Data == "k2");
        hit.Type.Should().Be(TelemetryScope.DependencyType);
        hit.Name.Should().Be("MethodY");
        hit.Target.Should().Be("ProviderX");
        hit.Success.Should().BeTrue();
        hit.ResultCode.Should().Be("Hit");
        hit.Properties![TelemetryScope.TypeTag].Should().Be("String");
        miss.Success.Should().BeTrue();
        miss.ResultCode.Should().Be("Miss");
        hit.Properties![TelemetryScope.BatchIdTag].Should().Be(miss.Properties![TelemetryScope.BatchIdTag]);
        hit.Properties![TelemetryScope.BatchIdTag].Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void The_null_provider_leaves_the_scope_disabled()
    {
        var scope = new TelemetryScope(NullTelemetryProvider.Instance, _clock, "ProviderX", "MethodY", typeof(string));

        scope.Stop();
        scope.Track(hit: true);
        scope.TrackKeyReads([("k1", true)]);

        scope.IsEnabled.Should().BeFalse();
        NewScope().IsEnabled.Should().BeTrue();
    }

    private TelemetryScope NewScope() => new(_telemetry, _clock, "ProviderX", "MethodY", typeof(string));

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; private set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        private long Timestamp { get; set; } = 1_000;

        public override long GetTimestamp() => Timestamp;

        public override DateTimeOffset GetUtcNow() => Now;

        public void Advance(TimeSpan by)
        {
            Timestamp += by.Ticks;
            Now += by;
        }
    }
}
