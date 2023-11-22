namespace UiPath.Platform.Caching;
public class CacheOptions
{
    public const char KeySeparator = ':';

    public static readonly Uri MachineUri = new($"urn:{Environment.MachineName}".ToLowerInvariant());

    public bool Enabled { get; set; } = true;

    public bool TelemetryEnabled { get; set; } = true;

    public bool BroadcastEnabled { get; set; } = true;

    public bool ShardKeyEnabled { get; set; }

    public bool AuditEnabled { get; set; }

    public string DefaultCache { get; set; } = KnownCacheProviderNames.InMemoryRedis;

    public string DefaultTopic { get; set; } = KnownTopicNames.RedisPubSub;

    public Uri? SourceUri { get; set; } = MachineUri;

    public char Separator { get; set; } = KeySeparator;

    public string AppShortName { get; set; } = default!;

    public Type? CacheFactory { get; set; }

    public Type? CacheKeyStrategyFactory { get; set; }

    public Type? TopicKeyStrategyFactory { get; set; }

    public int LargeValueThreshold { get; set; } = 1024*1024;
}
