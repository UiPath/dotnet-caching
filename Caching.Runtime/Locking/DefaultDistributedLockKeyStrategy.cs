namespace UiPath.Platform.Caching.Locking;

internal sealed class DefaultDistributedLockKeyStrategy : IDistributedLockKeyStrategy
{
    private const string LockSuffix = "lck";
    private readonly string _suffix;

    public DefaultDistributedLockKeyStrategy(char separator) =>
        _suffix = $"{separator}{LockSuffix}";

    public string GetLockKey(CacheKey cacheKey) =>
        cacheKey.Name + _suffix;
}
