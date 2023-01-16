namespace UiPath.Platform.Caching.Redis;

public class RedisCacheOptions : CacheOptions
{
    public string? InstanceName { get; set; }

    public int Version { get; set; } = 6;

    public int ExceptionsAllowedBeforeBreaking { get; set; } = 5;

    public TimeSpan DurationOfBreak { get; set; } = TimeSpan.FromMinutes(1);

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromMilliseconds(500);
}
