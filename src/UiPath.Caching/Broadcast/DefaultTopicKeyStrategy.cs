using System.Collections.Concurrent;

namespace UiPath.Caching.Broadcast;

public sealed class DefaultTopicKeyStrategy : ITopicKeyStrategy
{
    private readonly char _separator;

    /// <summary>
    /// The strategy runs on every cache operation, and the friendly name of a generic type is built with a
    /// <see cref="System.Text.StringBuilder"/> over its type arguments — so it is computed once per type. A
    /// non-generic type's name is a cached lookup either way; the table just makes the two cost the same.
    /// </summary>
    private readonly ConcurrentDictionary<Type, TopicKey> _keys = new();

    public DefaultTopicKeyStrategy(char? separator = null) => _separator = separator ?? CacheOptions.KeySeparator;

    public TopicKey GetTopicKey(Type topicType) =>
        _keys.GetOrAdd(topicType, static (type, separator) => type.GetCacheFriendlyTypeName(separator), _separator);
}
