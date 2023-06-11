namespace UiPath.Platform.Caching.Redis;

public class RedisCacheOptions : CacheOptionsBase
{
    public string? Prefix { get; set; }

    public int Version { get; set; } = 6;

    public string ConnectionString { get; set; } = default!;

    public int? BackOffMilliseconds { get; set; }

    public TimeSpan? HeartbeatInterval { get; set; }

    public bool ProfilerEnabled { get; set; }

    public RedisTypePrefixes RedisTypePrefixes { get; set; } = new RedisTypePrefixes();
}
