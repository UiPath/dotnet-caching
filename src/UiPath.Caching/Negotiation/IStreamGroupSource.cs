namespace UiPath.Caching.Negotiation;

/// <summary>The consumer groups of a stream and how recently each was read, as the node list needs them.</summary>
internal interface IStreamGroupSource
{
    /// <summary>The names of the stream's groups; none for a stream that does not exist.</summary>
    Task<IReadOnlyList<string>> GroupsAsync(RedisKey stream, CancellationToken cancellationToken);

    /// <summary>How long ago a consumer of the group last read, the most recent of them; <see langword="null"/> when it has no consumer or no longer exists.</summary>
    Task<TimeSpan?> IdleAsync(RedisKey stream, string group, CancellationToken cancellationToken);
}
