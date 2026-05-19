namespace UiPath.Platform.Caching;

/// <summary>
/// Tuning for proactive background refresh of a cache entry before its physical expiration.
/// Enablement lives on <see cref="CachePolicy.RehydrateEnabled"/>; this type carries the timing
/// parameters once rehydration is on.
/// </summary>
public sealed class RehydrateOptions
{
    /// <summary>Soft-TTL trigger fraction in (0, 1]. Default 0.75.</summary>
    public double Threshold { get; init; } = 0.75;

    /// <summary>Cooldown between consecutive refresh attempts after the trigger fires. Default 5s.</summary>
    public TimeSpan BaseCooldown { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Upper bound on the backoff cooldown after repeated refresh failures. Default 5 min.</summary>
    public TimeSpan MaxCooldown { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Background factory timeout as a fraction of the entry's <c>Duration</c>, floored at 1s.
    /// Default 0.5.
    /// </summary>
    public double TimeoutFraction { get; init; } = 0.5;

    /// <summary>Profile label surfaced on telemetry as the <c>profile</c> dimension.</summary>
    public string? Name { get; init; }
}
