namespace UiPath.Caching;

/// <summary>
/// Options for the in-memory <see cref="ISetCache"/> (the <c>InMemory</c> provider). Mirrors the
/// memory-tier knobs of <see cref="InMemoryCacheOptions"/>; the set cache stores each set as a
/// single <see cref="IMemoryCache"/> entry.
/// </summary>
public sealed class InMemorySetCacheOptions : IMemoryCacheOptions
{
    /// <summary>Indicates whether the in-memory set cache is enabled.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Default whole-set lifetime applied when no explicit expiration or <see cref="CachePolicy"/>
    /// expiration is supplied. <see langword="null"/> means the set never expires. Every add
    /// re-applies the resolved expiration, matching <see cref="ISetCache"/>.
    /// </summary>
    public TimeSpan? DefaultExpiration { get; set; } = TimeSpan.FromHours(1);

    /// <inheritdoc/>
    public bool TrackStatistics { get; set; } = true;

    /// <inheritdoc/>
    public TimeSpan StatisticsFlushInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <inheritdoc/>
    public long? SizeLimit { get; set; }

    /// <inheritdoc/>
    public double? CompactionPercentage { get; set; }

    /// <inheritdoc/>
    public ICacheEntrySizeProvider? SizeProvider { get; set; }
}
