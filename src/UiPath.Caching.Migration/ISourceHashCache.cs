namespace UiPath.Caching.Migration;

/// <summary>The application adapter over its older Redis client for one hash cache. It builds its own Redis key from the caller key.</summary>
public interface ISourceHashCache<T>
    where T : class
{
    /// <param name="fields">The fields to return, or null for the whole hash.</param>
    ValueTask<SourceRead<IDictionary<string, T?>>> GetAsync(string key, string[]? fields, CancellationToken cancellationToken);

    /// <param name="mode">Replace the whole key, or merge the fields into the hash it holds.</param>
    /// <param name="expiry">A deadline, or <see cref="CacheExpiry.Default"/> for none: a merge changes the key's lifetime only when one is given.</param>
    ValueTask<WriteResult> SetAsync(string key, IDictionary<string, T?> fields, HashCacheSetOption mode, CacheExpiry expiry, CancellationToken cancellationToken);

    ValueTask<WriteResult> RemoveAsync(string key, CancellationToken cancellationToken);

    ValueTask<WriteResult> RemoveFieldsAsync(string key, string[] fields, CancellationToken cancellationToken);

    /// <summary>Removes every hash of the cache; the scan stays with the source.</summary>
    ValueTask ClearAsync(CancellationToken cancellationToken);
}
