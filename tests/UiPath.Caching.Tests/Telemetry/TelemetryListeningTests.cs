using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using UiPath.Caching.OpenTelemetry;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Tests.Telemetry;

[CollectionDefinition(Name, DisableParallelization = true)]
public static class TelemetryListeners
{
    public const string Name = "Telemetry listeners";
}

[Collection(TelemetryListeners.Name)]
public sealed class TelemetryListeningTests : IDisposable
{
    private static readonly ManualTimeProvider Clock = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private readonly ConcurrentQueue<(string Instrument, double Value, Dictionary<string, object?> Tags)> _measurements = new();
    private readonly ConcurrentQueue<Activity> _activities = new();
    private readonly List<IDisposable> _listeners = [];

    public void Dispose()
    {
        foreach (var listener in _listeners)
        {
            listener.Dispose();
        }
    }

    [Fact]
    public void A_provider_reporting_false_skips_the_scope_and_the_topic_metrics()
    {
        var spy = new SpyProvider { Listening = false };
        var scope = NewScope(spy);

        scope.Stop();
        scope.Track(hit: true);
        scope.TrackKeyReads([("k", true)]);
        spy.TrackTopicReadMetric("topic", "1-2");
        spy.TrackTopicWriteMetric("topic", "1-2");

        scope.IsEnabled.Should().BeFalse();
        spy.Calls.Should().Be(0);
    }

    [Fact]
    public void A_provider_reporting_true_emits_through_the_scope_and_the_topic_metrics()
    {
        var spy = new SpyProvider { Listening = true };
        var scope = NewScope(spy);

        scope.Stop();
        scope.Track(hit: false, keyCount: 3);
        spy.TrackTopicReadMetric("topic", "5-7");
        spy.TrackTopicWriteMetric("topic", "5-7");

        scope.IsEnabled.Should().BeTrue();
        spy.Names.Should().Equal("Caching.Stats.Misses.P.M.String", "Caching.Stats.Topic.topic.Read", "Caching.Stats.Topic.topic.Write");
        spy.LastTags.Should().Equal(new Dictionary<string, string> { [Metrics.TopicName] = "topic", [Metrics.SequenceNumber] = "7" });
    }

    [Fact]
    public void A_provider_without_the_interface_is_treated_as_listening()
    {
        var opaque = new OpaqueProvider();

        opaque.IsListening().Should().BeTrue();
        var scope = NewScope(opaque);
        scope.Stop();
        scope.Track(hit: true);
        scope.IsEnabled.Should().BeTrue();
        opaque.Calls.Should().Be(1);
    }

    [Fact]
    public void No_provider_and_the_null_provider_are_not_listening()
    {
        ((ICachingTelemetryProvider?)null).IsListening().Should().BeFalse();
        NullTelemetryProvider.Instance.IsListening().Should().BeFalse();
        NewScope(NullTelemetryProvider.Instance).IsEnabled.Should().BeFalse();
    }

    [Fact]
    public void The_stack_wrapper_forwards_what_the_inner_provider_reports()
    {
        new StackTelemetryProvider(new SpyProvider { Listening = false }, "S").IsListening().Should().BeFalse();
        new StackTelemetryProvider(new SpyProvider { Listening = true }, "S").IsListening().Should().BeTrue();
        new StackTelemetryProvider(new OpaqueProvider(), "S").IsListening().Should().BeTrue();
        new StackTelemetryProvider(NullTelemetryProvider.Instance, "S").IsListening().Should().BeFalse();
    }

    [Fact]
    public void The_stack_wrapper_over_a_silent_provider_leaves_the_scope_disabled()
    {
        var spy = new SpyProvider { Listening = false };

        NewScope(new StackTelemetryProvider(spy, "S")).IsEnabled.Should().BeFalse();
    }

    [Fact]
    public void With_nothing_attached_the_provider_is_not_listening_and_skips_its_signals()
    {
        using var provider = new CachingTelemetryProvider();

        provider.IsListening.Should().BeFalse();
        NewScope(provider).IsEnabled.Should().BeFalse();
        provider.TrackMetric("unlistened", 1);
        provider.TrackEvent("unlistened");
        provider.TrackException(new InvalidOperationException());
        provider.TrackDependency("t", "t", "unlistened", "d", Clock.GetUtcNow(), TimeSpan.Zero, "r", true);
        _measurements.Should().BeEmpty();
        _activities.Should().BeEmpty();
    }

