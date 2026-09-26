using System.Text;

namespace UiPath.Caching.Redis;

public class PrefixRedisKeyStrategy : IRedisKeyStrategy
{

    public PrefixRedisKeyStrategy(string prefix, char separator)
    {
        Prefix = Guard.NotNullOrWhiteSpace(prefix, nameof(prefix)).ToLowerInvariant();
        Separator = char.ToLowerInvariant(Guard.NotWhiteSpace(separator, nameof(separator)));
        KeyPrefix = Encoding.UTF8.GetBytes($"{Prefix}{Separator}");
    }
    protected string Prefix { get; }
    protected char Separator { get; }

    /// <summary>Carried as the key's byte prefix, so the client writes it next to the name instead of the strategy concatenating a new string per key.</summary>
    private protected RedisKey KeyPrefix { get; }

    public virtual RedisKey GetRedisKey(CacheKey key) =>
        ((RedisKey)key.Name).Prepend(KeyPrefix);
}
