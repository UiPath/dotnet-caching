namespace UiPath.Platform.Caching.Redis;

/// <summary>
/// Options for <see cref="RedisSetCache"/>.
/// </summary>
public sealed class RedisSetCacheOptions
{
    /// <summary>
    /// such as <c>SPOP</c>. Resolved via <see cref="IResiliencePipelineProvider"/>.
    /// When <see langword="null"/> or empty, those operations run with no resilience pipeline
    /// </summary>
    public string? ResilienceKeyName { get; set; }
}
