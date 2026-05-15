using System.Globalization;
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

        ReadOnlySpan<char> span = value.AsSpan();
        int separatorIndex = span.IndexOf('-');
        if (separatorIndex < 0 || separatorIndex == span.Length - 1)
        {
            return StreamId.Invalid;
        }

        if (!long.TryParse(span[..separatorIndex], out long timestamp))
        {
            return StreamId.Invalid;
        }
        if (!long.TryParse(span[(separatorIndex + 1)..], out long sequence))
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
        if (streamIdValue.Valid)
        {
            cachingTelemetryProvider.TrackMetric(metricName, streamIdValue.Timestamp,
            [
                new(Metrics.TopicName, topicName),
                new(Metrics.SequenceNumber, streamIdValue.Sequence.ToString(CultureInfo.InvariantCulture)),
            ]);
        }
        else
        {
            cachingTelemetryProvider.TrackMetric(metricName, 0,
            [
                new(Metrics.TopicName, topicName),
                new(Metrics.SequenceNumber, Metrics.Invalid),
            ]);
        }
    }
}
