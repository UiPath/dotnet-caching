namespace UiPath.Platform.Caching;

public interface IMemRegionCache : IRegionCache
{
}

public interface IMemRegionCache<T> : IRegionCache<T>
    where T : class
{
}
