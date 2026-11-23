namespace UiPath.Caching.Migration;

public interface IRoutedCache<T>
    where T : class
{
    /// <summary>A factory result of null is returned and not stored.</summary>
    ValueTask<T?> GetOrAddAsync(string key, Func<CancellationToken, Task<T?>> factory, CacheExpiry expiry = default, CancellationToken cancellationToken = default);

    ValueTask<T?> TryGetAsync(string key, CancellationToken cancellationToken = default);

    ValueTask<WriteOutcome> SetAsync(string key, T value, CacheExpiry expiry = default, CancellationToken cancellationToken = default);

    /// <summary>Creates the entry only when the key holds nothing, in one atomic command on the client of the group's path; an entry already there is left as it is.</summary>
    ValueTask<AddOutcome> TryAddAsync(string key, T value, CacheExpiry expiry = default, CancellationToken cancellationToken = default);

    ValueTask<WriteOutcome> RemoveAsync(string key, CancellationToken cancellationToken = default);

    ValueTask ClearAsync(CancellationToken cancellationToken = default);
}
