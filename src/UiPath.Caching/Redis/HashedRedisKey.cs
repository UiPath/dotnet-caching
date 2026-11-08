namespace UiPath.Caching.Redis;

/// <summary>A Redis key with its hash taken once, for the single-flight table: hashing a key encodes it, and a run looks its key up three times.</summary>
internal readonly struct HashedRedisKey(RedisKey key) : IEquatable<HashedRedisKey>
{
    private readonly int _hash = key.GetHashCode();

    public RedisKey Key { get; } = key;

    public bool Equals(HashedRedisKey other) => _hash == other._hash && Key == other.Key;

    public override bool Equals(object? obj) => obj is HashedRedisKey other && Equals(other);

    public override int GetHashCode() => _hash;
}
