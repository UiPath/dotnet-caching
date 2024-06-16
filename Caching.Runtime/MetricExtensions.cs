using System.Reactive;
using System.Text;
using UiPath.Platform.Caching.Telemetry;

namespace UiPath.Platform.Caching;

internal static class MetricExtensions
{
    internal static void TrackTopicWriteMetric(this ICachingTelemetryProvider cachingTelemetryProvider, string topicName, RedisValue streamId)
        => TrackTopicMetric(cachingTelemetryProvider, topicName, Metrics.GetWriteTopicMetricName(topicName), streamId);

    internal static void TrackTopicReadMetric(this ICachingTelemetryProvider cachingTelemetryProvider, string topicName, RedisValue streamId)
        => TrackTopicMetric(cachingTelemetryProvider, topicName, Metrics.GetReadTopicMetricName(topicName), streamId);

    internal static StreamId GetStreamIdFromRedisValue(this RedisValue redisValue)
    {
        string? value = (string?)redisValue;
        if (value == null)
        {
            return StreamId.Invalid;
        }
        var streamIdParts = value.Split('-');
        if (streamIdParts.Length != 2)
        {
            return StreamId.Invalid;
        }
        if (!long.TryParse(streamIdParts[0], out long timestamp))
        {
            return StreamId.Invalid;
        }
        if (!long.TryParse(streamIdParts[1], out long sequence))
        {
            return StreamId.Invalid;
        }
        return new StreamId(timestamp, sequence);
    }
    private static void TrackTopicMetric(
        ICachingTelemetryProvider cachingTelemetryProvider,
        string topicName,
        string metricName,
        RedisValue streamId)
    {
        var streamIdValue = streamId.GetStreamIdFromRedisValue();
        var properties = new Dictionary<string, string>(2)
        {
            { Metrics.TopicName, topicName},
        };
        if (streamIdValue.Valid)
        {
            properties.Add(Metrics.SequenceNumber, streamIdValue.Sequence.ToString());
            cachingTelemetryProvider.TrackMetric(metricName, streamIdValue.Timestamp, properties);
        }
        else
        {
            properties.Add(Metrics.SequenceNumber, Metrics.Invalid);
            cachingTelemetryProvider.TrackMetric(metricName, 0, properties);
        }
    }
}
