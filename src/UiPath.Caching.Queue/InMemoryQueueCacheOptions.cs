namespace UiPath.Caching;

/// <summary>
/// Options for the in-memory queue-cache backing (the <c>InMemory</c> provider), shared by all its
/// collection kinds — like <see cref="InMemoryCacheOptions"/> is shared by the normal and hash
/// caches. Mirrors that type's memory-tier knobs; the set cache stores each set as a single
/// <see cref="IMemoryCache"/> entry.
/// </summary>
public sealed class InMemoryQueueCacheOptions : IMemoryCacheOptions
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
