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

    private static Dictionary<string, string> _topicWriteMetricNames = [];
    private static Dictionary<string, string> _topicReadMetricNames = [];
    private static string GetMetricName(string topicName, Dictionary<string, string> metricDictionary, string operationType)
    {
        if (metricDictionary.TryGetValue(topicName, out var metricName))
        {
            return metricName;
        }
        metricName = $"{Topic}{topicName}{operationType}";
        metricDictionary[topicName] = metricName;
        return metricName;
    }
    public static string GetWriteTopicMetricName(string topicName)
        => GetMetricName(topicName, _topicWriteMetricNames, Write);

    public static string GetReadTopicMetricName(string topicName) =>
        GetMetricName(topicName, _topicReadMetricNames, Read);
}
