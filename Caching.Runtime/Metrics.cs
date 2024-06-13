namespace UiPath.Platform.Caching;
public static class Metrics
{
    public const string Prefix = "Caching.Stats.";
    public const string Stream = Prefix + "Stream";
    public const string StreamGroup = Prefix + "StreamGroup";
    public const string StreamConsumer = Prefix + "StreamConsumer";
    public const string RedisClient = Prefix + "RedisClient";
    public const string MemoryCache = Prefix + "MemoryCache";
}
