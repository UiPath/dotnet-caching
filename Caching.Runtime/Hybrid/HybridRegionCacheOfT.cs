namespace UiPath.Platform.Caching.Hybrid;

public sealed class HybridRegionCache<T> : RegionCache<T>, IHybridRegionCache<T>
    where T : class
{
    public HybridRegionCache(IHybridRegionCache cache)
        : base(cache)
    {
    }
}
