namespace UiPath.Platform.Caching.Broadcast.Redis;

internal sealed class StreamSuffixChannelStrategy : IRedisChannelStrategy
{
    public const string DefaultName = "notify";

    private readonly IRedisStreamKeyStrategy _streamKeyStrategy;
    private readonly char _separator;
    private readonly string _name;

    public StreamSuffixChannelStrategy(IRedisStreamKeyStrategy streamKeyStrategy, CacheOptions options, string? name)
    {
        _streamKeyStrategy = streamKeyStrategy;
        (_separator, _name) = StreamSuffixChannel.Resolve(options, name);
    }

    public RedisChannel GetRedisChannel(TopicKey topicKey) =>
        RedisChannel.Literal(string.Join(_separator, _streamKeyStrategy.GetRedisKey(topicKey).ToString(), _name));
}
