namespace UiPath.Caching.Broadcast.Redis;

internal static class StreamSuffixChannel
{
    public static (char Separator, string Name) Resolve(CacheOptions options, string? name)
    {
        var separator = char.ToLowerInvariant(Guard.NotWhiteSpace(options.Separator, nameof(options.Separator)));
        var resolvedName = (string.IsNullOrWhiteSpace(name) ? StreamSuffixChannelStrategy.DefaultName : name).ToLowerInvariant();
        return (separator, resolvedName);
    }

    public static IRedisChannelStrategy Create(IRedisStreamKeyStrategy streamKeyStrategy, CacheOptions options, string? name, bool sharded) =>
        sharded
            ? new StreamSuffixShardedChannelStrategy(streamKeyStrategy, options, name)
            : new StreamSuffixChannelStrategy(streamKeyStrategy, options, name);
}
