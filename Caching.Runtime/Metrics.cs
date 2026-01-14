using System.Collections.Concurrent;

namespace UiPath.Platform.Caching;
public static class Metrics
{
    public const string Prefix = "Caching.Stats.";
    public const string Stream = Prefix + "Stream";
    public const string StreamGroup = Prefix + "StreamGroup";
    public const string StreamConsumer = Prefix + "StreamConsumer";
    public const string RedisClient = Prefix + "RedisClient";
    public const string MemoryCache = Prefix + "MemoryCache";
    public const string RedisProfilerSessions = Prefix + "RedisProfilerSessions";
    public const string Topic = Prefix + $"{nameof(Topic)}.";
    public const string Write = $".{nameof(Write)}";
    public const string Read = $".{nameof(Read)}";
    public const string MessageId = $".{nameof(MessageId)}";

    public const string TopicName = nameof(TopicName);
    public const string SequenceNumber = nameof(SequenceNumber);
    public const string Invalid = nameof(Invalid);

    private static readonly ConcurrentDictionary<string, string> _topicWriteMetricNames = new();
    private static readonly ConcurrentDictionary<string, string> _topicReadMetricNames = new();

    private static string GetMetricName(string topicName, ConcurrentDictionary<string, string> metricDictionary, string operationType)
        => metricDictionary.GetOrAdd(topicName, tn => $"{Topic}{tn}{operationType}");

    public static string GetWriteTopicMetricName(string topicName)
        => GetMetricName(topicName, _topicWriteMetricNames, Write);

    public static string GetReadTopicMetricName(string topicName) =>
        GetMetricName(topicName, _topicReadMetricNames, Read);
}
