using System.Collections.Concurrent;

namespace UiPath.Caching;
public static class Metrics
{
    public const string Prefix = "Caching.Stats.";
    public const string Stream = Prefix + "Stream";
    public const string StreamGroup = Prefix + "StreamGroup";
    public const string StreamConsumer = Prefix + "StreamConsumer";
    public const string DepartedGroupDeleted = Prefix + "DepartedGroupDeleted";
    public const string RedisClient = Prefix + "RedisClient";
    public const string MemoryCache = Prefix + "MemoryCache";
    public const string RedisProfilerSessions = Prefix + "RedisProfilerSessions";
    public const string Topic = Prefix + $"{nameof(Topic)}.";
    public const string Write = $".{nameof(Write)}";
    public const string Read = $".{nameof(Read)}";
    public const string MessageId = $".{nameof(MessageId)}";

    public const string FormatEffective = "cache.format.effective";
    public const string FormatFallback = "cache.format.fallback";
    public const string FormatViewAge = "cache.format.view.age";
    public const string FormatNodes = "cache.format.nodes";
    public const string FormatBlockers = Prefix + "FormatBlockers";

    public const string FormatSweep = "cache.format.sweep";
    public const string FormatInventory = "cache.format.inventory";

    public const string SerializerRead = "cache.serializer.read";
    public const string SerializerWrite = "cache.serializer.write";
    public const string SerializerRewrite = "cache.serializer.rewrite";

    public const string TopicName = nameof(TopicName);
    public const string SequenceNumber = nameof(SequenceNumber);
    public const string Invalid = nameof(Invalid);

    /// <summary>The tag naming what a topic's events travel over: <see cref="TransportStreams"/>, <see cref="TransportPubSub"/> or <see cref="TransportOff"/>.</summary>
    public const string Transport = nameof(Transport);
    public const string TransportStreams = "streams";
    public const string TransportPubSub = "pubsub";
    public const string TransportOff = "off";

    /// <summary>Tracked once when a Redis Streams topic moves to another transport; its tags are <see cref="TransportFrom"/>, <see cref="TransportTo"/> and <see cref="TransportReason"/>.</summary>
    public const string TransportChanged = Prefix + nameof(TransportChanged);
    public const string TransportFrom = "From";
    public const string TransportTo = "To";
    public const string TransportReason = "Reason";

    private static readonly ConcurrentDictionary<string, string> _topicWriteMetricNames = new();
    private static readonly ConcurrentDictionary<string, string> _topicReadMetricNames = new();

    public static string GetWriteTopicMetricName(string topicName)
        => GetMetricName(topicName, _topicWriteMetricNames, Write);

    public static string GetReadTopicMetricName(string topicName) =>
        GetMetricName(topicName, _topicReadMetricNames, Read);

    private static string GetMetricName(string topicName, ConcurrentDictionary<string, string> metricDictionary, string operationType)
        => metricDictionary.GetOrAdd(topicName, static (tn, operation) => $"{Topic}{tn}{operation}", operationType);
}