    [Fact]
    public void A_listener_attached_after_a_disabled_scope_enables_the_next_one()
    {
        using var provider = new CachingTelemetryProvider();
        var before = NewScope(provider);
        ListenToMeter();

        var after = NewScope(provider);
        after.Stop();
        after.Track(hit: true);

        before.IsEnabled.Should().BeFalse();
        after.IsEnabled.Should().BeTrue();
        var (instrument, _, tags) = _measurements.Single(m => m.Tags.GetValueOrDefault("metric.name") as string == "Caching.Stats.Hits.P.M.String");
        instrument.Should().Be("uipath.caching.metric");
        tags["Keys"].Should().Be("1");
    }

    [Fact]
    public void With_only_a_meter_listener_metrics_record_and_dependencies_do_not()
    {
        using var provider = new CachingTelemetryProvider();
        ListenToMeter();

        provider.IsListening.Should().BeTrue();
        provider.TrackMetric("m1", 2.5, [new("cache", "c1")]);
        provider.TrackDependency("t", "t", "metrics-only", "d", Clock.GetUtcNow(), TimeSpan.Zero, "r", true);

        var (_, value, tags) = _measurements.Single(m => m.Tags.GetValueOrDefault("metric.name") as string == "m1");
        value.Should().Be(2.5);
        tags.Should().BeEquivalentTo(new Dictionary<string, object?> { ["metric.name"] = "m1", ["cache"] = "c1" });
        _activities.Should().BeEmpty();
    }

    [Fact]
    public void With_only_an_activity_listener_dependencies_record_and_metrics_do_not()
    {
        using var provider = new CachingTelemetryProvider();
        ListenToActivities();

        provider.IsListening.Should().BeTrue();
        provider.TrackMetric("traces-only", 1);
        provider.TrackDependency("Redis", "host", "GET", "key", Clock.GetUtcNow(), TimeSpan.FromMilliseconds(3), "Hit", true, [new("Outcome", "Hit")]);

        _measurements.Should().BeEmpty();
        var activity = _activities.Single(a => a.OperationName == "GET");
        activity.GetTagItem("dependency.type").Should().Be("Redis");
        activity.GetTagItem("dependency.target").Should().Be("host");
        activity.GetTagItem("dependency.data").Should().Be("key");
        activity.GetTagItem("dependency.result_code").Should().Be("Hit");
        activity.GetTagItem("dependency.success").Should().Be(true);
        activity.GetTagItem("Outcome").Should().Be("Hit");
        activity.Status.Should().Be(ActivityStatusCode.Ok);
    }

    [Fact]
    public void With_only_the_event_counter_enabled_events_count_and_metrics_do_not_record()
    {
        using var provider = new CachingTelemetryProvider();
        ListenToMeter(instrument => instrument.Name == "uipath.caching.event");

        provider.IsListening.Should().BeTrue();
        provider.TrackEvent("e1", [new("cache", "c1")], [new("ms", 7)]);
        provider.TrackException(new InvalidOperationException());

        var counted = _measurements.Single();
        counted.Instrument.Should().Be("uipath.caching.event");
        counted.Value.Should().Be(1);
        counted.Tags.Should().BeEquivalentTo(new Dictionary<string, object?> { ["event.name"] = "e1", ["cache"] = "c1" });
    }

    [Fact]
    public void With_only_the_exception_counter_enabled_exceptions_count()
    {
        using var provider = new CachingTelemetryProvider();
        ListenToMeter(instrument => instrument.Name == "uipath.caching.exception");

        provider.TrackException(new InvalidOperationException(), [new("k", "v")]);

        var counted = _measurements.Single();
        counted.Value.Should().Be(1);
        counted.Tags.Should().BeEquivalentTo(new Dictionary<string, object?> { ["exception.type"] = "System.InvalidOperationException", ["k"] = "v" });
    }

