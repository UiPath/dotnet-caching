namespace UiPath.Caching;

/// <summary>An inner tier that can write a value only while the key still holds what an earlier read saw, which the read hands back as a stamp.</summary>
internal interface IStampedTier
{
    /// <summary>The snapshot and its stamp; the stamp is null when the key held nothing.</summary>
    ValueTask<(SnapshotRead<T> Snapshot, object? Stamp)> ReadStampedAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token);

    /// <summary>Writes the value if the key still holds what <paramref name="stamp"/> stands for, or is still absent when it is null.</summary>
    ValueTask<StampedWrite> WriteIfStampAsync<T>(CacheKey cacheKey, T? value, DateTimeOffset expiration, CachePolicy? policy, object? stamp, CancellationToken token);
}
