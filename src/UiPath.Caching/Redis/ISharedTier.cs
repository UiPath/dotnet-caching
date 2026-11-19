namespace UiPath.Caching.Redis;

internal interface ISharedTier
{
    void Invalidated(CacheKey key);

    void InvalidatedAll();

    IDisposable DemandMaster();
}