    [Fact]
    public void With_only_the_histogram_enabled_event_metrics_record_and_the_event_does_not_count()
    {
        using var provider = new CachingTelemetryProvider();
        ListenToMeter(instrument => instrument.Name == "uipath.caching.metric");

        provider.IsListening.Should().BeTrue();
        provider.TrackEvent("e1", [new("cache", "c1")], [new("ms", 7)]);

        var recorded = _measurements.Single();
        recorded.Instrument.Should().Be("uipath.caching.metric");
        recorded.Value.Should().Be(7);
        recorded.Tags.Should().BeEquivalentTo(new Dictionary<string, object?> { ["metric.name"] = "ms", ["event.name"] = "e1" });
    }

    [Fact]
    public void An_exception_annotates_an_activity_another_source_started_while_the_provider_is_not_listening()
    {
        using var provider = new CachingTelemetryProvider();
        using var source = new ActivitySource("UiPath.Caching.Tests.Foreign");
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == source.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        using var activity = source.StartActivity("request");

        provider.IsListening.Should().BeFalse();
        provider.TrackException(new InvalidOperationException("boom"));

        var annotation = activity!.Events.Single();
        annotation.Name.Should().Be("exception");
        annotation.Tags.Should().Contain(new KeyValuePair<string, object?>("exception.message", "boom"));
    }

    private static TelemetryScope NewScope(ICachingTelemetryProvider provider) =>
        new(provider, Clock, "P", "M", typeof(string));

    private void ListenToMeter(Func<Instrument, bool>? include = null)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == CachingTelemetryProvider.MeterName && (include?.Invoke(instrument) ?? true))
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Record(instrument, value, tags));
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument, value, tags));
        listener.Start();
        _listeners.Add(listener);
    }

    private void ListenToActivities()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == CachingTelemetryProvider.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = _activities.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);
        _listeners.Add(listener);
    }

    private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags) =>
        _measurements.Enqueue((instrument.Name, value, tags.ToArray().ToDictionary(t => t.Key, t => t.Value)));

    private sealed class SpyProvider : ICachingTelemetryProvider, ITelemetryListening
    {
        private int _calls;

        public bool Listening { get; init; }

        public bool IsListening => Listening;

        public int Calls => Volatile.Read(ref _calls);

        public List<string> Names { get; } = [];

        public Dictionary<string, string> LastTags { get; private set; } = [];

        public void TrackDependency(string type, string target, string name, string data, DateTimeOffset startTime, TimeSpan duration, string resultCode, bool success, TelemetryTags<string> properties = default, TelemetryTags<double> metrics = default) =>
            Interlocked.Increment(ref _calls);

        public void TrackEvent(string eventName, TelemetryTags<string> properties = default, TelemetryTags<double> metrics = default) =>
            Interlocked.Increment(ref _calls);

        public void TrackException(Exception ex, TelemetryTags<string> properties = default, TelemetryTags<double> metrics = default) =>
            Interlocked.Increment(ref _calls);

        public void TrackMetric(string name, double value, TelemetryTags<string> properties = default)
        {
            Interlocked.Increment(ref _calls);
            Names.Add(name);
            LastTags = properties.ToDictionary(p => p.Key, p => p.Value);
        }
    }

    private sealed class OpaqueProvider : ICachingTelemetryProvider
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public void TrackDependency(string type, string target, string name, string data, DateTimeOffset startTime, TimeSpan duration, string resultCode, bool success, TelemetryTags<string> properties = default, TelemetryTags<double> metrics = default) =>
            Interlocked.Increment(ref _calls);

        public void TrackEvent(string eventName, TelemetryTags<string> properties = default, TelemetryTags<double> metrics = default) =>
            Interlocked.Increment(ref _calls);

        public void TrackException(Exception ex, TelemetryTags<string> properties = default, TelemetryTags<double> metrics = default) =>
            Interlocked.Increment(ref _calls);

        public void TrackMetric(string name, double value, TelemetryTags<string> properties = default) =>
            Interlocked.Increment(ref _calls);
    }
}
