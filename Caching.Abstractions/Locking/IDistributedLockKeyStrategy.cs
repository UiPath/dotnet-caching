namespace UiPath.Platform.Caching.Locking;

public interface IDistributedLockKeyStrategy
{
    string GetLockKey(CacheKey cacheKey);
}
