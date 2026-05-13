using AsyncKeyedLock;

namespace UiPath.Platform.Caching.Locking;

internal sealed class AsyncKeyedLocalLock : ILocalLock, IDisposable
{
    private readonly AsyncKeyedLocker<string> _locker;

    public AsyncKeyedLocalLock(IOptions<CacheOptions> cacheOptions)
    {
        var opts = cacheOptions.Value;
        if (opts.LocalLockPoolSize <= 0)
        {
            throw new ArgumentOutOfRangeException(
                $"{nameof(cacheOptions)}.{nameof(CacheOptions.LocalLockPoolSize)}",
                opts.LocalLockPoolSize,
                $"{nameof(CacheOptions.LocalLockPoolSize)} must be greater than zero.");
        }
        if (opts.LocalLockPoolInitialFill < 0 || opts.LocalLockPoolInitialFill > opts.LocalLockPoolSize)
        {
            throw new ArgumentOutOfRangeException(
                $"{nameof(cacheOptions)}.{nameof(CacheOptions.LocalLockPoolInitialFill)}",
                opts.LocalLockPoolInitialFill,
                $"{nameof(CacheOptions.LocalLockPoolInitialFill)} must be between 0 and {opts.LocalLockPoolSize} ({nameof(CacheOptions.LocalLockPoolSize)}).");
        }
        _locker = new AsyncKeyedLocker<string>(new AsyncKeyedLockOptions(
            poolSize: opts.LocalLockPoolSize,
            poolInitialFill: opts.LocalLockPoolInitialFill));
    }

    public ValueTask<IDisposable> AcquireAsync(string key, CancellationToken token) =>
        _locker.LockAsync(key, token);

    public void Dispose() => _locker.Dispose();
}
