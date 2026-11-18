namespace UiPath.Caching.Migration;

public interface IRoutedCache<T>
    where T : class
{
    /// <summary>A factory result of null is returned and not stored.</summary>
    ValueTask<T?> GetOrAddAsync(string key, Func<CancellationToken, Task<T?>> factory, CacheExpiry expiry = default, CancellationToken cancellationToken = default);

    ValueTask<T?> TryGetAsync(string key, CancellationToken cancellationToken = default);

    ValueTask<WriteOutcome> SetAsync(string key, T value, CacheExpiry expiry = default, CancellationToken cancellationToken = default);

    ValueTask<WriteOutcome> RemoveAsync(string key, CancellationToken cancellationToken = default);

    ValueTask ClearAsync(CancellationToken cancellationToken = default);
}
