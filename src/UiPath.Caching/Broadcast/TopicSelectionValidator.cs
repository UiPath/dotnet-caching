using Microsoft.Extensions.Hosting;

namespace UiPath.Caching.Broadcast;

/// <summary>Fails host start when a topic name selects a provider that is disabled or not registered, which would otherwise turn broadcast off silently.</summary>
internal sealed class TopicSelectionValidator(
    IOptions<CacheOptions> cacheOptions,
    ITopicFactory factory,
    IEnumerable<ITopicProvider> providers,
    IOptions<InMemoryCacheOptions> inMemoryOptions,
    IOptions<InMemoryRedisCacheOptions> inMemoryRedisOptions) : IHostedService
{
    private static readonly Dictionary<string, string> EnableSettings = new(StringComparer.OrdinalIgnoreCase)
    {
        [KnownTopicNames.RedisPubSub] = "Broadcast:RedisPubSub:Enabled",
        [KnownTopicNames.RedisStreams] = "Broadcast:RedisStreams:Enabled",
    };

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var failures = Validate().ToList();
        return failures.Count > 0
            ? throw new OptionsValidationException(nameof(CacheOptions), typeof(CacheOptions), failures)
            : Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    internal IEnumerable<string> Validate()
    {
        var options = cacheOptions.Value;
        if (!options.BroadcastEnabled)
        {
            yield break;
        }

        var inMemory = inMemoryOptions.Value;
        var inMemoryRedis = inMemoryRedisOptions.Value;
        if ((ResolvesToDefault(inMemory.BroadcastEnable, inMemory.Topic) || ResolvesToDefault(inMemoryRedis.BroadcastEnable, inMemoryRedis.Topic))
            && Check(options.DefaultTopic, nameof(CacheOptions.DefaultTopic)) is { } defaultFailure)
        {
            yield return defaultFailure;
        }

        if (inMemory is { BroadcastEnable: true, Topic: { Length: > 0 } inMemoryTopic }
            && Check(inMemoryTopic, nameof(InMemoryCacheOptions) + "." + nameof(InMemoryCacheOptions.Topic)) is { } inMemoryFailure)
        {
            yield return inMemoryFailure;
        }

        if (inMemoryRedis is { BroadcastEnable: true, Topic: { Length: > 0 } redisTopic }
            && Check(redisTopic, nameof(InMemoryRedisCacheOptions) + "." + nameof(InMemoryRedisCacheOptions.Topic)) is { } redisFailure)
        {
            yield return redisFailure;
        }
    }

    // A cache that names no usable topic falls back to the default one.
    private bool ResolvesToDefault(bool broadcastEnabled, string? topic) =>
        broadcastEnabled && (topic is not { Length: > 0 } || factory.Get(topic) is NullTopicProvider);

    private string? Check(string? topic, string setting)
    {
        if (string.IsNullOrWhiteSpace(topic))
        {
            return $"{setting} is empty, so no topic provider is selected and broadcast would be off. Name a provider, or set BroadcastEnabled to false.";
        }

        if (factory.Get(topic) is not NullTopicProvider)
        {
            return null;
        }

        var provider = providers.LastOrDefault(p => string.Equals(p.Name, topic, StringComparison.OrdinalIgnoreCase));
        var enable = EnableSettings.TryGetValue(topic, out var key) ? $"set {key} to true" : $"enable the '{topic}' provider";
        return provider is null && !EnableSettings.ContainsKey(topic)
            ? $"{setting} names the topic provider '{topic}', which is not registered, so broadcast would be off. Registered: {string.Join(", ", factory.ProviderNames)}."
            : $"{setting} names the topic provider '{topic}', which is disabled or not registered, so broadcast would be off: {enable}, or point {setting} at an enabled provider.";
    }
}
