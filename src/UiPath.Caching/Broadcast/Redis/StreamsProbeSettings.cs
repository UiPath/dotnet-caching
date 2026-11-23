namespace UiPath.Caching.Broadcast.Redis;

/// <summary>What a probe or a refusal needs to know about the app-wide Streams settings.</summary>
internal sealed record StreamsProbeSettings(RedisKey ProbeKey, StreamsUnsupportedAction Action, bool UseOnQuirkServers, ILogger Logger)
{
    internal const string ProbeName = "capability-probe";

    public static StreamsProbeSettings For(RedisStreamsTopicOptions options, CacheOptions cacheOptions, ILogger logger) => new(
        (options.RedisStreamKeyStrategy ?? new PrefixStrategy(RedisKeyspaces.Streams, cacheOptions)).GetRedisKey(ProbeName),
        options.StreamsUnsupported,
        options.UseStreamsOnQuirkServers,
        logger);
}
