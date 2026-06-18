namespace UiPath.Caching.Locking;

public interface IDistributedLockKeyStrategy
{
    string GetLockKey(CacheKey cacheKey);
}
