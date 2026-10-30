namespace UiPath.Caching;

/// <summary>Reads and keeps refreshed entries for the value type a cache held them as, which the refresher no longer knows at event time.</summary>
internal interface IPeerRefreshReader
{
    /// <summary>Reads the keys in one inner call; throws on failure.</summary>
    ValueTask<PeerRead[]> ReadAsync(MultilayerCacheBase cache, CacheKey[] keys, CachePolicy policy, CancellationToken token);

    /// <summary>Keeps the read in place of the held entry unless a change since <paramref name="mark"/> came first; <paramref name="installed"/> is the entry kept.</summary>
    PeerKeep Keep(MultilayerCacheBase cache, RefreshMetadataState held, PeerRead read, long mark, out object? installed);
}
