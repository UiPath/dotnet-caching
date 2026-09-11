namespace UiPath.Caching.Broadcast.Redis;

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

    private static string ResolveChannelBase(string streamKey) =>
        RedisHashTag.EnsureTag(streamKey, "Sharded notify channel");
}
