namespace UiPath.Platform.Caching;

public interface IHybridRegionCache : IRegionCache
{
}

public interface IHybridRegionCache<T> : IRegionCache<T>
    where T : class
{
}
