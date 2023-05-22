namespace UiPath.Platform.Caching.Memory;

public sealed class MemCache<T> : Cache<T>, IMemCache<T>
    where T : class
{
    public MemCache(IMemCache memCache)
        : base(memCache)
    {
    }
}
