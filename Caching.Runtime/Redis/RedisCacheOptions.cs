namespace UiPath.Platform.Caching.Redis;

public class RedisCacheOptions : CacheOptions
{
    public string? InstanceName { get; set; }

    public int Version { get; set; } = 6;
}
