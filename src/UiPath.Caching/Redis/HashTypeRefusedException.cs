using StackExchange.Redis;

namespace UiPath.Caching.Redis;

/// <summary>A chunk of a merge met a key that holds something other than a hash, and changed nothing.</summary>
internal sealed class HashTypeRefusedException(RedisKey key) : RedisException($"{key} holds a value that is not a hash.");
