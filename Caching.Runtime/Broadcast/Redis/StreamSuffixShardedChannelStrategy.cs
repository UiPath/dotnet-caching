namespace UiPath.Platform.Caching.Broadcast.Redis;

internal sealed class StreamSuffixShardedChannelStrategy : IRedisChannelStrategy
{
    private readonly IRedisStreamKeyStrategy _streamKeyStrategy;
    private readonly char _separator;
    private readonly string _name;

    public StreamSuffixShardedChannelStrategy(IRedisStreamKeyStrategy streamKeyStrategy, CacheOptions options, string? name)
    {
        _streamKeyStrategy = streamKeyStrategy;
        (_separator, _name) = StreamSuffixChannel.Resolve(options, name);
    }

    public RedisChannel GetRedisChannel(TopicKey topicKey)
    {
        var streamKey = _streamKeyStrategy.GetRedisKey(topicKey).ToString();
        var channelBase = ResolveChannelBase(streamKey);
        return RedisChannel.Sharded(string.Join(_separator, channelBase, _name));
    }

    private static string ResolveChannelBase(string streamKey)
    {
        if (HasValidHashTag(streamKey))
        {
            return streamKey;
        }
        if (ContainsNoBraces(streamKey))
        {
            return "{" + streamKey + "}";
        }
        throw new InvalidOperationException(
            $"Sharded notify channel cannot guarantee Redis Cluster slot affinity for stream key '{streamKey}'. " +
            "The stream key must either contain a valid hash tag (non-empty content between '{' and '}', e.g. 'app:st:{topic}') " +
            "or contain no '{' or '}' characters at all.");
    }

    private static bool HasValidHashTag(string key)
    {
        var open = key.IndexOf('{');
        if (open < 0)
        {
            return false;
        }
        var close = key.IndexOf('}', open + 1);
        return close > open + 1;
    }

    private static bool ContainsNoBraces(string key) =>
        key.IndexOf('{') < 0 && key.IndexOf('}') < 0;
}
