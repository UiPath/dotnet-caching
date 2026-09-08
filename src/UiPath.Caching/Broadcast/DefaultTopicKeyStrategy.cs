using System.Collections.Concurrent;

namespace UiPath.Caching.Broadcast;

public sealed class DefaultTopicKeyStrategy : ITopicKeyStrategy
{
    private readonly char _separator;

    /// <summary>Cached per type: a generic type's friendly name is built with a <see cref="System.Text.StringBuilder"/>, and the strategy runs on every cache operation.</summary>
    private readonly ConcurrentDictionary<Type, TopicKey> _keys = new();

    public DefaultTopicKeyStrategy(char? separator = null) => _separator = separator ?? CacheOptions.KeySeparator;

    public TopicKey GetTopicKey(Type topicType) =>
        _keys.GetOrAdd(topicType, static (type, separator) => type.GetCacheFriendlyTypeName(separator), _separator);
}
