namespace UiPath.Platform.Caching.Memory;

public sealed class MemRegionCache<T> : RegionCache<T>, IMemRegionCache<T>
    where T : class
{
    public MemRegionCache(IMemRegionCache cache)
        : base(cache)
    {
    }
}
