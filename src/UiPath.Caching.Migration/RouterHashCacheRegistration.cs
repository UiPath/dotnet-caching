namespace UiPath.Caching.Migration;

public sealed class RouterHashCacheRegistration<T>
    where T : class
{
    public required string Name { get; init; }

    public required string Group { get; init; }

    public required ISourceHashCache<T> Source { get; init; }

    /// <summary>The caller key to the full Redis key text, used verbatim on the library path.</summary>
    public required Func<string, string> RedisKey { get; init; }

    /// <summary>A hash has no local copy yet, so only zero is accepted.</summary>
    public TimeSpan LocalCap { get; init; }

    /// <summary>The lifetime of a write that names none; null means no expiry.</summary>
    public TimeSpan? DefaultExpiry { get; init; }

    /// <summary>How long after a write of unknown outcome the key stays pending.</summary>
    public TimeSpan PendingWindow { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The most fields a hash may have to be promoted while its keys move; null takes the key move option's default.</summary>
    public int? MaxHashFields { get; init; }

    /// <summary>The codecs and key maps of the key move forms; without them the cache keeps the plain path whatever its group's state.</summary>
    internal KeyMoveHashBinding<T>? KeyMove { get; init; }
}
