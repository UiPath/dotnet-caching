namespace UiPath.Platform.Caching.Hybrid;

public class HybridCache<T> : Cache<T>, IHybridCache<T>
    where T : class
{
    public HybridCache(IHybridCache hybrid)
        : base(hybrid)
    {
    }
}
