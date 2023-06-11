namespace UiPath.Platform.Caching;
public class CacheOptions
{
    public static readonly Uri MachineUri = new($"urn:{Environment.MachineName}".ToLowerInvariant());

    public bool Enabled { get; set; } = true;

    public bool TelemetryEnabled { get; set; } = true;

    public bool BroadcastEnabled { get; set; } = true;

    public string DefaultCache { get; set; } = KnownCacheProviderNames.InMemoryRedis;

    public string DefaultTopic { get; set; } = KnownTopicNames.RedisPubSub;

    public Uri? SourceUri { get; set; } = MachineUri;

    public char Separator { get; set; } = Constants.KeySeparator;

    public Type? CacheFactory { get; set; }
}
