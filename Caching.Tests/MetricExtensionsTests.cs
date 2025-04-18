using StackExchange.Redis;
using UiPath.Platform.Caching.Telemetry;
namespace UiPath.Platform.Caching.Tests;
public class MetricExtensionsTests
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubstitute();

    [Fact]
    public void TrackReadWriteTopicMetric()
    {
        var topic = _fixture.Create<string>();
        var metric = _fixture.Create<string>();
        var timestamp = _fixture.Create<long>();
        var sequence = _fixture.Create<long>();
        var streamIdValue = new RedisValue($"{timestamp}-{sequence}");
        var metricProvider = _fixture.Create<ICachingTelemetryProvider>();
        var operation = _fixture.Create<ITelemetryOperation>();

        metricProvider.TrackTopicWriteMetric(topic, streamIdValue);
        metricProvider.TrackTopicReadMetric(topic, streamIdValue);

        var writeTopicMetricName = Metrics.GetWriteTopicMetricName(topic);
        var readTopicMetricName = Metrics.GetWriteTopicMetricName(topic);

        var streamId = streamIdValue.GetStreamIdFromRedisValue();
        metricProvider.Received(1).TrackMetric(writeTopicMetricName, streamId.Timestamp,
            Arg.Is<IDictionary<string, string>>(d =>
            d[Metrics.TopicName] == topic &&
            d[Metrics.SequenceNumber] == streamId.Sequence.ToString()
            )
        );
        metricProvider.Received(1).TrackMetric(readTopicMetricName, streamId.Timestamp,
            Arg.Is<IDictionary<string, string>>(d =>
            d[Metrics.TopicName] == topic &&
            d[Metrics.SequenceNumber] == streamId.Sequence.ToString()
            )
        );
    }
}
