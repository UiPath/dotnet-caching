using StackExchange.Redis;
using UiPath.Caching.Tests.Telemetry;
namespace UiPath.Caching.Tests;
public class MetricExtensionsTests
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubstitute();

    [Theory]
    [InlineData(null, "RedisValue.Null surfaces as a null string")]
    [InlineData("", "an empty value has no separator")]
    [InlineData("12345", "a value without '-' has no separator")]
    [InlineData("12345-", "the sequence is empty when '-' is the last character")]
    [InlineData("abc-123", "the timestamp half doesn't parse as long")]
    [InlineData("123-abc", "the sequence half doesn't parse as long")]
    public void GetStreamIdFromRedisValue_returns_Invalid_for_malformed_inputs(string? value, string because)
    {
        var redisValue = value is null ? RedisValue.Null : new RedisValue(value);

        var streamId = redisValue.GetStreamIdFromRedisValue();

        streamId.Should().Be(StreamId.Invalid, because);
    }

    [Fact]
    public void TrackTopicMetric_records_invalid_sequence_when_stream_id_is_malformed()
    {
        var topic = _fixture.Create<string>();
        var malformed = new RedisValue("not-a-stream-id-at-all");
        var telemetry = new RecordingTelemetryProvider();

        telemetry.TrackTopicWriteMetric(topic, malformed);

        var writeTopicMetricName = Metrics.GetWriteTopicMetricName(topic);
        telemetry.Metrics.Should().ContainSingle(m =>
            m.Name == writeTopicMetricName &&
            m.Value == 0 &&
            m.Properties != null &&
            m.Properties[Metrics.TopicName] == topic &&
            m.Properties[Metrics.SequenceNumber] == Metrics.Invalid);
    }

    [Fact]
    public void TrackReadWriteTopicMetric()
    {
        var topic = _fixture.Create<string>();
        var timestamp = _fixture.Create<long>();
        var sequence = _fixture.Create<long>();
        var streamIdValue = new RedisValue($"{timestamp}-{sequence}");
        var telemetry = new RecordingTelemetryProvider();

        telemetry.TrackTopicWriteMetric(topic, streamIdValue);
        telemetry.TrackTopicReadMetric(topic, streamIdValue);

        var writeTopicMetricName = Metrics.GetWriteTopicMetricName(topic);
        var readTopicMetricName = Metrics.GetReadTopicMetricName(topic);

        var streamId = streamIdValue.GetStreamIdFromRedisValue();
        telemetry.Metrics.Should().ContainSingle(m =>
            m.Name == writeTopicMetricName &&
            m.Value == streamId.Timestamp &&
            m.Properties != null &&
            m.Properties[Metrics.TopicName] == topic &&
            m.Properties[Metrics.SequenceNumber] == streamId.Sequence.ToString());
        telemetry.Metrics.Should().ContainSingle(m =>
            m.Name == readTopicMetricName &&
            m.Value == streamId.Timestamp &&
            m.Properties != null &&
            m.Properties[Metrics.TopicName] == topic &&
            m.Properties[Metrics.SequenceNumber] == streamId.Sequence.ToString());
    }
}
