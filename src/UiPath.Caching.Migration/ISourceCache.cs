namespace UiPath.Caching.Migration;

/// <summary>The application adapter over its older Redis client for one cache. It builds its own Redis key from the caller key.</summary>
public interface ISourceCache<T>
    where T : class
{
    ValueTask<SourceRead<T>> GetAsync(string key, CancellationToken cancellationToken);

    /// <param name="expiry">A deadline, or <see cref="CacheExpiry.Default"/> for no expiry.</param>
    ValueTask<WriteResult> SetAsync(string key, T value, CacheExpiry expiry, CancellationToken cancellationToken);

    /// <summary>Writes value and expiry in one command only when the key holds nothing, as the older client's conditional create does.</summary>
    /// <param name="expiry">A deadline, or <see cref="CacheExpiry.Default"/> for no expiry.</param>
    ValueTask<AddResult> TryAddAsync(string key, T value, CacheExpiry expiry, CancellationToken cancellationToken);

    ValueTask<WriteResult> RemoveAsync(string key, CancellationToken cancellationToken);

    /// <summary>Removes every entry of the cache; the scan stays with the source.</summary>
    ValueTask ClearAsync(CancellationToken cancellationToken);
}
