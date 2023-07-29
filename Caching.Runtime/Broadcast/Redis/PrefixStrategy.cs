namespace UiPath.Platform.Caching.Broadcast.Redis;

public class PrefixStrategy : IRedisChannelStrategy, IRedisStreamKeyStrategy
{
    private readonly string _prefix;
    private readonly char _separator;

    public PrefixStrategy(string prefix, CacheOptions options)
    {
        _separator = char.ToLowerInvariant(Guard.NotWhiteSpace(options.Separator, nameof(options.Separator)));
        _prefix = string.Join(_separator, Guard.NotNullOrWhiteSpace(options.AppShortName, nameof(options.AppShortName)), Guard.NotNullOrWhiteSpace(prefix, nameof(prefix))).ToLowerInvariant();
    }

    public RedisChannel GetRedisChannel(TopicKey topicKey) =>
        GetKey(topicKey);

    public RedisKey GetRedisKey(TopicKey topicKey) =>
        GetKey(topicKey);

    private string GetKey(string key) =>
        string.Join(_separator, _prefix, key);
}

