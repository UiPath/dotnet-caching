namespace UiPath.Caching.Redis;

/// <summary>
/// Options for <see cref="RedisSetCache"/>.
/// </summary>
public sealed class RedisSetCacheOptions
{
    /// <summary>
    /// Indicates whether the Redis set cache is enabled.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Name of the resilience pipeline applied to non-idempotent, destructive-read operations,
    /// such as <c>SPOP</c>. Resolved via <see cref="IResiliencePipelineProvider"/>.
    /// When <see langword="null"/> or empty, those operations run with no resilience pipeline.
    /// </summary>
    public string? ResilienceKeyName { get; set; }
}
