namespace UiPath.Platform.Caching.Redis;

public class RedisTypePrefixes
{
    public string String { get; set; } = "s";
    public string Hash { get; set; } = "h";
    public string PubSub { get; set; } = "ps";

    public string Streams { get; set; } = "st";
}
