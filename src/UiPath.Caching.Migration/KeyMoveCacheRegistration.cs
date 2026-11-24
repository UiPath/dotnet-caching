namespace UiPath.Caching.Migration;

/// <summary>One cache of a key move group: its traits and the key map between its in-place and native keys.</summary>
public sealed class KeyMoveCacheRegistration
{
    /// <summary>The name of the cache's router registration.</summary>
    public required string Name { get; init; }

    /// <summary>The key move group the cache moves with; the router registration names the same group.</summary>
    public required string Group { get; init; }

    /// <summary>The cache has no origin to refill it, so its group takes the S2 barrier and its writes close during it; <see cref="AtMostOnce"/> implies it.</summary>
    public bool NoOrigin { get; init; }

    /// <summary>The cache holds claims that two creators must not both win across the flip, so it is treated as <see cref="NoOrigin"/>; the runbook also holds its claim traffic during a reverse.</summary>
    public bool AtMostOnce { get; init; }

    /// <summary>The app refills what is lost: it consents to the cold start of X11 (which purges N and the L residue), and accepts that a late native write can leave a stale native key or delete an in-place key.</summary>
    public bool AcceptRefill { get; init; }

    /// <summary>The longest life of a value the old client wrote, which bounds the canary and the soak before CLOSED; null when unknown.</summary>
    public TimeSpan? MaxSourceTtl { get; init; }

    /// <summary>Glob over every in-place key of the cache, without the connection's key prefix.</summary>
    public required string InPlacePattern { get; init; }

    /// <summary>the in-place key to the caller key, or null for a key that is not the cache's.</summary>
    public required Func<string, string?> ParseInPlaceKey { get; init; }

    /// <summary>Glob over every native key of the cache, without the connection's key prefix; it starts with the library key prefix, the application's namespace.</summary>
    public required string NativePattern { get; init; }

    /// <summary>Maps the caller key to the native key, which starts with the library key prefix.</summary>
    public required Func<string, string> FormatNativeKey { get; init; }

    /// <summary>Maps the native key to the caller key or returns null if the cache does not own it.</summary>
    public required Func<string, string?> ParseNativeKey { get; init; }

    /// <summary>The ownership predicate for in-place keys; by default a key that <see cref="ParseInPlaceKey"/> reads is owned.</summary>
    public Func<string, bool>? OwnsInPlaceKey { get; init; }

    /// <summary>The ownership predicate for native keys; by default a key that <see cref="ParseNativeKey"/> reads is owned.</summary>
    public Func<string, bool>? OwnsNativeKey { get; init; }

    /// <summary>The remaining life below which the mover leaves a key of this cache to its next read, overriding the group's <see cref="KeyMoveBuilder.UseMoveMinTtl"/>; null follows the group. Zero moves every key. A <see cref="NoOrigin"/> cache is always moved in full, and a read-through promotion ignores it.</summary>
    public TimeSpan? MoveMinTtl { get; init; }
}
