using AsyncKeyedLock;

namespace UiPath.Caching.Locking;

internal sealed class AsyncKeyedLocalLock : ILocalLock, IDisposable
{
    private readonly AsyncKeyedLocker<LockKey> _locker;

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
        _locker = new AsyncKeyedLocker<LockKey>(new AsyncKeyedLockOptions(
            poolSize: opts.LocalLockPoolSize,
            poolInitialFill: opts.LocalLockPoolInitialFill));
    }

    public ValueTask<IDisposable> AcquireAsync(string key, CancellationToken token) =>
        _locker.LockAsync(new LockKey(string.Empty, key), token);

    public void Dispose() => _locker.Dispose();

    /// <summary>The lease on <paramref name="key"/> under <paramref name="prefix"/>, or null once the key has stayed held for <paramref name="timeout"/>; a caller that cancels gets the cancellation. No timer or cancellation source is made unless the key is held.</summary>
    internal ValueTask<IDisposable?> TryAcquireAsync(string prefix, string key, TimeSpan timeout, CancellationToken token)
    {
        // The wait takes at most Int32.MaxValue milliseconds; a longer timeout degrades to that.
        var bounded = timeout.TotalMilliseconds > int.MaxValue ? TimeSpan.FromMilliseconds(int.MaxValue) : timeout;
        return _locker.LockOrNullAsync(new LockKey(prefix, key), bounded, token);
    }

    private readonly record struct LockKey(string Prefix, string Name);
}
