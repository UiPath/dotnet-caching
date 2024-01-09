namespace UiPath.Platform.Caching.Broadcast;

public sealed class DefaultTopicKeyStrategy : ITopicKeyStrategy
{
    private readonly char _separator;
 
    public DefaultTopicKeyStrategy(char? separator = null) => _separator = separator ?? CacheOptions.KeySeparator;

    public TopicKey GetTopicKey(Type topicType) => topicType.GetCacheFriendlyTypeName(_separator);
}
