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

    /// <summary>
    /// For a cache whose older client puts a type version in its keys: the version of this process. The key map writes it into the key as the older
    /// client does; the router's events and topic take the name <c>{Name}@{version}</c>, so a peer on another version does not hear them. The group,
    /// the predicate and the telemetry keep <see cref="Name"/>. Read once, when the router is built.
    /// </summary>
    public Func<string>? Version { get; init; }

    /// <summary>A null the source stores is a hit, as the older client reads it, on both paths and in the local tier; the source reports it as <c>new SourceRead&lt;T&gt;(ReadOutcome.Found, null)</c>. A null from a factory is still not stored.</summary>
    public bool CacheNulls { get; init; }

    /// <summary>How long after a write of unknown outcome the key stays off the local tier before its second eviction.</summary>
    public TimeSpan PendingWindow { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The codecs and key maps of the key move forms; without them the cache keeps the router's per-group path whatever its group's state.</summary>
    internal KeyMoveBinding<T>? KeyMove { get; init; }
}
