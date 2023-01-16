namespace UiPath.Platform.Caching.Hybrid;

public class HybridRegionCache<T> : RegionCache<T>, IHybridRegionCache<T>
    where T : class
{
    public HybridRegionCache(IHybridRegionCache cache)
        : base(cache)
    {
    }
}
