namespace UiPath.Caching.Broadcast;

/// <summary>
/// Internal implementation detail of the per-topic options resolution wiring used by
/// the Redis broadcast providers. Public only to satisfy C# accessibility rules for
/// the public provider constructors that receive it via DI. Consumers should use
/// <c>ICachingBuilder.ConfigureRedisStreamsTopic</c> and
/// <c>ICachingBuilder.ConfigureRedisPubSubTopic</c> instead of touching this type.
/// </summary>
public sealed class PerTopicOptionsRegistry<TOptions> where TOptions : class
{
    private readonly Dictionary<string, List<Action<TOptions>>> _configures
        = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="topicsSection">
    /// Configuration section pointing directly at the broadcast provider's <c>Topics</c> array
    /// (e.g. <c>Broadcast:RedisStreams:Topics</c> resolved against the caching configuration).
    /// <see cref="Resolve"/> iterates this section's children.
    /// </param>
    public PerTopicOptionsRegistry(IConfigurationSection topicsSection)
    {
        ArgumentNullException.ThrowIfNull(topicsSection);
        TopicsSection = topicsSection;
    }

    public IConfigurationSection TopicsSection { get; }

    public void Configure(string topicName, Action<TOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(topicName);
        ArgumentNullException.ThrowIfNull(configure);
        if (string.IsNullOrWhiteSpace(topicName))
        {
            throw new ArgumentException("Topic name must be a non-empty, non-whitespace string.", nameof(topicName));
        }

        var key = topicName.Trim();
        if (!_configures.TryGetValue(key, out var actions))
        {
            actions = [];
            _configures[key] = actions;
        }
        actions.Add(configure);
    }

    public IReadOnlyList<Action<TOptions>> GetActions(string topicName)
    {
        if (string.IsNullOrWhiteSpace(topicName)) return [];
        return _configures.TryGetValue(topicName.Trim(), out var actions) ? actions : [];
    }

    /// <summary>
    /// Resolves per-topic options for the given <paramref name="topicKey"/>. Returns <c>null</c> when no
    /// <c>Topics</c> array entry matches and no code action is registered, so callers can fall back to
    /// the app-wide options instance without paying for a clone.
    /// </summary>
    /// <param name="topicKey">The topic key being resolved.</param>
    /// <param name="clone">Factory producing a fresh clone of the app-wide options. Invoked only when overrides apply.</param>
    /// <param name="logger">Optional logger used to emit a Debug message when a <c>Topics</c> entry is skipped because its <c>Name</c> is blank. Pass <c>null</c> to skip the log.</param>
    public TOptions? Resolve(TopicKey topicKey, Func<TOptions> clone, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(clone);
        if (topicKey.IsNull)
        {
            return null;
        }

        IConfigurationSection? lastMatch = null;
        foreach (var child in TopicsSection.GetChildren())
        {
            var name = child["Name"];
            if (string.IsNullOrWhiteSpace(name))
            {
                if (logger?.IsEnabled(LogLevel.Debug) == true)
                {
                    logger.LogDebug("Per-topic entry at '{Path}' has a missing or blank 'Name'; skipping.", child.Path);
                }
                continue;
            }

            if (string.Equals(name.Trim(), topicKey.Name, StringComparison.OrdinalIgnoreCase))
            {
                lastMatch = child;
            }
        }

        var actions = GetActions(topicKey.Name);
        if (lastMatch is null && actions.Count == 0)
        {
            return null;
        }

        var resolved = clone();
        lastMatch?.Bind(resolved);
        foreach (var action in actions)
        {
            action(resolved);
        }
        return resolved;
    }
}
