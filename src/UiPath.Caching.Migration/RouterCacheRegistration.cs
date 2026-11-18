namespace UiPath.Caching.Migration;

public sealed class RouterCacheRegistration<T>
    where T : class
{
    public required string Name { get; init; }

    public required string Group { get; init; }

    public required ISourceCache<T> Source { get; init; }

    /// <summary>The caller key to the full Redis key text, used verbatim on the library path.</summary>
    public required Func<string, string> RedisKey { get; init; }

    /// <summary>Longest a local copy lives; zero means no local copy.</summary>
    public TimeSpan LocalCap { get; init; }

    /// <summary>The lifetime of a write that names none; null means no expiry.</summary>
    public TimeSpan? DefaultExpiry { get; init; }

    /// <summary>How long after a write of unknown outcome the key stays off the local tier before its second eviction.</summary>
    public TimeSpan PendingWindow { get; init; } = TimeSpan.FromSeconds(30);
}
