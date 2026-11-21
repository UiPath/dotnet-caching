namespace UiPath.Caching.Redis;

/// <summary>The K2 map of a format move: the prefix of one format swapped for the other's, the name kept.</summary>
internal sealed class RedisKeyMap
{
    private readonly RedisKeyFormat _from;
    private readonly RedisKeyFormat _to;

    public RedisKeyMap(RedisKeyFormat from, RedisKeyFormat to)
    {
        if (from.ShardKey != to.ShardKey)
        {
            // A tag added on the way in cannot be told apart from one the name carried, so the map would not invert.
            throw new ArgumentException($"Key formats '{from.Id}' and '{to.Id}' differ in tag mode.", nameof(to));
        }

        _from = from;
        _to = to;
    }

    public bool TryMap(RedisKey key, string app, string keyspace, out RedisKey mapped)
    {
        if (_from.TryParse(key, app, keyspace, out var name))
        {
            mapped = _to.Build(app, keyspace, name);
            return true;
        }

        mapped = default;
        return false;
    }
}
