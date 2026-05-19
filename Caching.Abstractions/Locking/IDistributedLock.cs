namespace UiPath.Platform.Caching.Locking;

public interface IDistributedLock
{
    ValueTask<IAsyncDisposable> AcquireAsync(string key, TimeSpan expiry, TimeSpan waitTimeout, CancellationToken token);

    /// <summary>
    /// Non-blocking try-acquire. Returns the lease on success, or <c>null</c> when the lock could
    /// not be acquired (already held, or backend unavailable). Callers that need to distinguish
    /// "acquired" from "not acquired" — e.g. cross-node rehydrate dedup — must use this method
    /// instead of <see cref="AcquireAsync"/>, which uses a no-op sentinel that conflates the two.
    /// The default implementation returns <c>null</c> so external implementers degrade safely;
    /// implementations that back a real distributed lock should override.
    /// </summary>
    ValueTask<IAsyncDisposable?> TryAcquireAsync(string key, TimeSpan expiry, CancellationToken token) =>
        new(default(IAsyncDisposable));
}
